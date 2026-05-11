# Changelog

All notable changes to this project will be documented in this file.

## [1.3.16] - 2026-05-11

### Feature

- **SQL Server**: Add support `TrustedServerCertificate` and `Encrypt` options in the connection string when testing or using the database connection.

## [1.3.15] - 2026-05-02

### Changed

- **CustomToolProxy Enhancement**:
  - Integrated audit logging capabilities to monitor tool execution.
  - Improved query value parsing for more robust handling of dynamic inputs.
- **SQL Builder Refinement**:
  - Added support for **Table Aliases**, enabling more complex and readable JOIN queries.
  - Refactored **Column Options** to provide a more flexible and scalable configuration structure.

### Fixed

- **Sql Definition Json Builder**: Resolved an issue where the JSON builder incorrectly mapped schema definitions.
- **Custom Tool Param Issue**: Fixed a bug where custom tool parameters failed to pass through the proxy, ensuring correct argument delivery to underlying services.

## [1.3.14] - 2026-04-30

### Features

- **Testing Infrastructure**: Added comprehensive unit and integration test suites for all database strategies (**PostgreSQL**, **MySQL**, **SQLite**, **SQL Server**, **Oracle**) using **Testcontainers**, ensuring reliability across different database engines.
- **Database Management**: Introduced `DbManagementService` and corresponding API controllers to manage database connections and metadata more efficiently.
- **Validation Layers**: Integrated **FluentValidation** on the backend and **VeeValidate** on the frontend to provide robust data validation and improved user feedback.
- **Frontend Enhancements**: Initialized **Vitest** for frontend unit testing and added **MCP configuration** logic to support dynamic tool discovery.
- **Oracle & SQL Server Support**: Fully implemented and validated strategies for Oracle and SQL Server, including container-based integration tests.

### Fix

- **SqlKata Query Builder**: Fixed regressions in the SQL compiler related to subquery alias generation and side effects in column alias handling.
- **Error Mapping**: Improved database error code handling in `BaseStrategy` to provide more accurate troubleshooting hints (e.g., column/table not found).
- **MySQL Compatibility**: Fixed a bug where certain MySQL truncation errors (e.g., Error 1292) were not being correctly intercepted.

### Refactor

- **Code Cleanup**: Streamlined `using` directives and improved general code readability and maintainability across the backend modules.
- **Contribution Guidelines**: Updated `CONTRIBUTING.md` and added issue templates to improve the development workflow.

## [1.3.13] - 2026-04-24

### Features

- **Visual SQL Builder**: Introduced a comprehensive GUI for building SQL Query JSON definitions, allowing users to visually construct complex tools

### Refactor

- **SQL Builder Simplification**: Removed mandatory table alias (`mainAlias`) logic from the SQL builder to simplify the user interface and generated JSON structure.

## [1.3.12] - 2026-04-22

### Features

- Added **Database Management** page.
- Enabled direct association with existing databases when issuing **MCP API Keys**.

## [1.3.11] - 2026-04-21

### Breaking Change

- replace AES encryption with AesGcm for improved security and performance in connection string encryption. This change requires existing encrypted connection strings to be re-encrypted using the new AesGcm-based CryptoService implementation, as the encryption format and key management have been updated for enhanced security.

## [1.3.10] - 2026-04-20

### Feature

- **Custom SQL Tools (Low-Code Tool Plugin System)**: Administrators can now define domain-specific SQL operations (Query or DML) directly from the Admin Panel, exposing them as new MCP tools to the AI agent.
- **Dynamic Parameter Injection**: Introduced `{{parameterName}}` syntax for custom tools, allowing the AI to pass context-aware arguments into pre-defined SQL statements.
- **Admin UI Enhancements**: Added a dedicated management interface for creating, testing, and managing Custom SQL Tools.
- **Documentation Refactoring**: Major overhaul of `README.md` with a high-speed aesthetic, improed information architecture, and integrated Admin Panel snapshots.

## [1.3.9] - 2026-04-19

### Security Issue Fix

- Resource injection For ConnectString
- Clear text storage of sensitive information
- Exposure of private information
- Log entries created from user input

### Breaking Change

- UI Change: Manual entry of ADO.NET connection strings is disabled in the UI.

## [1.3.8] - 2026-04-18

### Improvement

- Remove unnecessary middleware to reduce performance consumption

### Fix

- Fix the issue where the dashboard is not displaying

## [1.3.7] - 2026-04-16

### Feature

- `allowed Tools` For Dynamic Tool List
- implement database connection testing feature 、 global provider support

## [1.3.6] - 2026-04-16

### Feature

- `allowed Tools` Manage tool access for the API key : This feature allows administrators to specify which tools or API endpoints an issued API key has access to. By managing tool access at the API key level, you can enforce fine-grained permissions and restrict certain keys to only use specific functionalities of the MCP API, enhancing security and control over how the API is used.

## [1.3.5] - 2026-04-16

### Fix

- Fix Where Condition IN/NOTIN

## [1.3.4] - 2026-04-12

### Breaking Change

- remove `get_table_reference` tool from MCP API: this tool was rarely used and added complexity to the codebase, and its functionality can be achieved through a combination of `get_tables` and `get_columns` calls. Removing it simplifies the API and reduces maintenance overhead without significantly impacting usability.

### Feature

- Added support some new database providers: `SqlServer`, `Oracle`, and `FireBird`. The MCP API can now handle connections to these additional database types, expanding the range of supported databases for SQL query execution and metadata retrieval.
- Added `execute_dml_safe` tool to MCP API: this new tool allows clients to execute DML statements (INSERT, UPDATE, DELETE) safely through the MCP API, with the same security guards and validation mechanisms as `execute_query_safe`. This expands the capabilities of the MCP API to support data modification operations in addition to query execution.

## [1.3.3] - 2026-04-11

### Refactor

- Refactor SQL strategy classes and improve query handling

## [1.3.2] - 2026-04-09

### Bug Fix

- Fixed MySQL case-insensitive string filters by avoiding unsupported `ILIKE` and using a compatible `LOWER(field) LIKE lower(pattern)` path.
- Fixed `combineConditions` handling for `union all` by normalizing combine type values (`union all`, `union_all`, `unionall`).
- Improved query error output strategy by database type, including PostgreSQL-specific guidance for `42P01` (relation/CTE reference not found).
- Updated `get_tables` flow to require an explicit schema name so table discovery is deterministic across database providers.

## [1.3.1] - 2026-04-08

### Feature

- form validation login and register

## [1.3.0] - 2026-04-08

### Breaking Change

- implement ICryptoService for db connection string encryption and decryption : this is a breaking change that requires existing encrypted connection strings in the database to be re-encrypted using the new CryptoService implementation.

## [1.2.0] - 2026-04-07

### Refactor

- module structure move

### Security

- enhance SQL query execution with security guards and aggregation validation

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
