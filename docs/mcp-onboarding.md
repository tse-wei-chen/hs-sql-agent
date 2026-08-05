# MCP client onboarding and compatibility

HS SQL Agent exposes a Streamable HTTP MCP endpoint at `/mcp`. Authenticate with
`X-MCP-Server-Key: <MCP key>`. The Admin Panel generates direct HTTP configuration
for Claude Desktop, Cursor, and generic Streamable HTTP clients only immediately
after Issue, Rotate, or Duplicate. The plaintext secret is not stored and cannot
be shown again after that dialog is closed.

## Compatibility baseline

The following rows distinguish what this repository actually verifies from client
configuration examples. Do not infer application-level Elicitation support from a
successful connection.

| Client / component | Version | Verified coverage |
| --- | --- | --- |
| HS SQL Agent Admin smoke client | 1.0.0 | Streamable HTTP `initialize` using MCP `2025-11-25`, `X-MCP-Server-Key` authentication, and server `tools` capability; response/error classification is covered by frontend tests. |
| ModelContextProtocol.AspNetCore server SDK | 1.4.0 | Server transport and MCP tool exposure used by this release. |
| Claude Desktop | Operator-installed version | A direct HTTP `mcpServers` entry with the `X-MCP-Server-Key` header is generated. Record the exact deployed version only after running the one-time smoke test and, for DML, a manual Elicitation decline/accept test. |
| Cursor | Operator-installed version | Current HTTP configuration is generated from the standard `mcpServers` schema. Record the exact deployed version only after the same connection and DML checks. |

The generated Claude Desktop snippet connects directly to the Streamable HTTP
endpoint and does not install or execute a third-party stdio bridge.

Cursor supports remote Streamable HTTP MCP servers and custom headers through its
MCP configuration. Client behavior changes over time, so generated configuration
and the deployed client version must be validated together rather than claiming an
untested version here.

## One-time smoke test

Run the smoke test before closing the one-time key dialog. It reports three stages
independently:

- **Network**: the browser received any HTTP response from `/mcp`. A passed network
  stage does not mean the key was accepted.
- **Auth**: the endpoint returned neither `401` nor `403` and accepted the one-time
  `X-MCP-Server-Key`. Other server errors are reported as inconclusive rather than as an
  authentication success.
- **Capability**: the body is a valid MCP initialize response and advertises the
  tools capability. A proxy HTML page or non-MCP endpoint fails here even if it
  returned HTTP 200.

In local frontend development, Nuxt proxies `/mcp` to `http://localhost:8080/mcp`.
In production, the endpoint is derived from the Admin Panel's public origin and can
be edited in the one-time dialog when a reverse proxy publishes MCP under a
different host.

## DML and Elicitation

`execute_dml_sql` and published DML Custom Tools require form Elicitation. The
server first executes a rollback-only dry run, sends `elicitation/create`, and only
commits after the human accepts. If the client does not declare and implement form
Elicitation, the operation is refused.

The Admin smoke client declares form Elicitation to exercise capability negotiation,
but it does not prove that another application's UI can display the request. Before
allowing DML in production, test the exact client version by invoking DML and verify
both Decline and Accept paths. Query-only keys do not require Elicitation; restrict
their allowed tool list instead of leaving it unrestricted.

Official references:

- [Claude Code MCP remote HTTP configuration](https://code.claude.com/docs/en/mcp)
- [Claude Desktop remote connector setup and limitations](https://support.claude.com/en/articles/11175166-get-started-with-custom-connectors-using-remote-mcp)
- [Cursor MCP documentation](https://cursor.com/docs/context/mcp)
- [MCP Elicitation capability](https://modelcontextprotocol.io/specification/2025-11-25/client/elicitation)
