"""Read-only Apollo server log operations."""

from urllib import request

from .client import (
    ApolloRejectedError,
    DEFAULT_API_BASE_URL,
    invoke_deployment_log_fetch,
    invoke_log_fetch,
    parse_deployment_log_response,
    parse_log_response,
)
from .session import find_mcstudio_process_id, get_credential_candidates


def _fetch_with_candidates(
    opener,
    credentials,
    api_base_url,
    apollo_id,
    server_id,
    lines,
):
    failure_codes = []
    for index, credential in enumerate(credentials):
        try:
            response = invoke_log_fetch(
                opener,
                api_base_url,
                apollo_id,
                server_id,
                lines,
                credential,
            )
            result = parse_log_response(response, apollo_id, server_id, lines)
        except ApolloRejectedError as exc:
            failure_codes.append(exc.code)
            continue

        remaining_credentials = [credential] + credentials[index + 1 :]
        return result, remaining_credentials

    observed = ", ".join(sorted(set(failure_codes))) or "none"
    raise RuntimeError(
        "None of the MCStudio session candidates was accepted by Apollo. "
        "Observed result codes: {}. If MCStudio was recently reauthenticated, "
        "open the target Apollo project page once and retry.".format(observed)
    )


def fetch_logs_many(
    apollo_id,
    server_ids,
    lines=200,
    api_base_url=DEFAULT_API_BASE_URL,
    mcstudio_process_id=0,
):
    """Fetch multiple server nodes while discovering MCStudio credentials once."""
    process_id = find_mcstudio_process_id(mcstudio_process_id)
    credentials = get_credential_candidates(process_id, apollo_id)
    opener = request.build_opener(request.ProxyHandler({}))
    results = []

    for server_id in server_ids:
        result, credentials = _fetch_with_candidates(
            opener,
            credentials,
            api_base_url,
            apollo_id,
            server_id,
            lines,
        )
        results.append(result)
    return results


def fetch_logs(
    apollo_id,
    server_id,
    lines=200,
    api_base_url=DEFAULT_API_BASE_URL,
    mcstudio_process_id=0,
):
    """Return one Apollo log response as a JSON-serializable dictionary."""
    return fetch_logs_many(
        apollo_id=apollo_id,
        server_ids=[server_id],
        lines=lines,
        api_base_url=api_base_url,
        mcstudio_process_id=mcstudio_process_id,
    )[0]


def fetch_deployment_logs(
    apollo_id,
    api_base_url=DEFAULT_API_BASE_URL,
    mcstudio_process_id=0,
):
    """Return Apollo deployment activity logs for one project."""
    process_id = find_mcstudio_process_id(mcstudio_process_id)
    credentials = get_credential_candidates(process_id, apollo_id)
    opener = request.build_opener(request.ProxyHandler({}))
    failure_codes = []

    for credential in credentials:
        try:
            response = invoke_deployment_log_fetch(
                opener,
                api_base_url,
                apollo_id,
                credential,
            )
            return parse_deployment_log_response(response, apollo_id)
        except ApolloRejectedError as exc:
            failure_codes.append(exc.code)

    observed = ", ".join(sorted(set(failure_codes))) or "none"
    raise RuntimeError(
        "None of the MCStudio session candidates was accepted by Apollo. "
        "Observed result codes: {}. If MCStudio was recently reauthenticated, "
        "open the target Apollo project page once and retry.".format(observed)
    )
