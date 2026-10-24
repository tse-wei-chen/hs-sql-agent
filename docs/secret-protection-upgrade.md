# Secret protection upgrade (breaking change)

This release replaces the legacy HMAC-derived encryption format. There is no legacy decryption fallback or automatic conversion. MCP key HMACs and JWT signing remain separate from secret encryption.

Before upgrading:

1. Back up the Admin Store and existing deployment secrets. Stop admission of new approvals, drain Pending approvals, and resolve Executing approvals with the existing operational procedure. Never mark uncertain executions completed just to pass migration. Both database migrations reject outstanding Pending/Executing approvals.
2. Complete any independent witness inspections needed for historical uncertain outcomes before upgrading: legacy encrypted resume payloads will no longer be readable, including historical terminal records. Keep the backup and old application available for read-only forensic work.
3. Generate **three independent** random 32-byte values, each encoded as Base64. For example, run `openssl rand -base64 32` three times. Provide them through protected environment variables or a mounted configuration secret; do not commit them.
4. Set `DB_ENCRYPTION_KEY`, `DML_ENCRYPTION_KEY`, and `DML_FINGERPRINT_KEY` in Docker deployments. The corresponding configuration is `SecretProtection:DatabasePasswords:Keys:db-1`, `SecretProtection:DmlPayloads:Keys:dml-1`, and `SecretProtection:DmlFingerprintKey`. Set each ring's `ActiveKeyId` to its configured key ID. Missing, malformed, short, or reused encryption/fingerprint keys fail startup.
5. Supply explicit `Bootstrap:Databases:*:McpKeys:*:Key` values for new bootstrap keys. Startup no longer generates or logs bootstrap keys. Alternatively, issue keys using the authenticated management API/UI, which returns the new key once. Revoke any keys previously exposed through logs.
6. Deploy all nodes together; do not mix old and new binaries. Retain the same key rings and fingerprint key on every node and across restarts. Re-enter each existing database password through the management API/UI, or provide the password in bootstrap configuration. Existing encrypted passwords fail closed until replaced. Empty passwords used by providers such as SQLite remain supported. Preserve `HMAC_KEY` to keep existing MCP keys usable.

Custom hosts must configure `HsSqlAgentRuntimeOptions.SecretProtection` (or the legacy aggregate's `SecretProtection`) before registering the runtime. `ICryptoService` now accepts a context string instead of caller-supplied key bytes. Database password contexts are `db-password:<SecretBindingId>`; durable execution payload contexts are `dml-payload:<RequestId>`. Applications must use the trusted record's identifier, never an identifier extracted from the ciphertext. The stored `PasswordHash` column still holds recoverable encrypted credentials; it is not a password hash.

New ciphertext uses `v2:<key-id>:<base64 nonce/tag/ciphertext>`. AES-256-GCM authenticates the format header, key ID, purpose, and record identifier. Nonces are fresh 12-byte random values and tags are 16 bytes. Null/absent passwords remain absent; empty and whitespace strings passed for encryption are encrypted exactly.

For encryption key rotation, first distribute the old and new keys to every node under distinct IDs. Then change `ActiveKeyId` everywhere. Reads use the envelope's key ID; writes use the active key. Re-save database passwords to re-encrypt them, and retain old DML keys for every pending or historical payload that still needs inspection. Retire a key only after no required ciphertext uses it. Docker Compose includes the active key mapping; supply additional retained IDs through an override file or mounted configuration. The fingerprint key is independent and is not versioned: rotate it only after draining approvals, and retain the prior deployment secrets for historical witness inspection.

`ExtraSettings` accepts only JSON boolean `Encrypt` and `TrustServerCertificate` fields. Unknown fields, duplicate fields, string values and invalid JSON are rejected on database configuration writes, bootstrap provisioning and connection tests. Sensitive extension settings are deliberately unsupported; adding them requires a dedicated encrypted storage and write-only API contract.

Connection tests and health probes return `DB_CONNECTION_FAILED` with a generic message instead of database-driver exception text. Failed probes do not persist raw driver messages.

The schema adds a stable, unique `SecretBindingId` for each database connection and removes the PostgreSQL ciphertext length limit. Rollback requires restoring the pre-upgrade backup and old deployment configuration; migration downgrade is intentionally unsupported. The existing human approval and commit-time DML revalidation flow remains required.
