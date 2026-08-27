namespace HsSqlAgent.SqlCore.Models;

public enum SqlCapabilityStatus
{
    Supported,
    Translated,
    Rejected
}

public sealed record SqlCapability(
    string Id,
    string Category,
    SqlCapabilityStatus Status,
    string Detail);

public sealed record ProviderSqlCapabilities(
    string MatrixVersion,
    SqlAgentToolType Provider,
    IReadOnlyList<SqlCapability> Capabilities);

public static class SqlCapabilityMatrix
{
    public const string Version = "2026-08-27.51";

    public static ProviderSqlCapabilities ForProvider(
        SqlAgentToolType provider,
        SqlProviderCapabilityProfile? targetProfile = null)
    {
        if (targetProfile is not null && targetProfile.Provider != provider)
        {
            throw new ArgumentException(
                $"Target capability profile declares provider {targetProfile.Provider}, but matrix provider is {provider}.",
                nameof(targetProfile));
        }

        var capabilities = new List<SqlCapability>
        {
            new("provider.target_profile", "provider", SqlCapabilityStatus.Supported,
                "Core accepts optional target runtime metadata including server version, compatibility level, session modes, and session settings. Undeclared target-profile-dependent capabilities remain fail-closed; SQL Server REGEXP_LIKE is enabled only by a declared target profile at compatibility level 170+, SQL Server canonical string concatenation requires runtime proof (ServerVersion 17.x with compatibility level 170+ emits native ||; ServerVersion 14.x+ or an explicit CONCAT_NULL_YIELDS_NULL=ON contract uses +), SQLite RIGHT/FULL OUTER JOIN requires ServerVersion 3.39+, SQLite deterministic ON CONFLICT UPSERT requires ServerVersion 3.24+, SQLite DML RETURNING requires ServerVersion 3.35+, portable multi-row Firebird DSQL RETURNING requires ServerVersion 5.0+, and conditional MySQL assured ON DUPLICATE KEY UPDATE lowering requires ServerVersion 8.0.19+ so Core can use proposed-row aliases instead of deprecated VALUES(column).") ,
            new("provider.source_profile", "provider", SqlCapabilityStatus.Supported,
                "Raw SQL compilation accepts a separate optional source runtime profile for session-dependent and version-dependent source semantics. The source profile provider must match the parsed source dialect and never authorizes target capabilities. MySQL source || is resolved as concatenation only when PIPES_AS_CONCAT or ANSI is explicitly declared; MySQL double-quoted identifiers are accepted only when ANSI_QUOTES or ANSI is explicitly declared. MySQL backslash-containing single-quoted strings and quoted identifiers use ordinary-character semantics only when NO_BACKSLASH_ESCAPES is explicitly declared; ANSI does not imply NO_BACKSLASH_ESCAPES. Under NO_BACKSLASH_ESCAPES, raw MySQL LIKE is accepted only when the source declares an explicit single-character ESCAPE clause; omitting that contract remains fail-closed rather than guessing pattern escape semantics. Raw SQL Server || source spelling remains fail-closed, including SQL Server 2025, until the Core source parser has an explicit T-SQL 17.x precedence/grammar contract. Raw SQLite RIGHT/FULL OUTER JOIN requires source ServerVersion 3.39+, raw SQLite ON CONFLICT UPSERT requires source ServerVersion 3.24+, raw SQLite RETURNING requires source ServerVersion 3.35+, and portable multi-row Firebird DSQL RETURNING requires source ServerVersion 5.0+. Absent or unrelated modes and versions remain fail-closed rather than guessing runtime semantics."),
            new("provider.unique_key_metadata", "provider", SqlCapabilityStatus.Supported,
                "Provider metadata readers inventory PRIMARY and UNIQUE conflict sources across PostgreSQL, MySQL, SQLite, SQL Server, Oracle, and Firebird. Simple enforced full-column keys are distinguishable from partial, expression/computed, prefix, disabled/invalid, or otherwise richer key shapes, and richer enforced keys remain visible instead of being filtered out. This metadata is an assurance prerequisite only; it does not by itself authorize a SQL lowering."),
            new("select.basic", "query", SqlCapabilityStatus.Translated,
                "SELECT/WHERE/GROUP BY/HAVING/ORDER BY and the structured JOIN grammar are represented by Core; provider-specific JOIN-family boundaries are declared separately."),
            SqlJoinCapabilityRules.RightJoinMatrixCapability(provider, targetProfile),
            SqlJoinCapabilityRules.FullJoinMatrixCapability(provider, targetProfile),
            new("select.row_limit", "query", SqlCapabilityStatus.Translated,
                "Structured Core row-count limits are translated to provider-native target syntax. Raw LIMIT spelling is accepted only for PostgreSQL, MySQL, and SQLite source dialects. PostgreSQL LIMIT ALL is canonicalized to no row-count limit, including LIMIT ALL OFFSET n where only the offset remains; MySQL and SQLite reject LIMIT ALL. MySQL and SQLite additionally accept native LIMIT offset,row_count and canonicalize the first integer to OFFSET and the second to LIMIT; PostgreSQL comma-form LIMIT is rejected. Raw bare OFFSET remains valid PostgreSQL syntax; MySQL and SQLite accept OFFSET only after LIMIT, and comma-form LIMIT cannot be combined with a separate OFFSET clause. PostgreSQL, Oracle, and Firebird raw source may use the modeled SQL-standard integer OFFSET ... ROW(S) and FETCH FIRST/NEXT ... ROW(S) ONLY forms, including FETCH without OFFSET; PostgreSQL may omit ROW/ROWS after OFFSET and may omit the FETCH count, which canonicalizes to one row. Explicit LIMIT and FETCH clauses remain mutually exclusive at the raw source boundary, including LIMIT ALL, matching PostgreSQL's alternative-syntax grammar. SQL Server raw OFFSET/FETCH requires statement-level ORDER BY, FETCH requires a preceding OFFSET, and TOP cannot share the same query scope. FETCH PERCENT, WITH TIES, and non-integer row-count expressions remain fail-closed because those semantics are not represented by the canonical Limit/Offset model."),
            new("select.singleton", "query", SqlCapabilityStatus.Translated,
                "SELECT expressions without a FROM source preserve singleton-row semantics; Oracle lowers through DUAL and Firebird through RDB$DATABASE. Free column references and wildcard projection fail closed instead of resolving against a provider dummy table, while COUNT(*) and correlated outer references remain valid."),
            new("select.cte_set", "query", SqlCapabilityStatus.Translated,
                "Statement-root CTEs and UNION/INTERSECT/EXCEPT are represented structurally. Root CTE set queries that need an outer ORDER BY/LIMIT/OFFSET wrapper keep the WITH definitions at statement scope while the native renderer wraps only the CTE-free set body and tail; this also covers execution-policy limits."),
            SqlNestedCteCapabilityRules.DerivedMatrixCapability(provider),
            SqlNestedCteCapabilityRules.SetBranchMatrixCapability(provider),
            SqlNestedCteCapabilityRules.ScalarRootMatrixCapability(provider),
            SqlNestedCteCapabilityRules.DefinitionLocalMatrixCapability(provider),
            new("select.cte_scope", "query", SqlCapabilityStatus.Rejected,
                "For PostgreSQL, MySQL, and SQLite scalar/EXISTS root CTE set queries, Core preserves correlated outer scope for outer ORDER BY/LIMIT/OFFSET when ORDER BY references only combined output names or output ordinals. Richer set-result ORDER BY expressions remain fail-closed because removing the generated _set wrapper is not yet proven scope- and ordering-equivalent for those expressions. Provider-specific nested-WITH support is declared separately by select.cte_derived, select.cte_set_branch, select.cte_scalar_root, and select.cte_definition_local."),
            new("expression.arithmetic", "expression", SqlCapabilityStatus.Translated,
                "+, -, *, and / are preserved by the AST/compiler."),
            SqlFirebirdDecimalCapabilityRules.MatrixCapability(provider, targetProfile),
            SqlModuloCapabilityRules.MatrixCapability(provider),
            SqlConcatCapabilityRules.MatrixCapability(provider, targetProfile),
            new("expression.like_escape", "expression", SqlCapabilityStatus.Translated,
                "Explicit single-character literal LIKE ESCAPE is represented structurally and emitted for all target providers while the pattern remains parameterized. Dynamic, empty, multi-character, and control-character escape specifications fail-closed. MySQL NO_BACKSLASH_ESCAPES source requires the explicit escape contract for raw LIKE; target rendering does not rely on provider-default escape semantics."),
            SqlScalarBooleanCapabilityRules.ProjectionMatrixCapability(provider),
            new("expression.boolean_literal_source", "expression", SqlCapabilityStatus.Translated,
                "Structured Core boolean values remain canonical. Raw SQL Server source rejects bare TRUE/FALSE before AST canonicalization because T-SQL bit constants use 0/1 and Core does not reinterpret those bare tokens as identifiers; quoted identifiers and numeric bit predicates remain available."),
            new("expression.cast", "expression", SqlCapabilityStatus.Translated,
                "Standard CAST input is normalized through a source-aware Core type model before provider-specific CAST spelling is emitted. Raw PostgreSQL :: cast spelling is accepted only when the declared source dialect is PostgreSQL; non-PostgreSQL raw sources fail before AST canonicalization. Unknown cross-dialect vendor types fail closed."),
            SqlIntervalLiteralCapabilityRules.MatrixCapability(provider),
            SqlAggregateFilterCapabilityRules.MatrixCapability(provider, targetProfile),
            new("aggregate.string", "aggregate", SqlCapabilityStatus.Translated,
                "Portable string aggregation canonicalizes STRING_AGG/GROUP_CONCAT/LISTAGG/LIST to one value expression plus a literal separator and lowers to provider-native syntax. Source defaults are normalized semantically: Oracle one-argument LISTAGG becomes an empty separator (its omitted delimiter is NULL/no separator), while one-argument GROUP_CONCAT and Firebird LIST use comma. STRING_AGG requires an explicit separator. MySQL raw GROUP_CONCAT SEPARATOR 'literal' is parsed as source syntax metadata and normalized into the canonical separator argument; comma-separated GROUP_CONCAT arguments remain multiple value expressions and are never reinterpreted as a separator. MySQL targets use native GROUP_CONCAT(... SEPARATOR ...)."),
            SqlAggregateLocalOrderingCapabilityRules.MatrixCapability(provider, targetProfile),
            new("aggregate.string.dynamic_separator", "aggregate", SqlCapabilityStatus.Rejected,
                "Dynamic or per-row string-aggregate separators are rejected at the Core capability boundary; the portable aggregate currently requires a literal separator so provider delimiter evaluation rules cannot drift during lowering."),
            new("temporal.typed_literals", "temporal", SqlCapabilityStatus.Translated,
                "Structured Core DATE, TIME, and TIMESTAMP values are represented as typed temporal values and bound as provider parameters. Raw typed-literal spelling is source-profiled before AST canonicalization: PostgreSQL accepts the modeled basic and WITH/WITHOUT TIME ZONE forms; MySQL accepts the basic forms but not WITH/WITHOUT TIME ZONE qualifiers; SQLite rejects ANSI typed-literal spelling; Oracle accepts DATE and TIMESTAMP basic spelling but not standalone TIME or the Core TIMESTAMP WITH/WITHOUT TIME ZONE spelling; Firebird accepts basic DATE/TIME/TIMESTAMP spelling, with zone information carried inside the literal value rather than a WITH/WITHOUT TIME ZONE type qualifier; SQL Server rejects ANSI typed-literal spelling and uses string values with CAST/CONVERT instead."),
            SqlStandaloneTimeCapabilityRules.MatrixCapability(provider),
            SqlOffsetTimestampCapabilityRules.MatrixCapability(provider, targetProfile),
            SqlCurrentTemporalCapabilityRules.MatrixCapability(provider),
            SqlQuarterDatePartCapabilityRules.MatrixCapability(provider),
            SqlDateMathCapabilityRules.MatrixCapability(provider),
            SqlTemporalFormatCapabilityRules.DateFormatMatrixCapability(provider),
            SqlTemporalFormatCapabilityRules.FormattedParseMatrixCapability(provider),
            SqlJsonCapabilityRules.ExtractMatrixCapability(provider),
            SqlJsonCapabilityRules.PathMatrixCapability(),
            SqlJsonCapabilityRules.SetMatrixCapability(provider),
            SqlRegexCapabilityRules.MatrixCapability(provider, targetProfile),
            SqlWindowCapabilityRules.BasicMatrixCapability(provider),
            SqlWindowCapabilityRules.FrameMatrixCapability(provider),
            new("ordering.ordinal", "ordering", SqlCapabilityStatus.Translated,
                "Statement-level ORDER BY output positions are represented as typed ordinals and emitted as ordinals rather than parameterized numeric literals."),
            SqlNullOrderingCapabilityRules.MatrixCapability(provider),
            new("parameter.unbound", "parameter", SqlCapabilityStatus.Rejected,
                "Unbound ?, :name, @name, $1, and {{name}} parameters are rejected; Custom Tool parameters are rendered first."),
            new("dml.basic", "dml", SqlCapabilityStatus.Translated,
                "INSERT VALUES, UPDATE, and DELETE use the structured DML path."),
            new("dml.update_expression", "dml", SqlCapabilityStatus.Translated,
                "UPDATE SET accepts structured scalar Core expressions including column arithmetic, scalar functions, CASE, CAST, and scalar subqueries. Aggregate/window placement and provider-specific expression capabilities are validated before lowering; runtime values remain parameters."),
            SqlScalarBooleanCapabilityRules.UpdateAssignmentMatrixCapability(provider),
            new("dml.insert_select", "dml", SqlCapabilityStatus.Translated,
                "INSERT ... SELECT is supported when the source projection width is statically known and matches the target column count. CTE-free sources render directly from the canonical AST; statement-root CTE sources use the native provider-aware CTE placement path."),
            new("dml.insert_select.cte_scope", "dml", SqlCapabilityStatus.Translated,
                "Statement-root CTEs in INSERT ... SELECT are lowered with provider-aware placement across all declared target providers while preserving parameter bindings, including root CTE set queries with outer ORDER BY/LIMIT/OFFSET. A root CTE whose body declares a local WITH follows select.cte_definition_local; nested derived/set-branch CTE support follows dml.nested_cte_scope."),
            SqlNestedCteCapabilityRules.DmlNestedMatrixCapability(provider),
            new("dml.advanced", "dml", SqlCapabilityStatus.Rejected,
                "Portable column-only DML RETURNING is tracked separately by dml.returning_output, and deterministic explicit-target INSERT conflict handling is tracked by dml.upsert_merge. Firebird metadata-assured UPDATE OR INSERT is also tracked by dml.upsert_merge; general MERGE, MySQL any-unique-key ON DUPLICATE KEY lowering without a sole-enforced-key equivalence proof, arbitrary conflict-update expressions, and INSERT ... SELECT upsert remain outside the portable DML contract."),
            SqlDmlReturningCapabilityRules.MatrixCapability(provider, targetProfile),
            SqlDmlUpsertCapabilityRules.MatrixCapability(provider, targetProfile)
        };

        return new ProviderSqlCapabilities(Version, provider, capabilities);
    }
}
