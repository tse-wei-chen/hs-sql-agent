# 🗺️ Roadmap

## MCP Tools

| Progress | Tool                    | Description                                                                |
| :------- | :---------------------- | :------------------------------------------------------------------------- |
| ✅       | `execute_query_safe`    | Execute a query (supports join, where, order by, group by, limit)          |
| ✅       | `get_columns`           | Get column names and data types of a table                                 |
| ✅       | `get_schemas`           | Get schemas in the database                                                |
| ✅       | `get_tables`            | Get tables in the database                                                 |
| ✅       | `execute_dml_safe`      | Execute a DML statement (INSERT, UPDATE, DELETE) with safety confirmation  |
| 🔜       | `save_query`            | Save query for AI agent                                                    |
| 🔜       | `update_semantic_layer` | Let AI agent update and enrich the Semantic Layer with discovered metadata |

## Admin & Security

| Progress | Feature              | Description                                   |
| :------- | :------------------- | :-------------------------------------------- |
| ✅       | Allowed Tools        | Manage tool access for each API key           |
| ✅       | Per-key Connection   | Override database settings for specific keys  |
| ✅       | Key Management       | Issue, list, and revoke keys in real-time     |
| ✅       | Audit Logging        | Detailed query execution history and metadata |
| ✅       | Rate Limiting        | Global rate limiting                          |
| ✅       | Table WhiteList      | Configure table whitelisting per API key      |
| ✅       | Semantic Layer       | Define DB semantic layer for AI agent         |

## Version History

| Version      | Date       | Highlights                                        |
| ------------ | ---------- | ------------------------------------------------- |
| 1.5.1-alpha  | 2026-05-22 | Bug fix: form create/edit not working             |
| 1.5.0-alpha  | 2026-05-18 | Table whitelist enforcement, SQL builder overhaul |
| 1.4.2-alpha  | 2026-05-16 | Advanced function/arithmetic expressions          |
| 1.4.1-alpha  | 2026-05-15 | SQL function expressions, enhanced arithmetic     |
| 1.4.0-alpha  | 2026-05-14 | Table whitelist, semantic layer, remove manual mode|
| 1.3.17-alpha | 2026-05-13 | UI improvements for SQL multi-select              |
| 1.3.16-alpha | 2026-05-11 | SQL Server TrustedServerCertificate support       |
| 1.3.15-alpha | 2026-05-02 | CustomToolProxy audit integration                 |
| 1.3.14-alpha | 2026-04-30 | Testcontainers infra, DB management, validations  |
| 1.3.13-alpha | 2026-04-24 | Visual SQL Builder GUI                            |
| 1.3.12-alpha | 2026-04-22 | Database Management page                          |
| 1.3.11-alpha | 2026-04-21 | AES → AesGcm encryption upgrade                  |
| 1.3.10-alpha | 2026-04-20 | Custom SQL Tools (low-code plugin system)         |
| 1.3.9-alpha  | 2026-04-19 | Security fixes (connection string, logging)       |
| 1.3.8-alpha  | 2026-04-18 | Performance improvements                          |
| 1.3.7-alpha  | 2026-04-16 | Allowed tools, DB connection testing              |
| 1.3.6-alpha  | 2026-04-16 | Tool access management per API key                |
| 1.3.5-alpha  | 2026-04-16 | Bug fix: WHERE IN/NOTIN conditions               |
| 1.3.4-alpha  | 2026-04-12 | SQL Server/Oracle/FireBird support, DML tool      |
| 1.3.3-alpha  | 2026-04-11 | SQL strategy refactor                            |
| 1.3.2-alpha  | 2026-04-09 | MySQL ILIKE fix, improved error handling          |
| 1.3.1-alpha  | 2026-04-08 | Login/register form validation                   |
| 1.3.0-alpha  | 2026-04-08 | CryptoService for connection string encryption   |
| 1.2.0-alpha  | 2026-04-07 | Module restructure, security enhancements        |
| 1.1.1-alpha  | 2026-04-07 | Bug fix: key-validation mapping                   |
| 1.1.0-alpha  | 2026-04-06 | Auth cache, IP rate limiting, pre-serialized errors|
| 1.0.0-alpha  | 2026-04-05 | Initial release                                  |

> Note: This project is in **alpha** stage. Features and APIs may change.
