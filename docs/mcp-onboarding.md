# MCP client onboarding and compatibility

HS SQL Agent exposes a Streamable HTTP MCP endpoint at `/mcp`. Authenticate with
`X-MCP-Server-Key: <MCP key>`. The Admin Panel generates direct HTTP configuration
for Claude Desktop, Cursor, and generic Streamable HTTP clients only immediately
after Issue, Rotate, or Duplicate. The plaintext secret is not stored and cannot
be shown again after that dialog is closed.

Configure the externally reachable endpoint on the server. Do not derive it from
the Admin UI origin, because the UI and MCP endpoint may use different hosts,
ports, or reverse-proxy paths:

```json
"Mcp": {
  "PublicEndpoint": "https://sql-agent.example.com/mcp"
}
```

The equivalent environment variable is `Mcp__PublicEndpoint`. The provided
Compose file maps `MCP_PUBLIC_ENDPOINT` to it. The authenticated Admin UI reads
this value from `GET /api/runtime/client-config` and displays it as read-only.
Non-Development startup requires the setting, and the server rejects values that
are not absolute HTTP or HTTPS URLs.

## Compatibility baseline

The following rows distinguish what this repository actually verifies from client
configuration examples. Do not infer application-level Elicitation support from a
successful connection.

| Client / component | Version | Verified coverage |
| --- | --- | --- |
| ModelContextProtocol.AspNetCore server SDK | 1.4.0 | Server transport and MCP tool exposure used by this release. |
| Claude Desktop | Operator-installed version | A direct HTTP `mcpServers` entry with the `X-MCP-Server-Key` header is generated. Record the exact deployed version only after testing the generated configuration and, for DML, a manual Elicitation decline/accept test. |
| Cursor | Operator-installed version | Current HTTP configuration is generated from the standard `mcpServers` schema. Record the exact deployed version only after the same connection and DML checks. |

The generated Claude Desktop snippet connects directly to the Streamable HTTP
endpoint and does not install or execute a third-party stdio bridge.

Cursor supports remote Streamable HTTP MCP servers and custom headers through its
MCP configuration. Client behavior changes over time, so generated configuration
and the deployed client version must be validated together rather than claiming an
untested version here.

## DML and Elicitation

`execute_dml_sql` and published DML Custom Tools require form Elicitation. For
UPDATE and DELETE, the server first reads the matched rows in a preview transaction
without executing the mutation, binds a one-time challenge to the validated
compiled plan, policy version, affected row count, and row-set fingerprint, then
sends `elicitation/create`. INSERT VALUES previews the immutable payload and binds
the challenge to that exact compiled plan instead of a pre-existing row set.

After the human accepts, the server validates and consumes the challenge. For
row-set mutations it opens the commit transaction, re-queries the matched rows and
compares the current row-set fingerprint before executing the exact compiled
mutation. A changed plan, policy, challenge, row set, or affected row count cancels
the operation instead of committing. If the client does not declare and implement
form Elicitation, the operation is refused.

Before allowing DML in production, test the exact client version by invoking DML
and verify both Decline and Accept paths. Query-only keys do not require Elicitation;
restrict their allowed tool list instead of leaving it unrestricted.

Official references:

- [Claude Code MCP remote HTTP configuration](https://code.claude.com/docs/en/mcp)
- [Claude Desktop remote connector setup and limitations](https://support.claude.com/en/articles/11175166-get-started-with-custom-connectors-using-remote-mcp)
- [Cursor MCP documentation](https://cursor.com/docs/context/mcp)
- [MCP Elicitation capability](https://modelcontextprotocol.io/specification/2025-11-25/client/elicitation)