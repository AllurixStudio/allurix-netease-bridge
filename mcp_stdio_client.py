"""Codex stdio adapter for the singleton Allurix MCP proxy."""

from __future__ import annotations

import argparse
import asyncio
import json
import msvcrt
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import urllib.parse
from pathlib import Path
from typing import Any

from mcp import ClientSession
from mcp.client.streamable_http import streamable_http_client
from mcp.server.mcpserver import MCPServer


_DEFAULT_PROXY_URL = "http://127.0.0.1:19132/mcp"
_DEFAULT_UPSTREAM_URL = "http://127.0.0.1:19131/sse"
_START_TIMEOUT_SECONDS = 15
_CALL_TIMEOUT_SECONDS = 110


def _endpoint_ready(url: str) -> bool:
    target = urllib.parse.urlsplit(url)
    health_url = urllib.parse.urlunsplit(
        (target.scheme, target.netloc, "/healthz", "", "")
    )
    request = urllib.request.Request(health_url, headers={"Accept": "application/json"})
    try:
        with urllib.request.urlopen(request, timeout=1) as response:
            payload = json.loads(response.read().decode("utf-8"))
            return response.status == 200 and payload.get("service") == "allurix-mcp-proxy"
    except (OSError, ValueError, urllib.error.HTTPError):
        return False


def _port_open(host: str, port: int) -> bool:
    try:
        with socket.create_connection((host, port), timeout=1):
            return True
    except OSError:
        return False


def _lock_file(timeout: float, port: int):
    path = Path(tempfile.gettempdir()) / ("allurix_mcp_proxy_%s.lock" % port)
    stream = path.open("a+b")
    if path.stat().st_size == 0:
        stream.write(b"0")
        stream.flush()
    deadline = time.monotonic() + timeout
    while True:
        try:
            stream.seek(0)
            msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
            return stream
        except OSError:
            if time.monotonic() >= deadline:
                stream.close()
                return None
            time.sleep(0.1)


def _unlock_file(stream) -> None:
    try:
        stream.seek(0)
        msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)
    finally:
        stream.close()


def ensure_proxy(proxy_url: str, daemon_script: Path, upstream_url: str) -> bool:
    if _endpoint_ready(proxy_url):
        return True
    target = urllib.parse.urlsplit(proxy_url)
    host = target.hostname or "127.0.0.1"
    port = target.port or 19132
    mcp_path = target.path or "/mcp"
    lock = _lock_file(_START_TIMEOUT_SECONDS, port)
    if lock is None:
        return _endpoint_ready(proxy_url)
    try:
        if _endpoint_ready(proxy_url):
            return True
        if _port_open(host, port):
            return False
        log_path = Path(tempfile.gettempdir()) / "allurix_mcp_proxy.log"
        log = log_path.open("ab")
        creation_flags = getattr(subprocess, "CREATE_NO_WINDOW", 0) | getattr(
            subprocess, "DETACHED_PROCESS", 0
        )
        subprocess.Popen(
            [
                sys.executable,
                str(daemon_script),
                "--host",
                host,
                "--port",
                str(port),
                "--mcp-path",
                mcp_path,
                "--sse-path",
                "/sse",
                "--upstream",
                upstream_url,
            ],
            stdin=subprocess.DEVNULL,
            stdout=log,
            stderr=subprocess.STDOUT,
            cwd=str(daemon_script.parent),
            close_fds=True,
            creationflags=creation_flags,
        )
        log.close()
        deadline = time.monotonic() + _START_TIMEOUT_SECONDS
        while time.monotonic() < deadline:
            if _endpoint_ready(proxy_url):
                return True
            time.sleep(0.1)
        return False
    finally:
        _unlock_file(lock)


async def _forward(proxy_url: str, tool: str, arguments: dict[str, Any] | None) -> str:
    try:
        async with streamable_http_client(proxy_url) as streams:
            async with ClientSession(*streams) as session:
                await asyncio.wait_for(session.initialize(), timeout=10)
                result = await asyncio.wait_for(
                    session.call_tool(
                        "allurix_bridge",
                        {"tool": tool, "arguments": arguments or {}},
                    ),
                    timeout=_CALL_TIMEOUT_SECONDS,
                )
                text_parts = [item.text for item in result.content if hasattr(item, "text")]
                if text_parts:
                    return "\n".join(text_parts)
                return json.dumps(result.model_dump(mode="json"), ensure_ascii=False)
    except Exception as exc:
        return json.dumps(
            {
                "ok": False,
                "error": "proxy_unavailable",
                "message": "%s: %s" % (type(exc).__name__, exc),
            },
            ensure_ascii=False,
        )


async def run_stdio(proxy_url: str, daemon_script: Path, upstream_url: str) -> None:
    server = MCPServer(
        name="allurix-bridge",
        version="1.1.0",
        instructions="Stdio adapter for the singleton Allurix MCP proxy.",
    )

    async def allurix_bridge(tool: str, arguments: dict[str, Any] | None = None) -> str:
        ready = await asyncio.to_thread(
            ensure_proxy, proxy_url, daemon_script, upstream_url
        )
        if not ready:
            return json.dumps(
                {
                    "ok": False,
                    "error": "proxy_not_ready",
                    "message": "Singleton Allurix MCP proxy did not start within 15 seconds.",
                },
                ensure_ascii=False,
            )
        return await _forward(proxy_url, tool, arguments)

    server.add_tool(
        allurix_bridge,
        name="allurix_bridge",
        description="Route a command through the singleton Allurix MCP proxy.",
        structured_output=False,
    )
    bootstrap = asyncio.create_task(
        asyncio.to_thread(ensure_proxy, proxy_url, daemon_script, upstream_url)
    )
    try:
        await server.run_stdio_async()
    finally:
        await asyncio.gather(bootstrap, return_exceptions=True)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--proxy-url", default=_DEFAULT_PROXY_URL)
    parser.add_argument("--upstream", default=_DEFAULT_UPSTREAM_URL)
    parser.add_argument(
        "--daemon-script",
        type=Path,
        default=Path(__file__).with_name("mcp_proxy.py"),
    )
    args = parser.parse_args()
    asyncio.run(run_stdio(args.proxy_url, args.daemon_script.resolve(), args.upstream))


if __name__ == "__main__":
    main()
