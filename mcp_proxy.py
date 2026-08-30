"""Single-process MCP proxy for MCStudio's injected Allurix Bridge."""

from __future__ import annotations

import argparse
import asyncio
import json
import logging
import time
from contextlib import AsyncExitStack, asynccontextmanager
from dataclasses import dataclass
from typing import Any

import uvicorn
from mcp import ClientSession
from mcp.client.sse import sse_client
from mcp.server.mcpserver import MCPServer
from starlette.applications import Starlette
from starlette.responses import JSONResponse
from starlette.routing import Route


_UPSTREAM_TOOL = "allurix_bridge"
_CONNECT_TIMEOUT_SECONDS = 5
_CALL_TIMEOUT_SECONDS = 30
_READ_WAIT_SECONDS = 60
_HEALTH_INTERVAL_SECONDS = 5
_RETRY_DELAYS = (0.5, 1.0, 2.0, 5.0)
_READ_ONLY_TOOLS = frozenset({
    "status",
    "list",
    "projects",
    "client_logs",
    "deploy_logs",
    "live_logs",
    "logs",
})

_STATE_STARTING = "starting"
_STATE_WAITING_FOR_SSE = "waiting_for_sse"
_STATE_WAITING_FOR_BRIDGE = "waiting_for_bridge"
_STATE_READY = "ready"
_STATE_SUSPENDED = "suspended"
_STATE_STOPPING = "stopping"
_STATE_STOPPED = "stopped"
_PROXY_SUSPEND = "_proxy_suspend"
_PROXY_RESUME = "_proxy_resume"


@dataclass(frozen=True)
class _PublishedConnection:
    generation: int
    session: ClientSession
    stack: AsyncExitStack
    tool_names: tuple[str, ...]


@dataclass
class _BridgeRequest:
    tool: str
    arguments: dict[str, Any] | None
    reply: asyncio.Future[str]
    deadline: float


class BridgeSupervisor:
    """Own the only reconnecting session to MCStudio's native SSE server."""

    def __init__(self, upstream_url: str) -> None:
        self.upstream_url = upstream_url
        self.state = _STATE_STARTING
        self.generation = 0
        self.last_error: str | None = None
        self.reconnect_reason: str | None = None
        self.upstream_tool_names: tuple[str, ...] = ()

        self._requests: asyncio.Queue[_BridgeRequest | None] = asyncio.Queue()
        self._stop = asyncio.Event()
        self._ready = asyncio.Event()
        self._worker: asyncio.Task[None] | None = None
        self._connection: _PublishedConnection | None = None
        self._retry_index = 0
        self._suspended_until = 0.0

    async def start(self) -> None:
        if self._worker is not None and not self._worker.done():
            return
        self._stop.clear()
        self._ready.clear()
        self.state = _STATE_WAITING_FOR_SSE
        self._worker = asyncio.create_task(self._run(), name="allurix-upstream-supervisor")

    async def close(self) -> None:
        if self.state == _STATE_STOPPED:
            return
        self.state = _STATE_STOPPING
        self._stop.set()
        self._fail_pending("proxy_stopping", "Allurix MCP Proxy 正在停止")
        if self._worker is None:
            await self._disconnect("proxy stopping")
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
            if tool not in _READ_ONLY_TOOLS:
                return self._not_loaded()
            try:
                await asyncio.wait_for(self._ready.wait(), timeout=_READ_WAIT_SECONDS)
            except asyncio.TimeoutError:
                return self._not_loaded("等待 MCStudio Allurix Bridge 超时")

        reply: asyncio.Future[str] = asyncio.get_running_loop().create_future()
        request = _BridgeRequest(
            tool,
            arguments,
            reply,
            time.monotonic() + _CALL_TIMEOUT_SECONDS,
        )
        await self._requests.put(request)
        if self._stop.is_set() and not reply.done():
            reply.set_result(self._error("proxy_stopping", "Allurix MCP Proxy 正在停止"))
        try:
            return await reply
        except asyncio.CancelledError:
            reply.cancel()
            raise

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
                        self.state = _STATE_WAITING_FOR_SSE
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
                        self._requests.get(), timeout=_HEALTH_INTERVAL_SECONDS
                    )
                except asyncio.TimeoutError:
                    await self._check_health()
                    continue
                if request is None:
                    break
                await self._execute(request)
        finally:
            self.state = _STATE_STOPPING
            await self._disconnect("proxy stopped")
            self._fail_pending("proxy_stopping", "Allurix MCP Proxy 已停止")
            self.state = _STATE_STOPPED

    async def _connect_once(self) -> bool:
        self.state = _STATE_WAITING_FOR_SSE
        stack = AsyncExitStack()
        try:
            read_stream, write_stream = await asyncio.wait_for(
                stack.enter_async_context(sse_client(self.upstream_url)),
                timeout=_CONNECT_TIMEOUT_SECONDS,
            )
            session = await stack.enter_async_context(ClientSession(read_stream, write_stream))
            await asyncio.wait_for(session.initialize(), timeout=_CONNECT_TIMEOUT_SECONDS)

            self.state = _STATE_WAITING_FOR_BRIDGE
            response = await asyncio.wait_for(
                session.list_tools(), timeout=_CONNECT_TIMEOUT_SECONDS
            )
            names = tuple(sorted(tool.name for tool in response.tools))
            if _UPSTREAM_TOOL not in names:
                self._record_failure(
                    _STATE_WAITING_FOR_BRIDGE,
                    "tool_missing",
                    "MCStudio MCP 已连接，但 allurix_bridge 尚未加载",
                )
                await stack.aclose()
                return False

            generation = self.generation + 1
            connection = _PublishedConnection(generation, session, stack, names)

            # Publish one immutable connection snapshot before announcing readiness.
            self._connection = connection
            self.generation = generation
            self.upstream_tool_names = names
            self.last_error = None
            self.reconnect_reason = None
            self.state = _STATE_READY
            self._retry_index = 0
            self._ready.set()
            logging.getLogger(__name__).info(
                "MCStudio bridge ready: generation=%s tools=%s",
                generation,
                ",".join(names),
            )
            return True
        except asyncio.CancelledError:
            await stack.aclose()
            raise
        except Exception as exc:
            await stack.aclose()
            self._record_exception(_STATE_WAITING_FOR_SSE, "connect_failed", exc)
            return False

    async def _check_health(self) -> None:
        connection = self._connection
        if connection is None:
            self._ready.clear()
            self.state = _STATE_WAITING_FOR_SSE
            return
        try:
            response = await asyncio.wait_for(
                connection.session.list_tools(), timeout=_CONNECT_TIMEOUT_SECONDS
            )
            names = tuple(sorted(tool.name for tool in response.tools))
            if _UPSTREAM_TOOL not in names:
                await self._disconnect("allurix_bridge disappeared")
                self._record_failure(
                    _STATE_WAITING_FOR_BRIDGE,
                    "tool_missing",
                    "MCStudio MCP 中的 allurix_bridge 已消失",
                )
                return
            self.upstream_tool_names = names
        except Exception as exc:
            await self._disconnect("health check failed")
            self._record_exception(_STATE_WAITING_FOR_SSE, "health_check_failed", exc)

    async def _execute(self, request: _BridgeRequest) -> None:
        if request.reply.cancelled():
            return
        if time.monotonic() >= request.deadline:
            self._set_reply(
                request,
                self._error("request_expired", "请求在发送到 MCStudio 前已超时"),
            )
            return
        if request.tool == _PROXY_SUSPEND:
            seconds = max(1.0, min(float((request.arguments or {}).get("seconds", 60)), 300.0))
            self._suspended_until = time.monotonic() + seconds
            await self._disconnect("proxy suspended")
            self.state = _STATE_SUSPENDED
            self._set_reply(request, json.dumps({"ok": True, "state": self.state}, ensure_ascii=False))
            return
        if request.tool == _PROXY_RESUME:
            self._suspended_until = 0.0
            await self._disconnect("proxy resumed")
            self.state = _STATE_WAITING_FOR_SSE
            self._set_reply(request, json.dumps({"ok": True, "state": self.state}, ensure_ascii=False))
            return

        connection = self._connection
        if connection is None or not self._ready.is_set():
            self._set_reply(request, self._not_loaded())
            return
        try:
            result = await asyncio.wait_for(
                connection.session.call_tool(
                    _UPSTREAM_TOOL,
                    {"tool": request.tool, "arguments": request.arguments or {}},
                ),
                timeout=_CALL_TIMEOUT_SECONDS,
            )
            self._set_reply(request, self._serialize_result(result))
        except Exception as exc:
            await self._disconnect("tool call failed")
            self._record_exception(_STATE_WAITING_FOR_SSE, "tool_call_failed", exc)
            self._set_reply(
                request,
                self._error("upstream_disconnected", "MCStudio Allurix Bridge 连接已断开"),
            )

    async def _wait_before_retry(self) -> None:
        delay = _RETRY_DELAYS[self._retry_index]
        self._retry_index = min(self._retry_index + 1, len(_RETRY_DELAYS) - 1)
        logging.getLogger(__name__).info(
            "Bridge reconnect pending: state=%s retry_in=%.1fs reason=%s error=%s",
            self.state,
            delay,
            self.reconnect_reason,
            self.last_error,
        )
        try:
            await asyncio.wait_for(self._stop.wait(), timeout=delay)
        except asyncio.TimeoutError:
            pass

    async def _disconnect(self, reason: str) -> None:
        connection = self._connection
        self._connection = None
        self._ready.clear()
        self.upstream_tool_names = ()
        self.reconnect_reason = reason
        if connection is not None:
            try:
                await connection.stack.aclose()
            except Exception:
                logging.getLogger(__name__).debug(
                    "Upstream SSE close failed", exc_info=True
                )

    def _record_exception(self, state: str, reason: str, exc: Exception) -> None:
        self._record_failure(state, reason, "%s: %s" % (type(exc).__name__, exc))

    def _record_failure(self, state: str, reason: str, message: str) -> None:
        changed = message != self.last_error or reason != self.reconnect_reason
        self._ready.clear()
        self.state = state
        self.reconnect_reason = reason
        self.last_error = message
        logger = logging.getLogger(__name__)
        if changed:
            logger.warning(
                "Bridge unavailable: state=%s generation=%s reason=%s error=%s",
                state,
                self.generation,
                reason,
                message,
            )
        else:
            logger.debug("Bridge still unavailable: %s", message)

    def _not_loaded(self, message: str | None = None) -> str:
        if message is None:
            if self.state == _STATE_WAITING_FOR_BRIDGE:
                message = "MCStudio MCP 已连接，Allurix Bridge 尚未加载"
            elif self.state == _STATE_SUSPENDED:
                message = "Allurix MCP Proxy 已暂停上游连接"
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
                "reconnect_reason": self.reconnect_reason,
                "last_error": self.last_error,
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


def build_mcp_server(bridge: BridgeSupervisor, name: str = "allurix-bridge") -> MCPServer:
    server = MCPServer(
        name=name,
        version="1.1.0",
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
            "Route a command to MCStudio. Read calls wait for reconnect; write calls fail without replay."
        ),
        structured_output=False,
    )
    return server


def build_app(
    bridge: BridgeSupervisor,
    host: str = "127.0.0.1",
    mcp_path: str = "/mcp",
    sse_path: str = "/sse",
) -> Starlette:
    server = build_mcp_server(bridge)
    streamable = server.streamable_http_app(streamable_http_path=mcp_path, host=host)
    legacy = server.sse_app(sse_path=sse_path, message_path="/messages/", host=host)

    async def health(request):
        return JSONResponse({
            "service": "allurix-mcp-proxy",
            "version": "1.1.0",
            "state": bridge.state,
            "generation": bridge.generation,
            "last_error": bridge.last_error,
        })

    @asynccontextmanager
    async def lifespan(app: Starlette):
        async with streamable.router.lifespan_context(app):
            await bridge.start()
            try:
                yield
            finally:
                await bridge.close()

    return Starlette(
        debug=False,
        routes=[Route("/healthz", health), *streamable.routes, *legacy.routes],
        lifespan=lifespan,
    )


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--upstream", default="http://127.0.0.1:19131/sse")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=19132)
    parser.add_argument("--mcp-path", default="/mcp")
    parser.add_argument("--sse-path", default="/sse")
    args = parser.parse_args()
    if not args.mcp_path.startswith("/") or not args.sse_path.startswith("/"):
        parser.error("--mcp-path and --sse-path must start with /")
    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s %(levelname)s %(name)s: %(message)s",
    )
    logging.getLogger("httpx2").setLevel(logging.WARNING)
    uvicorn.run(
        build_app(BridgeSupervisor(args.upstream), args.host, args.mcp_path, args.sse_path),
        host=args.host,
        port=args.port,
        log_level="warning",
        access_log=False,
    )


if __name__ == "__main__":
    main()
