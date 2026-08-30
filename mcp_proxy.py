"""Stable MCP proxy between Codex and MCStudio's legacy SSE server."""

from __future__ import annotations

import argparse
import asyncio
import json
import logging
import time
from contextlib import AsyncExitStack
from dataclasses import dataclass
from typing import Any

import uvicorn
from mcp import ClientSession
from mcp.client.sse import sse_client
from mcp.server.mcpserver import MCPServer


_UPSTREAM_TOOL = "allurix_bridge"
_CONNECT_TIMEOUT_SECONDS = 5
_CALL_TIMEOUT_SECONDS = 30
_HEALTH_INTERVAL_SECONDS = 5
_RETRY_DELAYS = (0.5, 1.0, 2.0, 5.0)

_STATE_STARTING = "starting"
_STATE_CONNECTING = "connecting"
_STATE_WAITING_FOR_BRIDGE = "waiting_for_bridge"
_STATE_READY = "ready"
_STATE_SUSPENDED = "suspended"
_STATE_STOPPING = "stopping"
_STATE_STOPPED = "stopped"
_PROXY_SUSPEND = "_proxy_suspend"
_PROXY_RESUME = "_proxy_resume"


@dataclass
class _BridgeRequest:
    tool: str
    arguments: dict[str, Any] | None
    reply: asyncio.Future[str]


class BridgeSupervisor:
    """Own one reconnecting legacy SSE session and serialize upstream calls."""

    def __init__(self, upstream_url: str) -> None:
        self.upstream_url = upstream_url
        self.state = _STATE_STARTING
        self.generation = 0
        self.last_error: str | None = None
        self.upstream_tool_names: tuple[str, ...] = ()

        self._requests: asyncio.Queue[_BridgeRequest | None] = asyncio.Queue()
        self._stop = asyncio.Event()
        self._ready = asyncio.Event()
        self._worker: asyncio.Task[None] | None = None
        self._stack: AsyncExitStack | None = None
        self._session: ClientSession | None = None
        self._retry_index = 0
        self._suspended_until = 0.0

    async def start(self) -> None:
        if self._worker is not None and not self._worker.done():
            return
        self._stop.clear()
        self._ready.clear()
        self.state = _STATE_CONNECTING
        self._worker = asyncio.create_task(self._run(), name="allurix-upstream-supervisor")

    async def close(self) -> None:
        if self.state == _STATE_STOPPED:
            return
        self.state = _STATE_STOPPING
        self._stop.set()
        self._fail_pending("proxy_stopping", "Allurix MCP Proxy 正在停止")
        if self._worker is None:
            await self._disconnect()
            self.state = _STATE_STOPPED
            return
        await self._requests.put(None)
        await self._worker
        self._worker = None

    async def call(self, tool: str, arguments: dict[str, Any] | None = None) -> str:
        await self.start()
        if self.state in (_STATE_STOPPING, _STATE_STOPPED):
            return self._error("proxy_stopping", "Allurix MCP Proxy 正在停止")
        control = tool in (_PROXY_SUSPEND, _PROXY_RESUME)
        if not control and not self._ready.is_set():
            return self._not_loaded()

        reply: asyncio.Future[str] = asyncio.get_running_loop().create_future()
        await self._requests.put(_BridgeRequest(tool, arguments, reply))
        if self._stop.is_set() and not reply.done():
            reply.set_result(self._error("proxy_stopping", "Allurix MCP Proxy 正在停止"))
        return await reply

    async def _run(self) -> None:
        try:
            while not self._stop.is_set():
                try:
                    request = self._requests.get_nowait()
                except asyncio.QueueEmpty:
                    request = None
                if request is not None:
                    await self._execute(request)
                    continue

                if self.state == _STATE_SUSPENDED:
                    if time.monotonic() >= self._suspended_until:
                        self.state = _STATE_CONNECTING
                        continue
                    try:
                        request = await asyncio.wait_for(self._requests.get(), timeout=1)
                    except asyncio.TimeoutError:
                        continue
                    if request is None:
                        break
                    await self._execute(request)
                    continue

                if not self._ready.is_set():
                    if await self._connect_once():
                        continue
                    await self._wait_before_retry()
                    continue

                try:
                    request = await asyncio.wait_for(
                        self._requests.get(),
                        timeout=_HEALTH_INTERVAL_SECONDS,
                    )
                except asyncio.TimeoutError:
                    await self._check_health()
                    continue

                if request is None:
                    break
                await self._execute(request)
        finally:
            self.state = _STATE_STOPPING
            await self._disconnect()
            self._fail_pending("proxy_stopping", "Allurix MCP Proxy 已停止")
            self.state = _STATE_STOPPED

    async def _connect_once(self) -> bool:
        self.state = _STATE_CONNECTING
        stack = AsyncExitStack()
        try:
            read_stream, write_stream = await asyncio.wait_for(
                stack.enter_async_context(sse_client(self.upstream_url)),
                timeout=_CONNECT_TIMEOUT_SECONDS,
            )
            session = await stack.enter_async_context(ClientSession(read_stream, write_stream))
            await asyncio.wait_for(session.initialize(), timeout=_CONNECT_TIMEOUT_SECONDS)

            self.state = _STATE_WAITING_FOR_BRIDGE
            response = await asyncio.wait_for(session.list_tools(), timeout=_CONNECT_TIMEOUT_SECONDS)
            names = tuple(sorted(tool.name for tool in response.tools))
            self.upstream_tool_names = names
            if _UPSTREAM_TOOL not in names:
                self.last_error = "upstream tool is not loaded"
                await stack.aclose()
                return False

            self._stack = stack
            self._session = session
            self._ready.set()
            self._retry_index = 0
            self.generation += 1
            self.last_error = None
            self.state = _STATE_READY
            logging.getLogger(__name__).info(
                "MCStudio bridge ready: generation=%s tools=%s",
                self.generation,
                ",".join(names),
            )
            return True
        except asyncio.CancelledError:
            await stack.aclose()
            raise
        except Exception as exc:
            self.last_error = "%s: %s" % (type(exc).__name__, exc)
            await stack.aclose()
            self.state = _STATE_CONNECTING
            return False

    async def _check_health(self) -> None:
        session = self._session
        if session is None:
            self._ready.clear()
            self.state = _STATE_CONNECTING
            return
        try:
            response = await asyncio.wait_for(session.list_tools(), timeout=_CONNECT_TIMEOUT_SECONDS)
            names = tuple(sorted(tool.name for tool in response.tools))
            self.upstream_tool_names = names
            if _UPSTREAM_TOOL not in names:
                self.last_error = "upstream tool is not loaded"
                await self._disconnect()
                self.state = _STATE_WAITING_FOR_BRIDGE
        except Exception as exc:
            self.last_error = "%s: %s" % (type(exc).__name__, exc)
            await self._disconnect()
            self.state = _STATE_CONNECTING

    async def _execute(self, request: _BridgeRequest) -> None:
        if request.tool == _PROXY_SUSPEND:
            seconds = max(1.0, min(float((request.arguments or {}).get("seconds", 60)), 300.0))
            self._suspended_until = time.monotonic() + seconds
            await self._disconnect()
            self.state = _STATE_SUSPENDED
            self._set_reply(request, json.dumps({"ok": True, "state": self.state}, ensure_ascii=False))
            return
        if request.tool == _PROXY_RESUME:
            self._suspended_until = 0.0
            await self._disconnect()
            self.state = _STATE_CONNECTING
            self._set_reply(request, json.dumps({"ok": True, "state": self.state}, ensure_ascii=False))
            return

        session = self._session
        if session is None or not self._ready.is_set():
            self._set_reply(request, self._not_loaded())
            return
        try:
            result = await asyncio.wait_for(
                session.call_tool(
                    _UPSTREAM_TOOL,
                    {"tool": request.tool, "arguments": request.arguments or {}},
                ),
                timeout=_CALL_TIMEOUT_SECONDS,
            )
            self._set_reply(request, self._serialize_result(result))
        except Exception as exc:
            self.last_error = "%s: %s" % (type(exc).__name__, exc)
            await self._disconnect()
            self.state = _STATE_CONNECTING
            self._set_reply(
                request,
                self._error("upstream_disconnected", "MCStudio Allurix Bridge 连接已断开"),
            )

    async def _wait_before_retry(self) -> None:
        delay = _RETRY_DELAYS[self._retry_index]
        self._retry_index = min(self._retry_index + 1, len(_RETRY_DELAYS) - 1)
        try:
            await asyncio.wait_for(self._stop.wait(), timeout=delay)
        except asyncio.TimeoutError:
            pass

    async def _disconnect(self) -> None:
        self._ready.clear()
        self._session = None
        self.upstream_tool_names = ()
        if self._stack is not None:
            stack = self._stack
            self._stack = None
            try:
                await stack.aclose()
            except Exception:
                logging.getLogger(__name__).debug("Upstream SSE close failed", exc_info=True)

    def _not_loaded(self) -> str:
        if self.state == _STATE_WAITING_FOR_BRIDGE:
            message = "MCStudio MCP 已连接，Allurix Bridge 尚未加载"
        else:
            message = "MCStudio MCP 尚未连接"
        return self._error("bridge_not_loaded", message)

    def _error(self, code: str, message: str) -> str:
        return json.dumps(
            {
                "ok": False,
                "error": code,
                "state": self.state,
                "generation": self.generation,
                "message": message,
            },
            ensure_ascii=False,
        )

    @staticmethod
    def _serialize_result(result: Any) -> str:
        text_parts = [item.text for item in result.content if hasattr(item, "text")]
        if getattr(result, "is_error", getattr(result, "isError", False)):
            return json.dumps(
                {
                    "ok": False,
                    "error": "upstream_tool_error",
                    "message": "\n".join(text_parts),
                },
                ensure_ascii=False,
            )
        if text_parts:
            return "\n".join(text_parts)
        return json.dumps(result.model_dump(mode="json"), ensure_ascii=False)

    @staticmethod
    def _set_reply(request: _BridgeRequest, value: str) -> None:
        if not request.reply.done():
            request.reply.set_result(value)

    def _fail_pending(self, code: str, message: str) -> None:
        value = self._error(code, message)
        while True:
            try:
                request = self._requests.get_nowait()
            except asyncio.QueueEmpty:
                return
            if request is not None:
                self._set_reply(request, value)


def build_mcp_server(bridge: BridgeSupervisor, name: str) -> MCPServer:
    server = MCPServer(
        name=name,
        version="1.0.0",
        instructions=(
            "Stable Allurix Bridge proxy. The allurix_bridge tool remains available while MCStudio reconnects."
        ),
    )

    async def allurix_bridge(tool: str, arguments: dict[str, Any] | None = None) -> str:
        return await bridge.call(tool, arguments)

    server.add_tool(
        allurix_bridge,
        name=_UPSTREAM_TOOL,
        description=(
            "Route a command to MCStudio. Returns bridge_not_loaded until the injected Allurix Bridge is ready."
        ),
        structured_output=False,
    )
    return server


def build_sse_server(
    bridge: BridgeSupervisor,
    host: str,
    port: int,
    path: str,
) -> uvicorn.Server:
    server = build_mcp_server(bridge, "allurix-bridge-sse")
    app = server.sse_app(sse_path=path, message_path="/messages/", host=host)
    config = uvicorn.Config(
        app,
        host=host,
        port=port,
        log_level="warning",
        access_log=False,
    )
    return uvicorn.Server(config)


class SseEndpoint:
    """Keep the local SSE endpoint alive without owning stdio."""

    def __init__(self, bridge: BridgeSupervisor, host: str, port: int, path: str) -> None:
        self.bridge = bridge
        self.host = host
        self.port = port
        self.path = path
        self._stop = asyncio.Event()
        self._worker: asyncio.Task[None] | None = None
        self._server: uvicorn.Server | None = None
        self._retry_index = 0

    async def start(self) -> None:
        if self._worker is not None and not self._worker.done():
            return
        self._stop.clear()
        self._worker = asyncio.create_task(self._run(), name="allurix-sse")

    async def close(self) -> None:
        self._stop.set()
        if self._server is not None:
            self._server.should_exit = True
        if self._worker is not None:
            await self._worker
        self._worker = None

    async def _run(self) -> None:
        while not self._stop.is_set():
            try:
                server = build_sse_server(self.bridge, self.host, self.port, self.path)
                self._server = server
                await server.serve()
                self._retry_index = 0
            except asyncio.CancelledError:
                raise
            except BaseException as exc:
                logging.getLogger(__name__).error(
                    "SSE endpoint failed: http://%s:%s%s (%s: %s)",
                    self.host,
                    self.port,
                    self.path,
                    type(exc).__name__,
                    exc,
                )
            finally:
                self._server = None

            if self._stop.is_set():
                break
            delay = _RETRY_DELAYS[self._retry_index]
            self._retry_index = min(self._retry_index + 1, len(_RETRY_DELAYS) - 1)
            try:
                await asyncio.wait_for(self._stop.wait(), timeout=delay)
            except asyncio.TimeoutError:
                pass


async def run_proxy(
    upstream_url: str,
    sse_host: str,
    sse_port: int,
    sse_path: str,
) -> None:
    bridge = BridgeSupervisor(upstream_url)
    stdio_server = build_mcp_server(bridge, "allurix-bridge")
    sse_endpoint = SseEndpoint(bridge, sse_host, sse_port, sse_path)
    await bridge.start()
    await sse_endpoint.start()
    try:
        await stdio_server.run_stdio_async()
    finally:
        await sse_endpoint.close()
        await bridge.close()


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--upstream", default="http://127.0.0.1:19131/sse")
    parser.add_argument("--sse-host", default="127.0.0.1")
    parser.add_argument("--sse-port", type=int, default=19132)
    parser.add_argument("--sse-path", default="/sse")
    args = parser.parse_args()
    if not args.sse_path.startswith("/"):
        parser.error("--sse-path must start with /")
    logging.basicConfig(level=logging.WARNING)
    asyncio.run(
        run_proxy(
            args.upstream,
            args.sse_host,
            args.sse_port,
            args.sse_path,
        )
    )


if __name__ == "__main__":
    main()
