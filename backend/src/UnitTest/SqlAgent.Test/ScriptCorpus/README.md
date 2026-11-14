# P6 real SQL Script semantic acceptance corpus

The checked-in corpus contains **240 independently named, multi-statement SQL scripts**:
40 per source dialect (PostgreSQL, MySQL, SQLite, SQL Server, Oracle, Firebird).
Rows use realistic inventory, billing, payroll and logistics workflows, with
provider-specific DDL type names and ALTER syntax. These are persisted concrete
inputs, not generated as part of the CI test.

| Scenario family | Per dialect | Required semantic evidence |
| --- | ---: | --- |
| Catalog | 16 | CREATE/ALTER/DROP lifecycle, proven Catalog deltas and Complete coverage |
| Mixed | 8 | DDL followed by valid SELECT/INSERT/UPDATE/DELETE, never executed |
| Invalid | 8 | Duplicate columns/tables and missing/repeated DROP must be Invalid |
| Unsupported | 8 | CHECK constraints and unknown statements must fail closed |

Each case is run through **both** `ParseScript` and `AnalyzeScript`. Assertions
cover source spans and statement order, semantic status, diagnostics, aggregate
Catalog deltas, effect coverage and unknown-state propagation. The test does
not connect to a database and does not compile/execute SQL. It is **not**
equivalent to six-dialect execution parity: provider integration and
capability-specific tests remain separate.

To extend the corpus, add a unique row with a multi-statement SQL workflow,
a stable scenario group, expected statement count and the appropriate proof
checkpoint. Never mark syntax support as semantic proof.
