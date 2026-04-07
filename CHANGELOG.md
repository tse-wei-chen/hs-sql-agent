# Changelog

All notable changes to this project will be documented in this file.

## [1.1.1] - 2026-04-07

### Bug Fix
- Fixed a key-validation mapping issue where `SqlConnectionString` was not returned in `McpAccessKeyValidationResult`, which could cause MCP runtime database connection resolution to fail for valid keys.


## [1.1.0] - 2026-04-06

### Performance & Security
- **Robust MCP Key Authentication Cache:** Implemented `IMemoryCache` with a SHA256 hashed cache-key and Striped Locking mechanism (`SemaphoreSlim`) to safely prevent cache stampede issues in high-concurrency environments while maintaining minimal memory overhead.
- **Global IP-based Rate Limiting:** Simplified the rate limiting flow by removing per-key dynamic rate limit overrides. The MCP API now strictly relies on a global, IP-bucketed rate limiter configurable via `appsettings.json`, ensuring stable traffic control before authentication checks.
- **Pre-serialized Error Responses:** Optimized overhead by returning pre-serialized byte arrays for frequent error responses (`401 Unauthorized`, `403 Forbidden`) during key evaluation.

### Refactor
- Stripped rate limit properties (`PermitLimitOverride`, `WindowSecondsOverride`, `QueueLimitOverride`) out of the database entity `McpAccessKey`, ViewModels, and the `McpAccessKeyService`.
- Reordered ASP.NET Core middleware in the `/mcp` pipeline to execute rate limiting prior to identity validation.
- Cleaned up the frontend UI (`index.vue`) by safely removing unused rate limiting fields and logic during API key issuance.


## [1.0.0] - 2026-04-05

### Feature
- Initial release of hs-sql-agent backend and frontend modules.