# Changelog

All notable changes to this project will be documented in this file.
## [1.8.2-alpha] - 2026-06-02
## What's Changed
* fix: add missing Dapper package reference causing runtime assembly load failure by @Copilot in https://github.com/tse-wei-chen/hs-sql-agent/pull/54
* fix: move assignment of issuedPlaintextKey to after form reset for co… by @tse-wei-chen in https://github.com/tse-wei-chen/hs-sql-agent/pull/56
* fix: error 500 for connect to mcp endpoint by @tse-wei-chen in https://github.com/tse-wei-chen/hs-sql-agent/pull/58

## New Contributors
* @Copilot made their first contribution in https://github.com/tse-wei-chen/hs-sql-agent/pull/54

**Full Changelog**: https://github.com/tse-wei-chen/hs-sql-agent/compare/v1.8.1-alpha...v1.8.2-alpha

## [1.8.1-alpha] - 2026-05-30

### NuGet Package Release
fix package use

## [1.8.0-alpha] - 2026-05-30

### NuGet Package Release

`HsSqlAgent.Server` is now available as a NuGet package — embed the full MCP SQL Agent into any existing ASP.NET Core application.

```bash
dotnet add package HsSqlAgent.Server
```

```csharp
builder.Services.AddHsSqlAgent(options => { ... });
app.UseHsSqlAgent();
// app.UseHsSqlAgent().ServeAdminUi();  // optional Admin UI
```

- **Admin UI embedded in DLL**: The frontend SPA is compiled and embedded as assembly resources (`EmbeddedFileProvider`). No external `wwwroot` files to deploy — the UI ships inside the NuGet package.
- **Dual-mode pipeline**: `UseHsSqlAgent()` for API-only (MCP + Admin API), `.ServeAdminUi()` to also serve the built-in Admin UI.
- **Project references included**: `Admin.Service`, `Common`, `SqlAgent.Service`, and `SqlKata.*` are automatically bundled in the package via `CopyProjectReferencesToPackage`.
- **GitHub Actions workflow**: New `.github/workflows/nuget-publish.yml` to publish on release or manually via `workflow_dispatch`.

### Docker

- **Fixed duplicate frontend build**: The `CompileFrontend` MSBuild target now skips `pnpm install/generate` when `wwwroot/index.html` already exists (e.g., Docker pre-populated). The existing files are still embedded as resources.
- **Frontend dist goes to HsSqlAgent.Server**: Dockerfile copies frontend output to `HsSqlAgent.Server/wwwroot/` instead of `ToolBox/wwwroot/`, aligning with the embedded resource strategy.

### Infrastructure

- Added `Microsoft.Extensions.FileProviders.Embedded` package reference.
- Added `<IsPackable>true</IsPackable>` to `HsSqlAgent.Server.csproj`.
- Added `NODE_ENV=production` to the `pnpm generate` step in `CompileFrontend`.
- Documentation moved to [GitHub Wiki](https://github.com/tse-wei-chen/hs-sql-agent/wiki) — main README simplified with links.

## [1.7.0-alpha] - 2026-05-27

### Breaking Change

**Custom Tools saved with the old query definition schema will fail to execute.** Existing saved query definitions (in `CustomTool.JsonDefinition`) that use the removed types `"type": "in"`, `SelectArithmeticCondition`, or `SqlFunctionArgument` can no longer be deserialized. Affected Custom Tools must be rebuilt or their JSON definition updated to the new schema.

- **Removed `InWhereCondition`**: IN/NOT IN is now handled by `BasicWhereCondition` via `Operator = "IN"` + `Values` array. The `type: "in"` discriminator no longer exists.
- **Removed `SelectArithmeticCondition`** (5 types: `FieldArithmeticCondition`, `ConstantArithmeticCondition`, `OperationArithmeticCondition`, `FunctionArithmeticCondition`, `CaseWhenArithmeticCondition`): Arithmetic operands now use the unified `SelectCondition` type directly, eliminating the redundant parallel hierarchy.
- **Removed `SqlFunctionArgument`** (4 types: `FieldFunctionArgument`, `ConstantFunctionArgument`, `NestedFunctionArgument`, `ArithmeticFunctionArgument`): Function arguments now use `SelectCondition` directly, same type as SELECT columns.

### Refactor

- **Backend** (`SqlAgent.Service`):
  - `BasicWhereCondition` gained `Values` property for IN support.
  - `OperationSelectCondition.Left`/`Right` changed from `SelectArithmeticCondition` to `SelectCondition`.
  - `FunctionSelectCondition.Arguments` changed from `List<SqlFunctionArgument>` to `List<SelectCondition>`.
  - Same change applied to `SqlFunctionCondition.Arguments`, `FunctionOrderByCondition.Arguments`, `FunctionGroupByCondition.Arguments`.
  - `ResolveSource()` alias resolution fixed: outer `Alias` now takes priority over `fromQuery.Alias`.
- **Frontend** (`query-definition.ts`, `useSqlBuilder.ts`, `SqlBuilderTabWhere.vue`):
  - Type definitions fully aligned with new backend schema.
  - `"in"` type removed from `WhereItem` union; IN conditions mapped to `type: "basic"` + `operator: "IN"`.
  - UI: IN is no longer a separate dropdown option — shown automatically when operator is `IN`/`NOT IN`.

## [1.6.0-alpha] - 2026-05-24

### Feature

- **Semantic Layer Update MCP Tool**: Added `UpdateSemanticLayer` MCP tool to upsert display names and descriptions for tables/columns, enriching schema discovery results.
- **CI/CD Pipeline**: Added GitHub Actions workflow (`.github/workflows/test.yml`) running `Admin.Test`, `ToolBox.Test`, `SqlAgent.Test`, and frontend tests on release.

### Infrastructure

- New `Infrastructure.csproj` project with corresponding test project.
- Added `ToolBox.Test`, `Common.Test`, `Infrastructure.Test` to solution.

### Tests

- **McpAccessKeyAuthMiddlewareTests** (+221 lines): Comprehensive coverage for MCP auth middleware — valid/invalid keys, missing Authorization, `X-MCP-Server-Key` header fallback, CORS origin enforcement, and context item propagation.
- **CustomToolProxyTests** (+196 lines): Tests for tool not found, parameter replacement, missing SQL config, DML execution, and audit logging on success/failure.
- **SanityTests**: Added to both `Common.Test` and `Infrastructure.Test`.

### Fix

- **Frontend `useVModel` removal**: Replaced `useVModel` (from `@vueuse/core`) with native `computed` getter/setter in `Input.vue`, `Textarea.vue`, and `PasswordInput.vue` to reduce dependency footprint. Added `value` prop as fallback for `modelValue`.
- **PasswordInput.vue**: Renamed internal `value` computed → `model` to avoid prop name collision; added `value` prop support.
- **db-management Select binding**: Fixed `Select` component to use explicit `:model-value` / `@update:model-value` instead of `v-bind="field"` for proper v-model compatibility.

## [1.5.1-alpha] - 2026-05-22

## fix form create/edit not work

## [1.5.0-alpha] - 2026-05-18

### Breaking Change

**Custom tools must be rebuilt** — old custom tools will fail. please rebuild the custom tools.

- **Table access validation**: Queries & DML now verify every referenced table against an API key whitelist (`McpContextItemKeys.TableWhitelist`). Missing permissions throw `UnauthorizedAccessException`.
- **Frontend type system overhaul**: All query definition interfaces migrated to **discriminated polymorphic union types** (e.g., `SelectCondition = FieldSelectCondition | OperationSelectCondition | FunctionSelectCondition | ...`). Old-format saved query definitions are **incompatible**.
- **Recursive table reference collection**: `CollectReferencesAndAliases` now traverses nested `FunctionSelectCondition`, `OperationSelectCondition`, `CaseWhenSelectCondition`, subquery arguments, and window definitions. Old custom tools that relied on simpler traversal may miss table references.
- **SqlKata.Service submodule** updated.

### Feature

- **Table whitelist enforcement** — `CustomToolProxy.ValidateAllTableAccess()` for both `QueryDefinition` and `DmlDefinition`.
- **SQL builder frontend enhancements**:
  - JOIN type selection, alias, and ON conditions reworked.
  - Function-based + field-based ordering with FILTER clauses.
  - Full polymorphic condition editor (basic, column_compare, IN, subquery, nested groups).
  - Comprehensive CRUD for select columns (arithmetic, function, case_when, subquery), order/group/having conditions.

### Refactor

- `HavingOpToSql` → `GetOperatorString` for clarity.
- `CollectFrom*` methods extracted for reuse across proxy validation and execution.
- `CollectFromQueryDefinition` now also collects HAVING, ORDER BY, GROUP BY references.
- `useSqlBuilder.ts` code readability & formatting improvements.

### Tests

- **+506 lines** of new strategy tests covering Orders table JOINs, WHERE, HAVING, ORDER BY, GROUP BY, window functions, CASE WHEN, arithmetic expressions, CTEs, and UNION.
- All strategy test files (SQLite, Postgres, MySql, SqlServer, Oracle, Firebird) updated to pass the extended test matrix.

## [1.4.2-alpha] - 2026-05-16

### Changed

- **Feature**:
  - Refactor and expand SQL model classes to support advanced function and arithmetic expressions in query building.
  - Introduce `SelectArithmeticCondition`, `SqlFunctionCondition`, and `SqlFunctionArgument` models for flexible, nested SQL function and arithmetic support.
  - Enable recursive arithmetic and function composition in select, group by, order by, and having clauses.
  - Add rich metadata and validation for each model property, improving API documentation and client usability.

#### Details:

- `SelectArithmeticCondition` now supports:
  - Field, constant, left/right operands, operator, and nested function or arithmetic nodes for complex expressions.
  - Example: Support for expressions like `(price * quantity) * (1 - discount)` and nested SQL functions.
- `SqlFunctionCondition`:
  - Represents SQL functions (aggregate/scalar) with a name and ordered argument list.
  - Supports nesting and composition (e.g., `ROUND(AVG(price), 2)`).
- `SqlFunctionArgument`:
  - Allows each function argument to be a field, constant, nested function, or arithmetic expression.
  - Enables deep composition for advanced SQL scenarios.
- All new/updated models are used in select, group by, order by, and having clauses for full query flexibility.

> **Note:** These changes are not breaking but significantly enhance the expressiveness and composability of SQL queries generated by the agent.

## [1.4.1-alpha] - 2026-05-15

### Changed

- **Feature**:
  - Add support for SQL function expressions
  - Enhance arithmetic conditions

## [1.4.0-alpha] - 2026-05-14

### Breaking Change

- **Remove "Configure Manually" mode**: MCP API keys now require association with a Database Management entry (`DbManagementId`). Legacy manual connection string input has been removed from both the API and UI.
- Old MCP keys will no longer be able to connect to the database. Please regenerate new MCP keys.

### Feature

- **Table Whitelist**: Administrators can now restrict each MCP API key to specific database tables. When a whitelist is configured, `get_tables` results are filtered, and `get_columns` / `execute_query_safe` / `execute_dml_safe` will reject access to non-whitelisted tables.
- **Semantic Layer**: Added a semantic metadata layer for databases, allowing administrators to define display names and descriptions for tables and columns. Semantic data is automatically merged into `get_tables` and `get_columns` MCP tool responses.
- **Dynamic Breadcrumb**: The layout breadcrumb now dynamically reflects the current route path with proper labels and navigation links.

## [1.3.17-alpha] - 2026-05-13

### Feature

- UI improve for sql multi select and display.

## [1.3.16-alpha] - 2026-05-11

### Feature

- **SQL Server**: Add support `TrustedServerCertificate` and `Encrypt` options in the connection string when testing or using the database connection.

## [1.3.15-alpha] - 2026-05-02

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

## [1.3.14-alpha] - 2026-04-30

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

## [1.3.13-alpha] - 2026-04-24

### Features

- **Visual SQL Builder**: Introduced a comprehensive GUI for building SQL Query JSON definitions, allowing users to visually construct complex tools

### Refactor

- **SQL Builder Simplification**: Removed mandatory table alias (`mainAlias`) logic from the SQL builder to simplify the user interface and generated JSON structure.

## [1.3.12-alpha] - 2026-04-22

### Features

- Added **Database Management** page.
- Enabled direct association with existing databases when issuing **MCP API Keys**.

## [1.3.11-alpha] - 2026-04-21

### Breaking Change

- replace AES encryption with AesGcm for improved security and performance in connection string encryption. This change requires existing encrypted connection strings to be re-encrypted using the new AesGcm-based CryptoService implementation, as the encryption format and key management have been updated for enhanced security.

## [1.3.10-alpha] - 2026-04-20

### Feature

- **Custom SQL Tools (Low-Code Tool Plugin System)**: Administrators can now define domain-specific SQL operations (Query or DML) directly from the Admin Panel, exposing them as new MCP tools to the AI agent.
- **Dynamic Parameter Injection**: Introduced `{{parameterName}}` syntax for custom tools, allowing the AI to pass context-aware arguments into pre-defined SQL statements.
- **Admin UI Enhancements**: Added a dedicated management interface for creating, testing, and managing Custom SQL Tools.
- **Documentation Refactoring**: Major overhaul of `README.md` with a high-speed aesthetic, improed information architecture, and integrated Admin Panel snapshots.

## [1.3.9-alpha] - 2026-04-19

### Security Issue Fix

- Resource injection For ConnectString
- Clear text storage of sensitive information
- Exposure of private information
- Log entries created from user input

### Breaking Change

- UI Change: Manual entry of ADO.NET connection strings is disabled in the UI.

## [1.3.8-alpha] - 2026-04-18

### Improvement

- Remove unnecessary middleware to reduce performance consumption

### Fix

- Fix the issue where the dashboard is not displaying

## [1.3.7-alpha] - 2026-04-16

### Feature

- `allowed Tools` For Dynamic Tool List
- implement database connection testing feature 、 global provider support

## [1.3.6-alpha] - 2026-04-16

### Feature

- `allowed Tools` Manage tool access for the API key : This feature allows administrators to specify which tools or API endpoints an issued API key has access to. By managing tool access at the API key level, you can enforce fine-grained permissions and restrict certain keys to only use specific functionalities of the MCP API, enhancing security and control over how the API is used.

## [1.3.5-alpha] - 2026-04-16

### Fix

- Fix Where Condition IN/NOTIN

## [1.3.4-alpha] - 2026-04-12

### Breaking Change

- remove `get_table_reference` tool from MCP API: this tool was rarely used and added complexity to the codebase, and its functionality can be achieved through a combination of `get_tables` and `get_columns` calls. Removing it simplifies the API and reduces maintenance overhead without significantly impacting usability.

### Feature

- Added support some new database providers: `SqlServer`, `Oracle`, and `FireBird`. The MCP API can now handle connections to these additional database types, expanding the range of supported databases for SQL query execution and metadata retrieval.
- Added `execute_dml_safe` tool to MCP API: this new tool allows clients to execute DML statements (INSERT, UPDATE, DELETE) safely through the MCP API, with the same security guards and validation mechanisms as `execute_query_safe`. This expands the capabilities of the MCP API to support data modification operations in addition to query execution.

## [1.3.3-alpha] - 2026-04-11

### Refactor

- Refactor SQL strategy classes and improve query handling

## [1.3.2-alpha] - 2026-04-09

### Bug Fix

- Fixed MySQL case-insensitive string filters by avoiding unsupported `ILIKE` and using a compatible `LOWER(field) LIKE lower(pattern)` path.
- Fixed `combineConditions` handling for `union all` by normalizing combine type values (`union all`, `union_all`, `unionall`).
- Improved query error output strategy by database type, including PostgreSQL-specific guidance for `42P01` (relation/CTE reference not found).
- Updated `get_tables` flow to require an explicit schema name so table discovery is deterministic across database providers.

## [1.3.1-alpha] - 2026-04-08

### Feature

- form validation login and register

## [1.3.0-alpha] - 2026-04-08

### Breaking Change

- implement ICryptoService for db connection string encryption and decryption : this is a breaking change that requires existing encrypted connection strings in the database to be re-encrypted using the new CryptoService implementation.

## [1.2.0-alpha] - 2026-04-07

### Refactor

- module structure move

### Security

- enhance SQL query execution with security guards and aggregation validation

## [1.1.1-alpha] - 2026-04-07

### Bug Fix

- Fixed a key-validation mapping issue where `SqlConnectionString` was not returned in `McpAccessKeyValidationResult`, which could cause MCP runtime database connection resolution to fail for valid keys.

## [1.1.0-alpha] - 2026-04-06

### Performance & Security

- **Robust MCP Key Authentication Cache:** Implemented `IMemoryCache` with a SHA256 hashed cache-key and Striped Locking mechanism (`SemaphoreSlim`) to safely prevent cache stampede issues in high-concurrency environments while maintaining minimal memory overhead.
- **Global IP-based Rate Limiting:** Simplified the rate limiting flow by removing per-key dynamic rate limit overrides. The MCP API now strictly relies on a global, IP-bucketed rate limiter configurable via `appsettings.json`, ensuring stable traffic control before authentication checks.
- **Pre-serialized Error Responses:** Optimized overhead by returning pre-serialized byte arrays for frequent error responses (`401 Unauthorized`, `403 Forbidden`) during key evaluation.

### Refactor

- Stripped rate limit properties (`PermitLimitOverride`, `WindowSecondsOverride`, `QueueLimitOverride`) out of the database entity `McpAccessKey`, ViewModels, and the `McpAccessKeyService`.
- Reordered ASP.NET Core middleware in the `/mcp` pipeline to execute rate limiting prior to identity validation.
- Cleaned up the frontend UI (`index.vue`) by safely removing unused rate limiting fields and logic during API key issuance.

## [1.0.0-alpha] - 2026-04-05

### Feature

- Initial release of hs-sql-agent backend and frontend modules.
