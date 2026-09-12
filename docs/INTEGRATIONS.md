# Agent integrations

AgentTerminal exposes the same four operations through three provider-neutral surfaces:

1. Human-readable command-line output for interactive use
2. Structured JSON command-line output for subprocess adapters
3. Model Context Protocol (MCP) over stdio

No integration requires an API key, model provider, network port, or cloud service.

## MCP stdio server

Start the MCP server with:

```powershell
AgentTerminal.exe mcp
```

The MCP client owns this process and communicates using newline-delimited JSON-RPC on
stdin and stdout. AgentTerminal writes no banners or diagnostics to stdout in MCP mode.

The server supports protocol versions `2024-11-05`, `2025-03-26`, `2025-06-18`, and
`2025-11-25`. It exposes:

| Tool | Purpose |
| --- | --- |
| `agent_terminal_open` | Create a window, tab, or split pane |
| `agent_terminal_run` | Run a direct, PowerShell, or WSL command |
| `agent_terminal_ping` | Check whether a session is reachable |
| `agent_terminal_stop` | Stop one session |

`agent_terminal_run` is annotated as potentially destructive because it can execute an
arbitrary command with the current user's permissions. MCP hosts should require approval
according to their own policy.

### Generic Windows configuration

Most MCP clients accept a configuration shaped like:

```json
{
  "mcpServers": {
    "agentterminal": {
      "command": "C:\\path\\to\\agentterminal\\dist\\AgentTerminal.exe",
      "args": ["mcp"]
    }
  }
}
```

Use an absolute executable path. A copy is available at
[`integrations/mcp.windows.example.json`](../integrations/mcp.windows.example.json).

### WSL-hosted agents

A WSL process can start the Windows MCP executable through Windows interop:

```yaml
mcp_servers:
  agentterminal:
    command: "/mnt/c/path/to/agentterminal/dist/AgentTerminal.exe"
    args: ["mcp"]
```

The MCP transport remains stdio. Actual terminal commands can use `direct`, `powershell`,
or `wsl` mode independently of where the MCP client runs.

## Function-tool adapters

[`integrations/function-tools.json`](../integrations/function-tools.json) contains a
provider-neutral manifest with tool names, descriptions, and JSON input schemas.

For APIs that accept function definitions at the top level, map each entry as follows:

```javascript
const tools = manifest.tools.map((tool) => ({
  type: "function",
  name: tool.name,
  description: tool.description,
  parameters: tool.inputSchema,
}));
```

For Chat-Completions-style APIs that nest the function definition:

```javascript
const tools = manifest.tools.map((tool) => ({
  type: "function",
  function: {
    name: tool.name,
    description: tool.description,
    parameters: tool.inputSchema,
  },
}));
```

The application hosting the model remains responsible for executing the selected tool.
It can either call the MCP server or translate the function arguments to the structured
CLI described below.

## Structured CLI

Place `--json` before the ordinary command:

```powershell
AgentTerminal.exe --json ping --name shell
AgentTerminal.exe --json run --name shell -- dotnet --version
AgentTerminal.exe --json run --name shell --shell 'git status --short'
AgentTerminal.exe --json run --name linux --wsl 'uname -a'
```

Successful ping result:

```json
{"ok":true,"action":"ping","session":"shell","reachable":true}
```

Command result:

```json
{
  "ok": true,
  "action": "run",
  "session": "shell",
  "exitCode": 0,
  "stdout": "8.0.100\r\n",
  "stderr": ""
}
```

The process still returns the executed command's exit code. Parse stdout as one JSON
document rather than inferring success from text.

## Tool argument mapping

### `agent_terminal_open`

| Argument | CLI mapping |
| --- | --- |
| `action: new_window` | `new-window` |
| `action: new_tab` | `new-tab` |
| `action: split_pane` | `split-pane` |
| `session` | `--name` |
| `window` | `--window` |
| `cwd` | `--cwd` |
| `title` | `--title` |
| `orientation` | `--horizontal` or `--vertical` |
| `size` | `--size` |
| `maximized` | `--maximized` when true |

### `agent_terminal_run`

- `mode: direct` requires `program`; `arguments` is an optional string array.
- `mode: powershell` requires `command` and maps to `--shell`.
- `mode: wsl` requires `command` and maps to `--wsl`.
- `cwd` is always an optional Windows host path.

## Hermes Agent

Hermes Agent supports custom MCP stdio servers. Add an `agentterminal` entry to the
Hermes `mcp_servers` configuration using the WSL example above, then reload or restart
Hermes so it discovers the four tools. Hermes prefixes discovered names with the MCP
server name; consult the Hermes tool list after loading.

Keep command approval enabled. AgentTerminal deliberately delegates authorization to
the host agent because it cannot know whether a generated command matches the user's
intent.

## Demerzel Chat and other frontends

A chat frontend does not gain execution authority merely because AgentTerminal is
installed. Its agent runtime must register the MCP server or the function-tool manifest,
execute tool calls, return their structured results to the model, and enforce approvals.

For architectures where a frontend delegates tool execution to Hermes, configure the
MCP server in Hermes rather than adding provider-specific code to AgentTerminal. The
frontend will then use the capability through Hermes's existing agent/tool loop.

## Security requirements for integrators

- Treat all command strings as untrusted until approved under the host's policy.
- Prefer `direct` mode with an argument array when shell syntax is unnecessary.
- Never interpolate model output into an extra shell layer.
- Never expose the stdio server through an unauthenticated network bridge.
- Do not send credentials or secrets through visible commands.
- Apply timeouts and cancellation in the MCP or function-tool host.
- Present destructive, financial, security-sensitive, installation, and external
  communication commands to the user before execution.
