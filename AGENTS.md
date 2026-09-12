# AgentTerminal Instructions for AI Coding Agents

## Purpose

AgentTerminal mirrors commands and their live output into a Windows Terminal window that the user can watch. Use the existing executable as a tool. **Do not modify, regenerate, extend, or add code to AgentTerminal.**

Executable locations:

- Windows: `D:\AgentTerminal\dist\AgentTerminal.exe`
- WSL: `/mnt/d/AgentTerminal/dist/AgentTerminal.exe`

Default visible session name: `demo`

Machine integrations may start the provider-neutral MCP stdio server with
`AgentTerminal.exe mcp`, or prefix ordinary CLI commands with `--json` for one-document
structured output. See `docs/INTEGRATIONS.md`. Do not scrape human-readable CLI output.

## Window and session model

- A **window** is a named Windows Terminal window containing one or more tabs and panes.
- A **session** is one independently controlled AgentTerminal host occupying a tab or pane.
- `--window` selects the visual window group.
- `--name` selects the session used by `run`, `ping`, and `stop`.
- `--profile` declares the session command environment: `powershell`, `cmd`, `wsl`, or
  `conda`.
- Every live session name must be unique, including sessions in different windows.

Create a window and its first session:

```powershell
& 'D:\AgentTerminal\dist\AgentTerminal.exe' new-window --window work --name shell --profile powershell --cwd 'D:\path\to\repo'
```

Add a tab on the fly:

```powershell
& 'D:\AgentTerminal\dist\AgentTerminal.exe' new-tab --window work --name linux --profile wsl --distro Ubuntu-22.04 --cwd 'D:\path\to\repo'
```

Split the active tab on the fly:

```powershell
& 'D:\AgentTerminal\dist\AgentTerminal.exe' split-pane --window work --name ml --profile conda --conda-env base --vertical --size 0.40 --cwd 'D:\path\to\repo'
```

`--horizontal` and `--vertical` control split orientation. `--size` is the fraction
allocated to the new pane and must be between 0.05 and 0.95. The split applies to the
currently active tab in the named window.

Route work by session name:

```powershell
& 'D:\AgentTerminal\dist\AgentTerminal.exe' run --name shell --command 'dotnet test'
& 'D:\AgentTerminal\dist\AgentTerminal.exe' run --name linux --command 'uname -a'
& 'D:\AgentTerminal\dist\AgentTerminal.exe' run --name ml --command 'python --version'
```

Use `--command` when the command should follow the destination profile. `ping --name
NAME` returns the profile and any Conda environment or WSL distribution in JSON mode.
Direct `--`, `--shell`, `--cmd`, and `--wsl` remain available as explicit per-command
overrides. Profile commands are stateless: do not assume `cd`, activated environments,
aliases, or shell variables persist between submissions.

A `conda` profile uses Conda installed on Windows. Do not create a `wsl` profile and
then attempt to activate a Windows Conda environment inside it. WSL-hosted callers may
pass `/mnt/c/...` or another `/mnt/<drive>/...` value as `cwd`; AgentTerminal converts
that mount path to the corresponding local Windows path.

## Required behavior

When the user asks to see terminal activity, route each relevant shell command through AgentTerminal instead of running it with a hidden/headless execution tool.

AgentTerminal displays:

- The submitted command
- Its working directory
- Live standard output and standard error
- The final exit code

The same output and exit code are returned to the calling agent. Read them normally and continue the task based on the result.

## Connect to an existing session

Check whether the visible session is reachable before sending work.

From Windows PowerShell:

```powershell
& 'D:\AgentTerminal\dist\AgentTerminal.exe' ping --name demo
```

From WSL:

```bash
/mnt/d/AgentTerminal/dist/AgentTerminal.exe ping --name demo
```

A successful connection prints:

```text
Agent terminal 'demo' is reachable.
```

If the session is unreachable, tell the user that the visible host is not running. An agent with permission to open desktop applications may start it using the instructions below. Otherwise, ask the user to start it.

## Start the visible host

From Windows PowerShell:

```powershell
& 'D:\AgentTerminal\dist\AgentTerminal.exe' start --name demo --cwd 'D:\AgentTerminal'
```

From WSL through Windows interop:

```bash
cmd.exe /c 'D:\AgentTerminal\dist\AgentTerminal.exe start --name demo --cwd D:\AgentTerminal'
```

Starting the host opens a real Windows Terminal window. Do not start another host when `ping` already succeeds. Use `new-tab` or `split-pane` with a new session name when another visible destination is desired.

## Run Windows programs directly

Use this form for a Windows executable with arguments. Arguments are passed directly without shell interpretation.

From Windows PowerShell:

```powershell
& 'D:\AgentTerminal\dist\AgentTerminal.exe' run --name demo --cwd 'D:\path\to\repo' -- git status --short
```

From WSL, the same form still launches a **Windows** program:

```bash
/mnt/d/AgentTerminal/dist/AgentTerminal.exe run --name demo --cwd 'D:\path\to\repo' -- git.exe status --short
```

Prefer direct execution when pipes, redirects, variables, and multiple shell statements are not needed.

## Run a Windows PowerShell command

Use `--shell` for PowerShell syntax such as pipelines, redirection, variables, or multiple statements.

```powershell
& 'D:\AgentTerminal\dist\AgentTerminal.exe' run --name demo --cwd 'D:\path\to\repo' --shell 'dotnet test; git status --short'
```

The value after `--shell` must be one quoted command string.

## Run a Linux command in WSL

Use `--wsl` for commands that must execute inside the default WSL distribution.

From WSL:

```bash
/mnt/d/AgentTerminal/dist/AgentTerminal.exe run --name demo --wsl 'pwd; whoami; git status --short'
```

From Windows PowerShell:

```powershell
& 'D:\AgentTerminal\dist\AgentTerminal.exe' run --name demo --wsl 'pwd; whoami; git status --short'
```

The value after `--wsl` must be one quoted Linux shell command string.

## Working-directory rules

- `--cwd` is a Windows path because the visible host is a Windows process.
- For Windows commands, set `--cwd` to the Windows repository path.
- For `--wsl` commands, navigate to a Linux or mounted path inside the quoted command when necessary:

```bash
/mnt/d/AgentTerminal/dist/AgentTerminal.exe run --name demo --wsl 'cd /mnt/d/path/to/repo && npm test'
```

- Do not assume that `cd` persists between submissions. Each submitted command is a separate process.

## Exit codes and failures

- AgentTerminal exits with the submitted command's exit code.
- Treat a nonzero exit code exactly as if the command had run through the agent's normal shell tool.
- If the host disconnects, ping it again before retrying.
- Do not silently fall back to headless execution when the user explicitly asked to watch the command. Report that the visible session is unavailable.

## Safety and limitations

- Commands are serialized and run one at a time.
- Named-pipe access is restricted to the current Windows user.
- Do not send passwords, API keys, access tokens, or other secrets through the visible terminal.
- Fully interactive stdin programs are not supported. Do not use AgentTerminal for password prompts, text editors, REPLs, full-screen TUIs, or commands requiring keyboard responses.
- Do not attempt UI keystroke injection into Windows Terminal.
- Do not stop the visible host unless the user asks. If requested:

```powershell
& 'D:\AgentTerminal\dist\AgentTerminal.exe' stop --name demo
```

## Minimal agent workflow

1. Run `ping --name <session>`.
2. Create a uniquely named tab, pane, or window if a new destination is needed.
3. Prefer `--command` to use the session profile; select direct `--`, `--shell`,
   `--cmd`, or `--wsl` only as an intentional override.
4. Include the appropriate working directory.
5. Submit the command and stream its returned output to your normal reasoning loop.
6. Check the returned exit code before continuing.
7. Leave visible hosts running.

## Quick harmless test

From WSL:

```bash
/mnt/d/AgentTerminal/dist/AgentTerminal.exe run --name demo --wsl 'echo "AI agent connected to AgentTerminal"; whoami; pwd'
```

From Windows PowerShell:

```powershell
& 'D:\AgentTerminal\dist\AgentTerminal.exe' run --name demo --shell 'Write-Output "AI agent connected to AgentTerminal"; whoami; Get-Location'
```
