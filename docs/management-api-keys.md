# Management API Keys (machine-to-machine administration)

Issue: [#291](https://github.com/tse-wei-chen/hs-sql-agent/issues/291)

Management API Keys allow trusted backends to call existing **HsSqlAgent administration REST APIs** without an interactive human JWT. They are **not MCP keys**, and cannot authenticate to `/mcp`, access host-app routes, manage human accounts/roles, or issue other Management API keys.

## Create a key
1. Sign into the Admin UI with a human account authorized for `/runtime/security.edit`.
2. Navigate to **Management API Keys**.
3. Choose a name, expiration (up to 365 days), and explicit canonical permissions. For SaaS provisioning, select the needed subset of `/runtime/custom-tools.*` and `/runtime/mcp-keys.*`.
4. Issue and copy the `hsa_mgmt_` secret shown exactly once. Store it in a server-side secret manager.

The server persists a SHA-256 digest of a 256-bit random key and a short non-secret prefix; list operations **never** return plaintext. Rotate revokes the old key immediately and issues a replacement with the same grants and expiry. Revoke takes effect immediately on subsequent requests.

## Call existing APIs

Use the `X-API-Key` header for Management API Keys. `Authorization: Bearer <JWT>` remains reserved for human admin authentication. A request with both headers is rejected rather than falling back to another identity.
 

```bash
curl -X POST "https://agent.example.com/api/CustomSqlTool" \
  -H "X-API-Key: ${HS_MANAGEMENT_API_KEY}" \
  -H "Content-Type: application/json" \
  -d '{"name":"saas_demo_orders","type":"Query","dbManagementId":1,"sqlTemplate":"SELECT id FROM orders WHERE company_id = 9527"}'
```

Publish the returned Custom Tool ID through the existing `POST /api/CustomSqlTool/{id}/publish` and issue a **separate MCP access key** through `POST /api/runtime/mcp-keys`. A Management API Key's `Permissions` are different from MCP `AllowedTools`.

## Authorization and isolation
- Valid management credentials receive 403 when the endpoint's canonical permission is not granted; missing or invalid credentials receive 401 through the existing Admin API authorization boundary.
- Only a strict subset of existing canonical permissions can be issued. No machine key can grant `/auth/*` permissions, security-policy edit, or management-key administration.
- Access is **not row-/tenant-isolated** and currently **not scoped to a specific database ID**. A granted management permission applies to matching managed resources. Integrators must enforce SaaS tenancy themselves and should not hand a key to a browser or untrusted customer.
- Keys are checked against persistent state per request, avoiding stale authorization after revocation.
- Review the existing Audit view for machine actor `management-api-key`.

Host applications retain their own authorization policy for human calls; this credential is understood only inside HsSqlAgent Admin controllers' canonical-permission filter. Always use HTTPS, rotate keys periodically, and restrict inbound networks where possible.
