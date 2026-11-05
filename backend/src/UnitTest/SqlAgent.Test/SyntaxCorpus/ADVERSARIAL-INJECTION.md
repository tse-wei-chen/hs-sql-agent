# Adversarial SQL injection campaign

This campaign lives in CoreAdversarialInjectionMatrixTests.cs and runs in the existing SqlAgent.Test CI job. It executes no SQL against production or external databases.

## Coverage floor

- 768 deterministic stacked-statement attempts across six source and target dialects and the Query and DML compilation entries.
- 192 stacked-statement attempts against the single-statement DDL compilation entry (the explicit DDL batch API is a separate contract).
- 48 malformed lexical and control-character attempts.
- 24 physical-table authorization attacks through direct queries, CTEs, joins, and nested subqueries.
- 24 comment-obfuscated unbounded DML attempts, requiring typed Policy diagnostics.
- 144 positive controls where SQL-injection-shaped content must remain parameterized data, not executable SQL.
- Six-provider Query, DML, and DDL positive control paths.

Total: **1,200 adversarial/parameter-safety cases**, plus positive baseline controls. Counts are deterministic floors and must not be silently reduced.

## What a passing run proves

The text-based single-statement compiler entries reject attempted statement stacking, and rejected Query/DML attempts retain repeatable failure evidence with no executable plan fingerprint. The DML constant-predicate cases require a typed policy-stage diagnosis. Approved physical table names are evaluated using compiler binding rather than substring matches. Valid SQL string literals are preserved as command parameters instead of being interpolated into native SQL.

A rejected SQL string is not necessarily a syntax error: the correct typed boundary (lexical, parse, binding, source/target capability, or policy) must be asserted whenever the case is designed to exercise a particular layer. Existing syntax-contract and DML policy suites cover more precise stage checks.

## Non-goals and next security gates

This is a deterministic compiler-level campaign, not a penetration test or proof of end-to-end security. It does not replace Host-level authorization, approval replay/tamper tests, six real-provider differential tests, per-session MySQL and SQL Server quoting modes, or bounded adversarial fuzz/property testing. Extend those independently without confusing static acceptance, runtime execution, and authorized commit.

The corpus contains standard SQL control statements solely as negative inputs. No command is sent to a database in this suite.
