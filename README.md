# AgentTerminal

AgentTerminal is a small .NET 8 bridge that lets a local coding agent run commands
inside a **visible Windows Terminal workspace**. You can watch the exact command,
live standard output and standard error, and final exit code while the same result is
returned to the calling agent.

It uses Windows Terminal's native windows, tabs, panes, fonts, selection, and rendering
instead of implementing another terminal emulator.

![AgentTerminal with named tabs and an independently controlled split pane](docs/agent-terminal-layout.png)

> [!WARNING]
> AgentTerminal is an arbitrary command runner. Commands execute with the permissions
> of the current Windows user. Only allow trusted local agents to invoke it, inspect
> commands before approving sensitive operations, and never send passwords, tokens,
> or other secrets through a visible session.

## What it provides

- Visible, agent-controlled command execution in Windows Terminal
- Named Windows Terminal window groups
- Tabs and split panes created on demand
- Independently addressable sessions for every tab or pane
- Direct Windows process execution without shell interpolation
- Per-session PowerShell, Command Prompt, WSL, and Conda profiles
- PowerShell, CMD, and WSL override modes for one-off commands
- Live output mirrored to both the visible terminal and calling agent
- Original command exit codes returned to the caller
- Local named-pipe transport restricted to the current Windows user
- Built-in MCP stdio server for agent-independent tool discovery
- Structured JSON output and provider-neutral function schemas

## What it does not provide

- Remote access or a network API
- Sandboxing or permission reduction
- Command approval dialogs
- Persistent layout restoration after all windows close
- Interactive stdin for password prompts, editors, REPLs, or full-screen TUIs
- Keystroke injection into Windows Terminal

## Requirements

To run AgentTerminal:

- Windows 10 or Windows 11
- [Windows Terminal](https://learn.microsoft.com/windows/terminal/install) with the
  `wt.exe` app execution alias enabled
- .NET 8 Runtime for the default framework-dependent build
- WSL only if `--wsl` execution is required
- Conda only if a `conda` profile is required

To build AgentTerminal, install the .NET 8 SDK or newer.

## Build

Clone the repository and publish a framework-dependent, single-file Windows executable:

```powershell
git clone https://github.com/goyanx/agentterminal.git
Set-Location .\agentterminal

dotnet publish .\AgentTerminal\AgentTerminal.csproj `
  -c Release `
  -r win-x64 `
  --self-contained false `
  -p:PublishSingleFile=true `
  -o .\dist
```

The executable will be created at `dist\AgentTerminal.exe`.

## Quick start

From the repository root:

```powershell
$at = (Resolve-Path .\dist\AgentTerminal.exe).Path

# Create a visible Windows Terminal window and its first controlled session.
& $at new-window --window work --name shell --profile powershell --cwd 'C:\path\to\your\repo'

# Confirm that the session control channel is reachable.
& $at ping --name shell

# Run a command and receive its output and exit code.
& $at run --name shell -- git status --short
```

The Windows Terminal window remains open and waits for additional commands.

## Windows, tabs, panes, and sessions

AgentTerminal distinguishes between two names:

- A **window name** identifies a Windows Terminal window group.
- A **session name** identifies one controlled AgentTerminal host in a tab or pane.

Every live session must have a unique name, including sessions in different windows.
Agents send later `run`, `ping`, and `stop` commands to the session name.

```powershell
# Create the named window group "work" with a first session named "shell".
& $at new-window --window work --name shell --cwd 'C:\path\to\your\repo'

# Add a separately controlled tab to the same window.
& $at new-tab --window work --name tests --cwd 'C:\path\to\your\repo'

# Split the active tab. The new pane receives 40% of the parent pane.
& $at split-pane --window work --name server `
  --vertical --size 0.40 --cwd 'C:\path\to\your\repo'

# Route work to individual destinations.
& $at run --name tests -- dotnet test
& $at run --name server --shell 'dotnet run'
```

`split-pane` applies to the currently active tab in the named window. Windows Terminal
creates a window when the specified window group does not exist. To guarantee a separate
physical window, use a new unique `--window` name.

## Session profiles

Each visible session has one predictable execution profile. Commands submitted with
`--command` run through that profile:

| Profile | Command host | Profile option |
| --- | --- | --- |
| `powershell` | PowerShell 7, falling back to Windows PowerShell | Default |
| `cmd` | `cmd.exe /d /s /c` | DOS/Windows command syntax |
| `wsl` | `wsl.exe` and Bash | Optional `--distro NAME` |
| `conda` | PowerShell inside `conda run` | Optional `--conda-env NAME`, default `base` |

The `conda` profile uses a Windows Conda installation. It is separate from a `wsl`
profile and cannot activate a Windows Conda environment inside Linux.

```powershell
& $at new-window --window work --name ps --profile powershell --cwd 'C:\src\app'
& $at new-tab --window work --name dos --profile cmd --cwd 'C:\src\app'
& $at new-tab --window work --name linux --profile wsl --distro Ubuntu-22.04 --cwd 'C:\src\app'
& $at new-tab --window work --name ml --profile conda --conda-env diffusion --cwd 'C:\src\app'

& $at run --name ps --command '$PSVersionTable.PSVersion'
& $at run --name dos --command 'dir /b'
& $at run --name linux --command 'uname -a'
& $at run --name ml --command 'python --version'
```

Profiles are intentionally stateless between submissions. Every command gets a clean
child process, reliable output boundaries, and an exact exit code. Use `--cwd`, a Conda
environment, or an explicit command when state is needed; shell variables and `cd`
changes do not carry into the next command.

## Command execution modes

### Direct Windows execution

Place the executable and arguments after `--`. This mode does not invoke a shell, so
characters such as `$`, `|`, `>`, and `;` are passed as ordinary arguments.

```powershell
& $at run --name shell --cwd 'C:\path\to\your\repo' -- git status --short
& $at run --name tests --cwd 'C:\path\to\your\repo' -- dotnet test
```

Prefer direct execution unless shell syntax is necessary.

### PowerShell execution

Use `--shell` with one quoted command string for pipelines, redirects, variables, or
multiple statements. AgentTerminal uses PowerShell 7 when installed and falls back to
Windows PowerShell.

```powershell
& $at run --name shell --cwd 'C:\path\to\your\repo' `
  --shell 'dotnet build; git status --short'
```

Shell input is interpreted as PowerShell code. Do not pass untrusted text into this mode.

### Command Prompt execution

Use `--cmd` for a one-off command using Command Prompt syntax, regardless of the
session's profile:

```powershell
& $at run --name shell --cmd 'dir /b && echo complete'
```

### WSL execution

Use `--wsl` with one quoted Linux shell command. AgentTerminal invokes the default WSL
distribution through `wsl.exe -- bash -lc`.

```powershell
& $at run --name linux --wsl 'cd /mnt/c/path/to/repo && uname -a && git status --short'
```

The AgentTerminal client can also be called from inside WSL:

```bash
/mnt/c/path/to/agentterminal/dist/AgentTerminal.exe \
  run --name linux --wsl 'pwd; whoami; git status --short'
```

`--cwd` is always a Windows host path. For Linux working directories, use `cd` inside
the quoted `--wsl` command. Working-directory changes do not persist between submissions.

## CLI reference

```text
agent-terminal start      [--window WINDOW] [--name NAME] [--cwd PATH]
                           [--title TITLE] [--profile PROFILE] [--maximized]
agent-terminal new-window [--window WINDOW] [--name NAME] [--cwd PATH]
                           [--title TITLE] [--profile PROFILE] [--maximized]
agent-terminal new-tab    [--window WINDOW] --name NAME [--cwd PATH]
                           [--title TITLE] [--profile PROFILE]
agent-terminal split-pane [--window WINDOW] --name NAME [--cwd PATH]
                           [--title TITLE] [--profile PROFILE]
                           [--conda-env ENV] [--distro DISTRO]
                           [--horizontal|--vertical]
                           [--size 0.05-0.95]

agent-terminal run [--name NAME] [--cwd PATH] -- PROGRAM [ARGUMENTS...]
agent-terminal run [--name NAME] [--cwd PATH] --command "PROFILE COMMAND"
agent-terminal run [--name NAME] [--cwd PATH] --shell "POWERSHELL COMMAND"
agent-terminal run [--name NAME] [--cwd PATH] --cmd "CMD COMMAND"
agent-terminal run [--name NAME] [--cwd PATH] --wsl "LINUX COMMAND"

agent-terminal ping [--name NAME]
agent-terminal stop [--name NAME]

agent-terminal --json COMMAND [OPTIONS]
agent-terminal mcp
```

Aliases:

| Full command | Alias |
| --- | --- |
| `new-window` | `start`, `window` |
| `new-tab` | `tab` |
| `split-pane` | `split` |

Names may contain letters, numbers, periods, underscores, and dashes, with a maximum
length of 40 characters. The default session name is `main`. For `new-window`, the
window name defaults to the session name; tabs and panes default to window `main`.

### Exit behavior

- `run` returns the executed command's exit code.
- `ping` returns zero when the session is reachable.
- `stop` asks one named session to exit; it does not stop other tabs or panes.
- Commands sent to one session execute serially, one at a time.

## Using AgentTerminal with an AI coding agent

An agent must explicitly call AgentTerminal; existing headless shell calls are not
intercepted automatically. Give the agent the path to the executable and a session name.

Suggested instruction:

> For commands I should be able to watch, invoke `AgentTerminal.exe run --name
> <session> -- <program> <args>` instead of using a hidden shell. Create additional
> destinations with `new-tab` or `split-pane`, assigning every destination a unique
> session name and the appropriate `--profile`. Use `--command` to honor that profile;
> use direct `--`, `--shell`, `--cmd`, or `--wsl` only for an intentional override.
> Never silently fall back to headless execution when visibility was requested.

See [AGENTS.md](AGENTS.md) for a complete instruction file designed for coding agents.

## MCP and function-tool integration

AgentTerminal includes a local MCP stdio server:

```powershell
AgentTerminal.exe mcp
```

It exposes `agent_terminal_open`, `agent_terminal_run`, `agent_terminal_ping`, and
`agent_terminal_stop`. Clients without MCP support can use the provider-neutral
[function-tool manifest](integrations/function-tools.json) together with structured CLI
output:

```powershell
AgentTerminal.exe --json run --name shell -- dotnet --version
```

See [Agent integrations](docs/INTEGRATIONS.md) for MCP configuration, function-schema
mapping, WSL/Hermes setup, and integration security requirements.

## Architecture

```text
Coding agent or local user
          │
          │ AgentTerminal.exe run --name <session> ...
          ▼
Current-user Windows named pipe
          │
          ▼
AgentTerminal host in a Windows Terminal tab or pane
          │
          ├── direct Windows child process
          ├── PowerShell child process
          ├── cmd.exe → /d /s /c
          ├── wsl.exe → bash -lc
          └── conda.exe run → PowerShell
          │
          ▼
stdout, stderr, and exit code
   ├── rendered in the visible terminal
   └── returned to the calling agent
```

There is no TCP listener, web server, cloud service, telemetry client, model API, or
credential store in AgentTerminal.

## Security model

AgentTerminal is intended for trusted local use.

- Named pipes use the current-user-only option.
- Session names are validated before being used in pipe and window identifiers.
- Direct execution passes arguments without invoking a command shell.
- PowerShell, CMD, WSL, and Conda profile commands execute shell code verbatim.
- Commands run with the current user's permissions; AgentTerminal is not a sandbox.
- Output is displayed and returned to the caller but is not intentionally persisted by
  AgentTerminal.
- Do not expose AgentTerminal through an unauthenticated HTTP, RPC, or remote-control
  wrapper.
- Do not allow untrusted model output to execute without an external approval policy.

When integrating with an autonomous agent, confirmation should be required for deletion,
software installation, credential access, security changes, financial activity, and
other consequential operations.

See [SECURITY.md](SECURITY.md) for supported-version and private vulnerability-reporting
information.

## Troubleshooting

### `wt.exe` cannot be found

Install Windows Terminal and enable its app execution alias in Windows settings. Confirm
that `%LOCALAPPDATA%\Microsoft\WindowsApps` is present in the Windows `PATH`.

### The session is not running

Check the session name:

```powershell
& $at ping --name shell
```

If it is unavailable, create the window, tab, or pane again. Session names are exact.

### A tab opens in an unexpected window

Use the same `--window` value that created the original window group. A missing named
window can cause Windows Terminal to create a new one.

### A Linux command is not found

Direct mode launches Windows executables even when the client is invoked from WSL. Use
`--wsl 'COMMAND'` for Linux programs.

### A command waits for input

Interactive stdin is not supported. Cancel or close that session and use a noninteractive
form of the command. Never place secrets directly in command-line arguments.

### Conda reports `chcp` not found when called from WSL

Use version 0.4.1 or newer. AgentTerminal normalizes the essential Windows process
environment when its MCP server is launched through WSL. WSL clients may pass mounted
working directories such as `/mnt/c/src/project`; they are converted to local Windows
paths. CMD and Conda profiles cannot use `\\wsl.localhost\...` UNC directories.

## Development

Build the project without publishing:

```powershell
dotnet build .\AgentTerminal\AgentTerminal.csproj -c Release
```

Before submitting a change:

1. Build with zero warnings.
2. Test direct, PowerShell, and WSL execution as applicable.
3. Verify real Windows Terminal window, tab, or pane creation for windowing changes.
4. Ensure examples and `AGENTS.md` match the CLI.
5. Confirm no secrets, local logs, compiled binaries, or personal files are staged.

## Project status

AgentTerminal is an early-stage Windows utility. Its command-line interface and behavior
may evolve while interactive input, session discovery, persistence, and stronger approval
workflows are explored.

See [CHANGELOG.md](CHANGELOG.md) for release history.

## License

No license has been selected yet. Until a license file is added, do not assume permission
to copy, modify, or redistribute the source beyond what applicable law permits.
