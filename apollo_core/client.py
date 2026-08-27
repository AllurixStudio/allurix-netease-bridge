"""Low-level Apollo request signing, transport, and response parsing."""

import hashlib
import json
from collections.abc import Mapping
from urllib import error, request


DEFAULT_API_BASE_URL = "http://obt-apicenter.mc.netease.com:30000"
LOG_FETCH_PATH = "/deploy/log-fetch"
ACT_LOGS_PATH = "/act/get-act-logs"


class ApolloRejectedError(RuntimeError):
    def __init__(self, code):
        self.code = str(code)
        super().__init__("Apollo rejected the request with code {}.".format(self.code))


class ApolloTransportError(RuntimeError):
    """Apollo could not be reached or returned a transport-level failure."""


class ApolloProtocolError(RuntimeError):
    """Apollo returned a response that did not match the expected contract."""


def build_signing_key(path, user, apollo_id):
    signing_input = path + user + str(apollo_id)
    return hashlib.md5(signing_input.encode("utf-8")).hexdigest().upper()


def _post_json(opener, api_base_url, path, apollo_id, credential, body):
    headers = {
        "Content-Type": "application/json",
        "User-Agent": "MCStudio/1.1.56.4050",
        "apolloid": str(apollo_id),
        "uid": credential["uid"],
        "user": credential["user"],
        "key": build_signing_key(path, credential["user"], apollo_id),
        "md5_token": credential["md5_token"],
        "act_id": credential["act_id"],
        "act_type": credential["act_type"],
    }
    api_request = request.Request(
        api_base_url.rstrip("/") + path,
        data=json.dumps(body, separators=(",", ":")).encode("utf-8"),
        headers=headers,
        method="POST",
    )
    try:
        with opener.open(api_request, timeout=15) as response:
            try:
                return json.loads(response.read().decode("utf-8"))
            except (UnicodeDecodeError, json.JSONDecodeError) as exc:
                raise ApolloProtocolError(
                    "Apollo API returned an invalid JSON response."
                ) from exc
    except error.HTTPError as exc:
        if exc.code in (401, 403):
            raise ApolloRejectedError("http-{}".format(exc.code)) from exc
        raise ApolloTransportError(
            "Apollo API returned HTTP status {}.".format(exc.code)
        ) from exc
    except error.URLError as exc:
        raise ApolloTransportError(
            "Apollo API request failed: {}.".format(exc.reason)
        ) from exc


def invoke_log_fetch(
    opener,
    api_base_url,
    apollo_id,
    server_id,
    lines,
    credential,
):
    return _post_json(
        opener,
        api_base_url,
        LOG_FETCH_PATH,
        apollo_id,
        credential,
        {
            "apolloid": apollo_id,
            "serverid": server_id,
            "offset": -1,
            "len": -lines,
        },
    )


def invoke_deployment_log_fetch(
    opener,
    api_base_url,
    apollo_id,
    credential,
):
    return _post_json(
        opener,
        api_base_url,
        ACT_LOGS_PATH,
        apollo_id,
        credential,
        {"apolloid": apollo_id},
    )


def parse_deployment_log_response(response, apollo_id):
    if not isinstance(response, Mapping):
        raise ApolloProtocolError("Apollo API returned an invalid response object.")
    if response.get("code") != 0:
        raise ApolloRejectedError(response.get("code", "unknown"))
    return {
        "success": True,
        "apollo_id": apollo_id,
        "content": response.get("entity"),
    }


def parse_log_response(response, apollo_id, server_id, requested_lines):
    if not isinstance(response, Mapping):
        raise ApolloProtocolError("Apollo API returned an invalid response object.")
    if response.get("code") != 0:
        raise ApolloRejectedError(response.get("code", "unknown"))

    entity = response.get("entity") or {}
    if not isinstance(entity, Mapping):
        raise ApolloProtocolError("Apollo API returned an invalid log entity.")
    return {
        "success": True,
        "apollo_id": apollo_id,
        "server_id": server_id,
        "requested_lines": requested_lines,
        "returned_length": entity.get("len"),
        "offset": entity.get("offset"),
        "content": entity.get("content"),
    }