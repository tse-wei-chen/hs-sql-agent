# MCP client onboarding and compatibility

HS SQL Agent exposes a Streamable HTTP MCP endpoint at `/mcp`, but normal client onboarding does **not** require manually assembling the endpoint and header configuration.

The first-party Admin Panel generates ready-to-paste MCP client configuration immediately after **Issue**, **Rotate**, or **Duplicate** creates a new plaintext MCP key. The one-time **Save and connect** dialog contains:

- the plaintext MCP key;
- the server-configured MCP endpoint;
- a **Claude Desktop** configuration tab;
- a **Cursor** configuration tab;
- a **Visual Studio Code** configuration tab;
- a **Generic HTTP** configuration tab;
- a copy button for each generated configuration.

Choose the client tab, copy the generated configuration, and paste it into that client's MCP configuration. The generated snippet already includes the endpoint and `X-MCP-Server-Key` header.

The plaintext secret is deliberately not stored. Closing the **Save and connect** dialog permanently removes that plaintext value from the Admin UI, so the same generated configuration cannot be reconstructed later from the stored key record. Rotate or duplicate the key if a new plaintext secret and generated configuration are needed.

## Recommended onboarding flow

1. Configure the server's externally reachable MCP endpoint.
2. In **Runtime → MCP Keys**, issue a key for the target database and select the minimum required tools/tables.
3. In the **Save and connect** dialog, select Claude Desktop, Cursor, Visual Studio Code, or Generic HTTP.
4. Click the corresponding **Copy ... config** button.
5. Paste the copied JSON into the MCP client's configuration and connect.
6. If the key can invoke DML, test both Elicitation decline and accept paths with the exact deployed client version.

Rotate and Duplicate use the same onboarding dialog because both operations create a new plaintext secret. Editing an existing key does not reveal the old secret again.

## Public endpoint configuration

Configure the externally reachable endpoint on the server. Do not derive it from the Admin UI origin, because the UI and MCP endpoint may use different hosts, ports, or reverse-proxy paths:

```json
"Mcp": {
  "PublicEndpoint": "https://sql-agent.example.com/mcp"
}
```

The equivalent environment variable is `Mcp__PublicEndpoint`. The provided Compose file maps `MCP_PUBLIC_ENDPOINT` to it. The authenticated Admin UI reads this value from `GET /api/runtime/client-config` and uses it when generating the one-time client configuration. Non-Development startup requires the setting, and the server rejects values that are not absolute HTTP or HTTPS URLs.

Set the production endpoint correctly **before** issuing production keys so the copied client configuration contains the externally reachable URL.

## Manual / generic HTTP form

For clients that do not use one of the generated client-specific shapes, the Generic HTTP tab produces the equivalent Streamable HTTP connection object. At the protocol level, authentication is:

```http
X-MCP-Server-Key: <MCP key>
```

This manual form is a fallback/reference. For Claude Desktop, Cursor, and Visual Studio Code, prefer the generated configuration from the key dialog so users do not have to manually reassemble the endpoint and secret.

## Compatibility baseline

The following rows distinguish what this repository actually generates from application-level behavior that still needs to be tested against the deployed client version. A successful connection does not prove DML Elicitation support.

| Client / component | Version | Verified coverage |
| --- | --- | --- |
| ModelContextProtocol.AspNetCore server SDK | 1.4.0 | Server transport and MCP tool exposure used by this release. |
| Claude Desktop | Operator-installed version | The Admin Panel generates a direct HTTP `mcpServers` entry with the `X-MCP-Server-Key` header. Record the exact deployed version only after testing the copied configuration and, for DML, a manual Elicitation decline/accept test. |
| Cursor | Operator-installed version | The Admin Panel generates an HTTP `mcpServers` entry with the endpoint and `X-MCP-Server-Key` header. Validate the copied configuration and DML behavior against the installed version. |
| Visual Studio Code | Operator-installed version | The Admin Panel generates a `servers` entry with `type: "http"`, the endpoint, and `X-MCP-Server-Key` header. Validate the copied configuration and DML behavior against the installed version. |
| Generic Streamable HTTP client | Client-specific | The Admin Panel generates a connection object with `type: "streamable-http"`, the endpoint, and `X-MCP-Server-Key` header. Adapt only the surrounding client-specific configuration if required. |

The generated Claude Desktop snippet connects directly to the Streamable HTTP endpoint and does not install or execute a third-party stdio bridge.

Client behavior changes over time, so generated configuration and the deployed client version must be validated together rather than claiming an untested version here.

## DML and Elicitation

`execute_dml_sql` and published DML Custom Tools require form Elicitation. For UPDATE and DELETE, the server first reads the matched rows in a preview transaction without executing the mutation, binds a one-time challenge to the validated compiled plan, policy version, affected row count, and row-set fingerprint, then sends `elicitation/create`. INSERT VALUES previews the immutable payload and binds the challenge to that exact compiled plan instead of a pre-existing row set.

After the human accepts, the server validates and consumes the challenge. For row-set mutations it opens the commit transaction, re-queries the matched rows and compares the current row-set fingerprint before executing the exact compiled mutation. A changed plan, policy, challenge, row set, or affected row count cancels the operation instead of committing. If the client does not declare and implement form Elicitation, the operation is refused.

Before allowing DML in production, test the exact client version by invoking DML and verify both Decline and Accept paths. Query-only keys do not require Elicitation; restrict their allowed tool list instead of leaving it unrestricted.

Official references:

- [Claude Code MCP remote HTTP configuration](https://code.claude.com/docs/en/mcp)
- [Claude Desktop remote connector setup and limitations](https://support.claude.com/en/articles/11175166-get-started-with-custom-connectors-using-remote-mcp)
- [Cursor MCP documentation](https://cursor.com/docs/context/mcp)
- [Visual Studio Code MCP servers](https://code.visualstudio.com/docs/copilot/chat/mcp-servers)
- [MCP Elicitation capability](https://modelcontextprotocol.io/specification/2025-11-25/client/elicitation)
