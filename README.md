# Allurix MCStudio MCP Bridge

A Windows-only local bridge that exposes MCStudio Apollo projects, logs, and
operations through one local MCP proxy shared by Codex and the Web Manager.

Allurix runs inside MCStudio and uses MCStudio's loaded Apollo project model.
MCP clients do not receive Apollo credentials and do not replay authenticated
Apollo HTTP requests.

## Architecture

```text
Codex
  -> lightweight mcp_stdio_client.py per task
  -> Streamable HTTP at 127.0.0.1:19132/mcp
  -> stable shared allurix_bridge tool

Web Manager
  -> legacy SSE at 127.0.0.1:19132/sse

mcp_proxy.py
  -> one shared BridgeSupervisor and generation
  -> legacy SSE client to 127.0.0.1:19131/sse
  -> injected MCStudio allurix_bridge
  -> tool DLLs in mcstudio_bridge/bin/tools
  -> MCStudio Apollo project state
```

`BRegister.dll` is injected into the MCStudio process that owns port `19131`.
It stops the native MCP server, registers `allurix_bridge`, and starts the
native server again. `start-allurix-bridge.bat` starts one `mcp_proxy.py`
service if port `19132` is not already listening. The service exposes
Streamable HTTP on `/mcp`, keeps `/sse` for Web Manager compatibility, and
connects to the native legacy SSE server on `19131/sse` in the background.
When Codex starts first, `mcp_stdio_client.py` uses a cross-process lock to
start the same singleton service automatically. Per-task stdio adapters never
connect to `19131` and never own bridge state.

The supervisor states are `starting`, `waiting_for_sse`,
`waiting_for_bridge`, `ready`, `suspended`, `stopping`, and `stopped`.
Read-only calls wait up to 60 seconds for recovery; state-changing calls fail
immediately and are never replayed. Reconnect delays are 0.5, 1, 2, then 5
seconds. Every client observes the same generation, session, tool cache, and
fixed `allurix_bridge` tool.

Apollo HTTP is not an MCP fallback. All MCP operations go through the injected
bridge and MCStudio's in-process state.

## Requirements

- Windows and MCStudio with its native MCP server enabled.
- Python 3.10 or newer. A virtual environment is optional.
- Visual Studio Build Tools with the x86 C++/CLI toolchain.
- MCStudio's `mcp_csharp_bridge.dll`.
- Administrator access if Windows denies opening the MCStudio process for DLL
  injection.

The scripts default to `D:\MCStudio`. Override `MCSTUDIO_DIR` and
`MCSTUDIO_EXE` when MCStudio is installed elsewhere.

## Install

Install the Python package and MCP dependency:

```powershell
py -3 -m pip install -e .
```

Build the x86 bridge from a Visual Studio Developer Command Prompt:

```bat
set MCSTUDIO_DIR=D:\MCStudio
build-bridge.bat
```

Build output is written to the ignored `mcstudio_bridge\bin` directory.

## Start and inject

Run:

```bat
set MCSTUDIO_EXE=D:\MCStudio\MCStudio.exe
start-allurix-bridge.bat
```

The script performs this sequence:

1. Starts the singleton MCP proxy on port `19132` if needed.
2. Starts MCStudio if it is not already running.
3. Waits for MCStudio's native MCP server on port `19131`.
4. Finds the MCStudio process that owns port `19131`.
5. Injects `BRegister.dll` into that exact process.
6. Waits for the bootstrap to register `allurix_bridge` and restart native MCP.

After changing `AllurixBridge.cs`, `BRegister.cpp`, or the injector, restart
MCStudio before rebuilding and injecting. Windows keeps those loaded binaries
locked. Tool DLLs are loaded from bytes and can be refreshed with the bridge's
`reload` command after replacement.

## Connect Codex

Register the lightweight stdio adapter:

```powershell
codex mcp add allurix-bridge -- C:\Python314\python.exe D:\_Development\_Nightbreak\allurix-netease-mcp\mcp_stdio_client.py --proxy-url http://127.0.0.1:19132/mcp
codex mcp list
```

Restart or open a new Codex task after adding the server. The MCP tool exposed
to Codex is named `allurix_bridge`.

Equivalent `~/.codex/config.toml` configuration:

```toml
[mcp_servers.allurix-bridge]
command = "C:\\Python314\\python.exe"
args = [
  "D:\\_Development\\_Nightbreak\\allurix-netease-mcp\\mcp_stdio_client.py",
  "--proxy-url", "http://127.0.0.1:19132/mcp"
]
startup_timeout_sec = 10
tool_timeout_sec = 120
```

`127.0.0.1:19132/mcp` is the Codex Streamable HTTP endpoint.
`127.0.0.1:19132/sse` remains available for legacy clients. Port `19131` is
only the proxy's MCStudio upstream and must not be configured as a Codex MCP
server.

## Call shape

All functionality is routed through one MCP tool:

```json
{
  "name": "allurix_bridge",
  "arguments": {
    "tool": "status",
    "arguments": {}
  }
}
```

Always call `projects` first and use the returned project and node IDs. Do not
hardcode an Apollo project ID or assume that lobby/game node counts are fixed.

## Commands and tools

| `tool` | Arguments | Behaviour |
| --- | --- | --- |
| `status` | `{}` | Reports native MCP status, port, loaded tool count, tool directory, and DLL load errors. |
| `list` | `{}` | Lists every loaded bridge tool. |
| `reload` | `{}` | Reloads tool DLLs from disk without reinjecting the main bridge. |
| `projects` | `{}` | Lists Apollo projects and all currently known lobby, game, master, service, and proxy nodes. |
| `logs` | `apollo_id`, `server_id`, optional `lines`, `offset` | Reads Apollo server logs. Use `lines: -200, offset: -1` for the latest window. |
| `client_logs` | optional `tail_lines` | Reads the latest 1-1000 lines from MCStudio's local-client log buffer; default 200. |
| `confirm_redeploy` | `apollo_id`, `confirmation` | Confirms the visible MCStudio redeploy dialog in-process; no mouse or keyboard automation. |
| `live_logs` | optional `server_id` | Reads the Server Log window currently open in MCStudio. If supplied, `server_id` must match that window. |
| `deploy_logs` | `apollo_id` | Reads deployment activity/history available in the loaded project. |
| `hotfix` | `apollo_id`, `confirmation`, optional `client` | Queues server hotfix, or client hotfix when `client: true`. |
| `development_test` | `apollo_id`, `confirmation` | Queues MCStudio's native development-test command. |
| `clear` | `apollo_id`, `confirmation` | Queues stop-and-clear for the project. This interrupts its servers. |
| `redeploy` | `apollo_id`, `confirmation` | Queues MCStudio's native redeploy flow. |

### Read examples

Discover projects and nodes:

```json
{"tool":"projects","arguments":{}}
```

Read the latest 200 lines from one returned node:

```json
{
  "tool": "logs",
  "arguments": {
    "apollo_id": 12345,
    "server_id": 4000,
    "lines": -200,
    "offset": -1
  }
}
```

Read deployment logs:

```json
{"tool":"deploy_logs","arguments":{"apollo_id":12345}}
```

Read the latest local-client lines:

```json
{"tool":"client_logs","arguments":{"tail_lines":200}}
```

`client_logs` requires a local client launched through MCStudio.
`live_logs` requires the MCStudio Server Log window to be open.

### Operation examples

Operation confirmations are exact and case-sensitive. MCStudio must also hold
server control for the selected Apollo project; otherwise the queued operation
is rejected with `未锁定服务器控制权，请锁定后重试。`:

```json
{"tool":"hotfix","arguments":{"apollo_id":12345,"confirmation":"HOTFIX 12345","client":false}}
```

```json
{"tool":"hotfix","arguments":{"apollo_id":12345,"confirmation":"HOTFIX 12345","client":true}}
```

```json
{"tool":"development_test","arguments":{"apollo_id":12345,"confirmation":"DEVELOPMENT TEST 12345"}}
```

```json
{"tool":"clear","arguments":{"apollo_id":12345,"confirmation":"CLEAR 12345"}}
```

```json
{"tool":"redeploy","arguments":{"apollo_id":12345,"confirmation":"REDEPLOY 12345"}}
```

After the native redeploy confirmation dialog appears, confirm it in-process:

```json
{"tool":"confirm_redeploy","arguments":{"apollo_id":12345,"confirmation":"CONFIRM REDEPLOY 12345"}}
```

The confirmation tool only accepts the visible MCStudio redeploy dialog. It
invokes the unique enabled `确定` button through Windows UI Automation and
never uses mouse, keyboard, or Computer Use automation.

A `request_queued` response means the operation was submitted to MCStudio's UI
Dispatcher. It does not claim that the operation completed; observe MCStudio
and the relevant logs for completion.

The adapter reconnects after a stale native SSE session, but never replays the
interrupted call. The caller receives `upstream_disconnected`; a later call is
forwarded after recovery. This applies equally to read and state-changing
commands, preventing ambiguous duplicate execution.

## Troubleshooting

### Native MCP is not listening

Check the native endpoint:

```powershell
netstat -ano | findstr :19131
```

Enable/start MCP in MCStudio, then rerun `start-allurix-bridge.bat`.

### Build reports that a DLL is in use

Restart MCStudio, rebuild, then inject again. The main bridge and bootstrap are
loaded modules and cannot be overwritten safely while MCStudio is using them.

### Injection succeeds but the bridge is unavailable

Inspect:

```text
mcstudio_bridge\bin\allurix_bootstrap.log
```

Then call `allurix_bridge` with `status`. A healthy response has
`mcp_status: "Running"`, the expected tool count, and an empty `load_errors`
array. If the bootstrap DLL was already loaded, restart MCStudio before
injecting again.

### Port 19132 is unavailable

Check the listener:

```powershell
netstat -ano | findstr :19132
```

Port `19132` must have exactly one `mcp_proxy.py` listener. Stop stale proxy
processes before starting the service again. Upstream reconnects happen inside
that process and do not replace the listener.

### An operation is queued but nothing starts

Read `deploy_logs` for the project. If it reports
`未锁定服务器控制权，请锁定后重试。`, acquire server control in MCStudio and
retry the operation. A queued response only confirms Dispatcher submission.

### `client_logs` or `live_logs` reports no window

These tools read MCStudio UI buffers; they do not create or select their UI.
Launch a local client before using `client_logs`, and open the desired Server
Log window before using `live_logs`.

## Verification

Run the local checks:

```powershell
py -3 -m unittest discover -s tests -v
py -3.14 -m compileall -q apollo_core mcp_proxy.py
```

Build validation covers the x86 C# bridge, injector, all tool DLLs, and the
C++/CLI bootstrap.

## Repository layout

```text
mcp_proxy.py          Singleton Streamable HTTP + legacy SSE proxy
mcp_stdio_client.py   Lightweight Codex stdio adapter and singleton launcher
apollo_core/          Explicit read-oriented Apollo helpers
mcstudio_bridge/      Bridge, injector, bootstrapper, and MCP tool sources
```

Build output, runtime logs, local notes, tests, local captures, and editor
metadata are ignored.

## License

Licensed under the [MIT License](LICENSE).
