# Security policy

## Security model

AgentTerminal is a local arbitrary command runner. It does not sandbox commands or
reduce the current user's permissions. Its purpose is visibility and agent integration,
not isolation.

- The control channel is a Windows named pipe restricted to the current user.
- The MCP server uses local stdio and does not listen on a network port.
- No credentials are required, stored, or transmitted by AgentTerminal.
- Direct execution avoids a command shell; PowerShell and WSL modes intentionally
  interpret shell code.
- MCP clients and function-tool hosts are responsible for user approval and policy.

Only connect trusted local clients. Do not place an unauthenticated HTTP, WebSocket,
RPC, or public network bridge in front of AgentTerminal.

## Supported versions

Security fixes are applied to the latest commit on the `main` branch while the project
is in early development. Tagged release support will be documented when releases begin.

## Reporting a vulnerability

Please report vulnerabilities privately using
[GitHub Security Advisories](https://github.com/goyanx/agentterminal/security/advisories/new).
Do not open a public issue containing exploit details, credentials, private logs, or
personal data.

Include:

- A concise description of the issue and impact
- The affected commit or version
- Reproduction steps using non-sensitive sample data
- Any suggested mitigation

## Integration guidance

Integrators should:

- Require confirmation for consequential commands.
- Prefer direct execution with an argument array over shell strings.
- Treat model-generated commands as untrusted input.
- Keep the MCP server on stdio rather than exposing it over a network.
- Avoid placing secrets in commands, arguments, output, screenshots, or logs.
- Apply bounded timeouts at the agent-host layer.
