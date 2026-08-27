"""Expose Streamable HTTP while keeping MCStudio's native SSE bridge intact."""

from __future__ import annotations

import argparse
import asyncio
import json
from contextlib import AsyncExitStack, asynccontextmanager
from dataclasses import dataclass
from typing import Any

import uvicorn
from mcp import ClientSession
from mcp.client.sse import sse_client
from mcp.server.mcpserver import MCPServer
from starlette.applications import Starlette


_CALL_TIMEOUT_SECONDS = 30
_RETRYABLE_TOOLS = frozenset(
    {"status", "list", "reload", "projects", "client_logs", "deploy_logs", "live_logs", "logs"}
)


@dataclass
class _BridgeRequest:
    tool: str
    arguments: dict[str, Any] | None
    reply: asyncio.Future[str]


class LegacyBridgeClient:
    def __init__(self, url: str) -> None:
        self.url = url
        self.requests: asyncio.Queue[_BridgeRequest | None] | None = None
        self.worker: asyncio.Task[None] | None = None

    async def start(self) -> None:
        if self.worker is not None and not self.worker.done():
            return
        self.requests = asyncio.Queue()
        self.worker = asyncio.create_task(self._run(), name="allurix-legacy-sse")

    async def close(self) -> None:
        worker = self.worker
        requests = self.requests
        self.worker = None
        self.requests = None
        if worker is None:
            return
        if requests is not None:
            await requests.put(None)
        await worker

    async def call(self, tool: str, arguments: dict[str, Any] | None) -> str:
        await self.start()
        if self.requests is None:
            raise RuntimeError("Legacy bridge worker unavailable")
        reply: asyncio.Future[str] = asyncio.get_running_loop().create_future()
        await self.requests.put(_BridgeRequest(tool, arguments, reply))
        return await reply

    async def _run(self) -> None:
        stack: AsyncExitStack | None = None
        session: ClientSession | None = None

        async def disconnect() -> None:
            nonlocal stack, session
            if stack is not None:
                await stack.aclose()
            stack = None
            session = None

        try:
            while self.requests is not None:
                request = await self.requests.get()
                if request is None:
                    break
                try:
                    attempts = 2 if request.tool in _RETRYABLE_TOOLS else 1
                    for attempt in range(attempts):
                        try:
                            if session is None:
                                stack = AsyncExitStack()
                                read_stream, write_stream = await stack.enter_async_context(sse_client(self.url))
                                session = await stack.enter_async_context(ClientSession(read_stream, write_stream))
                                await session.initialize()
                            result = await asyncio.wait_for(
                                session.call_tool(
                                    "allurix_bridge",
                                    {"tool": request.tool, "arguments": request.arguments or {}},
                                ),
                                timeout=_CALL_TIMEOUT_SECONDS,
                            )
                            text_parts = [item.text for item in result.content if hasattr(item, "text")]
                            if getattr(result, "is_error", getattr(result, "isError", False)):
                                raise RuntimeError("\n".join(text_parts) or "MCStudio MCP tool call failed")
                            output = "\n".join(text_parts) if text_parts else json.dumps(
                                result.model_dump(mode="json"), ensure_ascii=False
                            )
                            if not request.reply.done():
                                request.reply.set_result(output)
                            break
                        except Exception:
                            await disconnect()
                            if attempt + 1 == attempts:
                                raise
                except Exception as exc:
                    if not request.reply.done():
                        request.reply.set_exception(RuntimeError("MCStudio MCP request failed: " + str(exc)))
        finally:
            await disconnect()


def build_app(upstream_url: str) -> Starlette:
    bridge = LegacyBridgeClient(upstream_url)
    server = MCPServer(
        name="allurix-bridge",
        version="1.0.0",
        instructions="Proxy to the injected MCStudio Allurix Bridge.",
    )

    async def allurix_bridge(tool: str, arguments: dict[str, Any] | None = None) -> str:
        return await bridge.call(tool, arguments)

    server.add_tool(
        allurix_bridge,
        name="allurix_bridge",
        description="Route a command to the injected MCStudio Allurix Bridge.",
        structured_output=False,
    )

    streamable = server.streamable_http_app(streamable_http_path="/mcp", host="127.0.0.1")
    legacy_sse = server.sse_app(sse_path="/sse", message_path="/messages/", host="127.0.0.1")

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
        routes=[*streamable.routes, *legacy_sse.routes],
        lifespan=lifespan,
    )


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=19132)
    parser.add_argument("--upstream", default="http://127.0.0.1:19131/sse")
    args = parser.parse_args()
    uvicorn.run(build_app(args.upstream), host=args.host, port=args.port, log_level="warning")


if __name__ == "__main__":
    main()
