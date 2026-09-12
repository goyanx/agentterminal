# Changelog

All notable changes to AgentTerminal are documented in this file.

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
