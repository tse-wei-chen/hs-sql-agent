namespace HsSqlAgent.SqlCore.Models;

/// <summary>
/// Single target-provider contract for nested CTE fragments. PostgreSQL, MySQL, and SQLite have
/// declared lowering for WITH inside derived tables, set branches, scalar/EXISTS roots, and CTE
/// definitions; Oracle, SQL Server, and Firebird remain fail-closed for those nested forms.
/// Statement-root CTE placement and scope-sensitive set-tail rules remain separate contracts.
/// </summary>
internal static class SqlNestedCteCapabilityRules
{
    internal static bool SupportsTarget(SqlAgentToolType provider) =>
        provider is SqlAgentToolType.Postgres
            or SqlAgentToolType.MySQL
            or SqlAgentToolType.Sqlite;

    internal static SqlCapability DerivedMatrixCapability(
        SqlAgentToolType provider) =>
        new(
            "select.cte_derived",
            "query",
            Status(provider),
            SupportsTarget(provider)
                ? "Derived-table-local CTEs are compiled as complete target subqueries and reattached with ordered bindings, preserving lexical scope without CTE hoisting. The Core provider compiler applies the same rewrite before every nested SELECT, so query, scalar/EXISTS, and DML subqueries share this behavior; derived CTE set queries with an outer tail are included."
                : provider == SqlAgentToolType.Oracle
                    ? "Oracle rejects WITH clauses nested inside parenthesized subqueries, so derived-table-local CTEs fail closed instead of emitting ORA-32034-prone SQL."
                    : provider == SqlAgentToolType.MsSqlServer
                        ? "SQL Server has no declared portable WITH-at-the-start-of-a-general-derived-subquery contract in the Core target profile, so derived-table-local CTEs fail closed in query and DML contexts."
                        : "Firebird nested CTE placement is kept fail-closed in query and DML contexts until a target-profile contract is modeled and integration-tested.");

    internal static SqlCapability SetBranchMatrixCapability(
        SqlAgentToolType provider) =>
        new(
            "select.cte_set_branch",
            "query",
            Status(provider),
            SupportsTarget(provider)
                ? "Set-operation branches with a statement-root CTE are fully compiled as target fragments and wrapped behind a CTE-free derived SELECT before UNION/INTERSECT/EXCEPT lowering. The provider compiler applies this to ordinary, scalar/EXISTS, and DML nested SELECT compilation while preserving branch scope, tail clauses, and ordered bindings."
                : provider == SqlAgentToolType.Oracle
                    ? "Oracle rejects the nested parenthesized WITH form required by the current set-branch wrapper, so set-branch-local CTEs fail closed."
                    : provider == SqlAgentToolType.MsSqlServer
                        ? "SQL Server has no declared portable nested-WITH branch wrapper contract in the Core target profile, so set-branch-local CTEs fail closed in query and DML contexts."
                        : "Firebird nested CTE placement is kept fail-closed for set branches until a target-profile contract is modeled and integration-tested.");

    internal static SqlCapability ScalarRootMatrixCapability(
        SqlAgentToolType provider) =>
        new(
            "select.cte_scalar_root",
            "query",
            Status(provider),
            SupportsTarget(provider)
                ? "Scalar and EXISTS subqueries may own a statement-root WITH clause. Core renders those expressions through a complete provider compiler invocation, preserving the root CTE, correlated outer references, and ordered bindings. Root CTE set queries with outer ORDER BY/LIMIT/OFFSET are also lowered directly when set-result ordering references only a combined output name or output ordinal, avoiding the generated _set derived wrapper so correlated outer references stay in scope. Richer set-result ORDER BY expressions remain tracked by select.cte_scope."
                : provider == SqlAgentToolType.Oracle
                    ? "Oracle rejects WITH inside the parenthesized scalar/EXISTS subquery form, so scalar-root CTEs fail closed."
                    : provider == SqlAgentToolType.MsSqlServer
                        ? "SQL Server does not permit a nested WITH clause in the Core general-subquery profile, so scalar/EXISTS root CTEs fail closed."
                        : "Firebird scalar/EXISTS root CTE placement remains fail-closed until a target-profile contract is modeled and integration-tested.");

    internal static SqlCapability DefinitionLocalMatrixCapability(
        SqlAgentToolType provider) =>
        new(
            "select.cte_definition_local",
            "query",
            Status(provider),
            SupportsTarget(provider)
                ? "A CTE body may declare its own local WITH scope. Core recursively validates and renders each nested scope directly from the canonical AST without hoisting local definitions. Same-name shadowing, positional binding order, and local set-operation bodies with outer ORDER BY/LIMIT/OFFSET are preserved; CTE definitions have no parent correlation scope in the Core binder."
                : provider == SqlAgentToolType.Oracle
                    ? "Oracle does not support nesting a WITH clause inside another WITH query block in the Core target profile, so CTE-definition-local WITH fails closed."
                    : provider == SqlAgentToolType.MsSqlServer
                        ? "SQL Server has no declared portable nested-WITH-inside-a-CTE-definition contract in the Core target profile, so this shape fails closed."
                        : "Firebird CTE-definition-local WITH remains fail-closed until a target-profile contract is modeled and integration-tested.");

    internal static SqlCapability DmlNestedMatrixCapability(
        SqlAgentToolType provider) =>
        new(
            "dml.nested_cte_scope",
            "dml",
            Status(provider),
            SupportsTarget(provider)
                ? "DML nested SELECTs preserve CTE scope in four modeled forms: scalar/EXISTS root CTEs use a complete provider compile; scalar/EXISTS root CTE set queries with outer tails use scope-preserving direct lowering when ORDER BY references a combined output name or output ordinal; CTE-definition-local WITH bodies, including local set tails, are recursively compiled and reattached as raw CTE components; derived-table and set-branch CTE fragments use the Core query-graph adapter. Ordered bindings and correlated outer references remain structural; richer scalar/EXISTS set-result ORDER BY expressions remain fail-closed under select.cte_scope."
                : provider == SqlAgentToolType.Oracle
                    ? "Oracle nested parenthesized or nested-definition WITH forms fail closed in DML because the target grammar rejects them; statement-root INSERT ... SELECT CTEs remain supported through the dedicated placement path."
                    : "Nested WITH fragments in DML fail closed because this provider has no declared portable general-subquery or nested-CTE-definition contract; statement-root INSERT ... SELECT CTEs remain supported through the dedicated placement path.");

    private static SqlCapabilityStatus Status(SqlAgentToolType provider) =>
        SupportsTarget(provider)
            ? SqlCapabilityStatus.Translated
            : SqlCapabilityStatus.Rejected;
}
