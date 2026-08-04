# MCP client onboarding and compatibility

HS SQL Agent exposes a Streamable HTTP MCP endpoint at `/mcp`. Authenticate with
`Authorization: Bearer <MCP key>` (the legacy `X-MCP-Server-Key` header is also
accepted). The Admin Panel generates Claude Desktop, Cursor, and generic HTTP
configuration only immediately after Issue, Rotate, or Duplicate. The plaintext
secret is not stored and cannot be shown again after that dialog is closed.

## Compatibility baseline

The following rows distinguish what this repository actually verifies from client
configuration examples. Do not infer application-level Elicitation support from a
successful connection.

| Client / component | Version | Verified coverage |
| --- | --- | --- |
| HS SQL Agent Admin smoke client | 1.0.0 | Streamable HTTP `initialize` using MCP `2025-11-25`, Bearer authentication, and server `tools` capability; response/error classification is covered by frontend tests. |
| ModelContextProtocol.AspNetCore server SDK | 1.4.0 | Server transport and MCP tool exposure used by this release. |
| Claude Desktop | Operator-installed version | The generated local stdio configuration uses pinned `mcp-remote@0.1.38` to attach the static Bearer header. Record the exact deployed version only after running the one-time smoke test and, for DML, a manual Elicitation decline/accept test. |
| Cursor | Operator-installed version | Current HTTP configuration is generated from the standard `mcpServers` schema. Record the exact deployed version only after the same connection and DML checks. |

Claude Desktop's native remote connectors are configured through **Settings →
Connectors**, not `claude_desktop_config.json`, and do not accept an arbitrary static
Bearer header. HS SQL Agent does not currently expose MCP OAuth, so the generated
Desktop snippet uses the third-party, experimental `mcp-remote` bridge through the
local stdio configuration. It is pinned rather than installed from `latest`, requires
Node.js/npx, and should be security-reviewed before enterprise rollout. Native
Claude Desktop remote configuration remains unavailable until HS SQL Agent supports
MCP OAuth.

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
  Bearer key. Other server errors are reported as inconclusive rather than as an
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
- [`mcp-remote` bridge and custom-header syntax](https://github.com/geelen/mcp-remote)
- [MCP Elicitation capability](https://modelcontextprotocol.io/specification/2025-11-25/client/elicitation)
