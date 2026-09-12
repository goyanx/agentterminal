# Changelog

All notable changes to AgentTerminal are documented in this file.

## [0.6.0] - 2026-09-13

### Added

- A line-oriented human command prompt in every visible AgentTerminal session.
- Shared human/agent command routing with visible source labels.
- Human prompt controls for working directory, background jobs, logs, and shutdown.

### Fixed

- Cancelling a host now terminates an active foreground child-process tree.

## [0.5.0] - 2026-09-13

### Added

- Supervised background commands through `run --background` and the MCP run tool.
- Background-job status, captured logs, and process-tree stop operations.
- A provider-neutral `agent_terminal_job` MCP/function tool.
- Bounded in-memory stdout and stderr capture for the current background job.

### Fixed

- Long-running commands no longer have to occupy the session control connection and
  trigger agent-host timeouts.
- Stopping a session also terminates its supervised background process tree.

## [0.4.1] - 2026-09-13

### Fixed

- Normalize essential Windows environment variables when the MCP server is launched
  through WSL interoperability, preventing Conda failures such as `chcp` not found.
- Translate `/mnt/<drive>/...` working directories supplied by WSL clients to local
  Windows paths.
- Reject unsupported UNC working directories for CMD and Conda with a clear error.
- Use absolute paths for built-in Windows command hosts.

## [0.4.0] - 2026-09-12

### Added

- Per-session `powershell`, `cmd`, `wsl`, and `conda` execution profiles.
- Conda environment and WSL distribution selection when creating a session.
- Profile-aware `--command` CLI execution and MCP `mode: session` calls.
- Explicit Command Prompt execution through `--cmd` and MCP `mode: cmd`.
- Profile metadata in structured ping results so callers can verify destinations.

### Design

- Profile commands use clean child processes rather than leaking shell state between
  agent calls, preserving reliable output boundaries and exit codes.

## [0.3.0] - 2026-09-12

### Added

- Built-in MCP stdio server using newline-delimited JSON-RPC.
- MCP tools for opening surfaces, running commands, checking sessions, and stopping
  sessions.
- Structured CLI output through the global `--json` option.
- Provider-neutral function-tool schemas.
- Generic MCP configuration and integration documentation.
- Explicit package metadata and public security policy.

### Security

- Command execution is marked potentially destructive in MCP tool annotations.
- MCP transport is local stdio only; no network listener was added.
- Integration guidance requires host-side approval for consequential commands.

## [0.2.0] - 2026-09-12

### Added

- Named Windows Terminal window groups.
- Tabs and horizontal or vertical split panes created on demand.
- Independently addressable sessions for every tab and pane.

## [0.1.0] - 2026-09-12

### Added

- Visible Windows Terminal host controlled through a current-user named pipe.
- Direct Windows, PowerShell, and WSL command modes.
- Live stdout and stderr mirroring with exit-code preservation.
