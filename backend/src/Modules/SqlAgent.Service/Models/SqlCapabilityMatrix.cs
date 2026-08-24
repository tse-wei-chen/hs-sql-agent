using SqlAgent.Service.Enums;

namespace SqlAgent.Service.Models;

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
    public const string Version = "2026-08-24.7";

    public static ProviderSqlCapabilities ForProvider(SqlAgentToolType provider)
    {
        var capabilities = new List<SqlCapability>
        {
            new("select.basic", "query", SqlCapabilityStatus.Translated,
                "SELECT/JOIN/WHERE/GROUP BY/HAVING/ORDER BY within the structured Core grammar."),
            new("select.singleton", "query", SqlCapabilityStatus.Translated,
                "SELECT expressions without a FROM source preserve singleton-row semantics; Oracle lowers through DUAL and Firebird through RDB$DATABASE. Free column references and wildcard projection fail closed instead of resolving against a provider dummy table, while COUNT(*) and correlated outer references remain valid."),
            new("select.cte_set", "query", SqlCapabilityStatus.Translated,
                "Statement-root CTEs and UNION/INTERSECT/EXCEPT are represented structurally; CTE output aliases and set-result ordering are validated before lowering."),
            new("select.cte_scope", "query", SqlCapabilityStatus.Rejected,
                "CTEs that would be compiled through SqlKata nested Select compilation and lose their definition fail closed: derived-table-local CTEs, set-operation-branch-local CTEs, and root CTE set queries that require an outer ORDER BY/LIMIT/OFFSET wrapper are rejected until explicit CTE hoisting is modeled."),
            new("expression.arithmetic", "expression", SqlCapabilityStatus.Translated,
                "+, -, *, and / are preserved by the AST/compiler."),
            new("expression.modulo", "expression",
                provider is SqlAgentToolType.Oracle or SqlAgentToolType.Firebird
                    ? SqlCapabilityStatus.Translated
                    : SqlCapabilityStatus.Supported,
                provider is SqlAgentToolType.Oracle or SqlAgentToolType.Firebird
                    ? "Canonical modulo is rendered as MOD(left, right); source-dialect validation rejects a native % spelling where that spelling is invalid."
                    : "The provider-native % operator is emitted."),
            new("expression.concat", "expression",
                provider is SqlAgentToolType.MySQL or SqlAgentToolType.MsSqlServer
                    ? SqlCapabilityStatus.Translated
                    : SqlCapabilityStatus.Supported,
                provider == SqlAgentToolType.MySQL
                    ? "Canonical string concatenation is translated to CONCAT(left, right); MySQL source '||' is rejected because its meaning depends on PIPES_AS_CONCAT sql_mode."
                    : provider == SqlAgentToolType.MsSqlServer
                        ? "Canonical string concatenation is translated to +."
                        : "The provider-native || operator is emitted."),
            new("expression.boolean_select", "expression",
                provider is SqlAgentToolType.Oracle or SqlAgentToolType.MsSqlServer
                    ? SqlCapabilityStatus.Rejected
                    : SqlCapabilityStatus.Supported,
                provider is SqlAgentToolType.Oracle or SqlAgentToolType.MsSqlServer
                    ? "Boolean/comparison expressions in the SELECT list are rejected; predicates remain supported."
                    : "Boolean/comparison expressions can be projected in the SELECT list."),
            new("expression.cast", "expression", SqlCapabilityStatus.Translated,
                "CAST and PostgreSQL :: input are normalized through a source-aware Core type model before provider-specific CAST spelling is emitted; unknown cross-dialect vendor types fail closed."),
            new("expression.interval", "expression",
                provider == SqlAgentToolType.Postgres ? SqlCapabilityStatus.Supported : SqlCapabilityStatus.Rejected,
                provider == SqlAgentToolType.Postgres
                    ? "PostgreSQL INTERVAL 'literal' is preserved."
                    : "INTERVAL is rejected until an equivalent provider translation contract is implemented."),
            new("aggregate.string", "aggregate", SqlCapabilityStatus.Translated,
                "Portable string aggregation canonicalizes STRING_AGG/GROUP_CONCAT/LISTAGG/LIST to one value expression plus a literal separator and lowers to provider-native syntax. MySQL targets use GROUP_CONCAT(value SEPARATOR separator); raw MySQL comma-separated GROUP_CONCAT arguments remain multiple value expressions and are never reinterpreted as a separator."),
            new("aggregate.string.dynamic_separator", "aggregate", SqlCapabilityStatus.Rejected,
                "Dynamic or per-row string-aggregate separators are rejected at the Core capability boundary; the portable aggregate currently requires a literal separator so provider delimiter evaluation rules cannot drift during lowering."),
            new("temporal.typed_literals", "temporal", SqlCapabilityStatus.Translated,
                "DATE, TIME, and TIMESTAMP literals are parsed into typed values and bound as provider parameters."),
            new("temporal.standalone_time", "temporal",
                provider == SqlAgentToolType.Oracle ? SqlCapabilityStatus.Rejected : SqlCapabilityStatus.Translated,
                provider == SqlAgentToolType.Oracle
                    ? "Oracle has no standalone TIME type; standalone TIME values are rejected."
                    : "TIME values are bound using the provider's native temporal parameter type."),
            new("temporal.offset_timestamp", "temporal",
                provider == SqlAgentToolType.MySQL ? SqlCapabilityStatus.Rejected : SqlCapabilityStatus.Translated,
                provider == SqlAgentToolType.MySQL
                    ? "MySQL has no native timestamp type that preserves an input UTC offset; offset values are rejected."
                    : "Offset timestamps are bound natively; PostgreSQL and Firebird normalize the represented instant to UTC."),
            new("temporal.current_keywords", "temporal",
                provider == SqlAgentToolType.Oracle ? SqlCapabilityStatus.Translated : SqlCapabilityStatus.Supported,
                provider == SqlAgentToolType.Oracle
                    ? "CURRENT_DATE and CURRENT_TIMESTAMP are supported; CURRENT_TIME is rejected because Oracle has no standalone TIME type."
                    : "CURRENT_DATE, CURRENT_TIME, and CURRENT_TIMESTAMP are emitted with provider-specific translation where needed."),
            new("temporal.date_arithmetic", "temporal", SqlCapabilityStatus.Translated,
                "DATEADD/DATEDIFF are canonicalized only from declared source-dialect forms; target-specific unit restrictions are validated before lowering."),
            new("temporal.date_format", "temporal",
                provider == SqlAgentToolType.Firebird ? SqlCapabilityStatus.Rejected : SqlCapabilityStatus.Translated,
                provider == SqlAgentToolType.Firebird
                    ? "Portable date formatting is rejected because no complete translation is declared."
                    : "Declared source date-format functions and tokens are normalized and translated to provider-native syntax."),
            new("temporal.formatted_parse", "temporal",
                provider is SqlAgentToolType.Postgres or SqlAgentToolType.MySQL or SqlAgentToolType.Oracle
                    ? SqlCapabilityStatus.Translated
                    : SqlCapabilityStatus.Rejected,
                provider is SqlAgentToolType.Postgres or SqlAgentToolType.MySQL or SqlAgentToolType.Oracle
                    ? "TO_DATE input and format tokens are translated to the provider-native function."
                    : "Formatted date parsing is rejected because no complete provider translation is declared."),
            new("json.extract", "json",
                provider is SqlAgentToolType.Firebird or SqlAgentToolType.MsSqlServer or SqlAgentToolType.Oracle
                    ? SqlCapabilityStatus.Rejected : SqlCapabilityStatus.Translated,
                provider is SqlAgentToolType.MsSqlServer or SqlAgentToolType.Oracle
                    ? "Ambiguous JSON_EXTRACT is rejected because the scalar/object result type is unknown; use an explicit JSON_VALUE or JSON_QUERY contract."
                    : provider == SqlAgentToolType.Firebird
                        ? "Portable JSON extraction has no declared Firebird equivalent."
                        : "Constant JSON property-chain paths such as $.user.name are normalized and translated; root-only, array-index, wildcard, filter, quoted-property, recursive-descent, and dynamic paths fail closed."),
            new("json.path.simple", "json", SqlCapabilityStatus.Translated,
                "Portable JSON paths are limited to constant property chains beginning at $, for example $.user.name; root-only, array-index, wildcard, filter, quoted property names, recursive descent, and dynamic paths are rejected before lowering."),
            new("json.set", "json",
                provider is SqlAgentToolType.Oracle or SqlAgentToolType.Firebird
                    ? SqlCapabilityStatus.Rejected : SqlCapabilityStatus.Translated,
                provider is SqlAgentToolType.Oracle or SqlAgentToolType.Firebird
                    ? "Portable JSON mutation has no declared equivalent for this provider."
                    : "Portable JSON mutation is rendered with provider-native functions after constant property-chain path validation."),
            new("regex.match", "regex",
                provider is SqlAgentToolType.Postgres or SqlAgentToolType.MySQL or SqlAgentToolType.Oracle
                    ? SqlCapabilityStatus.Translated : SqlCapabilityStatus.Rejected,
                provider is SqlAgentToolType.Postgres or SqlAgentToolType.MySQL or SqlAgentToolType.Oracle
                    ? "REGEXP_LIKE semantics are rendered using the provider's declared regex syntax."
                    : provider == SqlAgentToolType.MsSqlServer
                        ? "SQL Server 2025 REGEXP_LIKE requires compatibility level 170, but server-version/compatibility-level capability profiles are not part of the Core plan yet."
                        : "Regex matching is rejected because no reliable native equivalent is declared."),
            new("window.basic", "window", SqlCapabilityStatus.Translated,
                "OVER with PARTITION BY and ORDER BY is represented structurally; provider-specific function/order requirements are validated before lowering."),
            new("window.frame", "window", SqlCapabilityStatus.Translated,
                "ROWS/RANGE frames are represented structurally; provider/function combinations that do not accept a frame and SQL Server RANGE offsets fail closed before lowering."),
            new("ordering.ordinal", "ordering", SqlCapabilityStatus.Translated,
                "Statement-level ORDER BY output positions are represented as typed ordinals and emitted as ordinals rather than parameterized numeric literals."),
            new("ordering.nulls", "ordering",
                provider is SqlAgentToolType.MySQL or SqlAgentToolType.MsSqlServer
                    ? SqlCapabilityStatus.Translated
                    : SqlCapabilityStatus.Supported,
                provider is SqlAgentToolType.MySQL or SqlAgentToolType.MsSqlServer
                    ? "Structured ASC NULLS FIRST and DESC NULLS LAST are canonicalized to the provider's identical native default ordering and the unsupported modifier is omitted. ASC NULLS LAST and DESC NULLS FIRST remain fail-closed because no single-evaluation rewrite is modeled; raw MySQL/SQL Server source syntax with NULLS modifiers is rejected at the source-dialect boundary."
                    : "NULLS FIRST/LAST is emitted natively."),
            new("parameter.unbound", "parameter", SqlCapabilityStatus.Rejected,
                "Unbound ?, :name, @name, $1, and {{name}} parameters are rejected; Custom Tool parameters are rendered first."),
            new("dml.basic", "dml", SqlCapabilityStatus.Translated,
                "INSERT VALUES, UPDATE, and DELETE use the structured DML path."),
            new("dml.update_expression", "dml", SqlCapabilityStatus.Translated,
                "UPDATE SET accepts structured scalar Core expressions including column arithmetic, scalar functions, CASE, CAST, and scalar subqueries. Aggregate/window placement and provider-specific expression capabilities are validated before lowering; runtime values remain parameters."),
            new("dml.update.boolean_assignment", "dml",
                provider is SqlAgentToolType.Oracle or SqlAgentToolType.MsSqlServer
                    ? SqlCapabilityStatus.Rejected
                    : SqlCapabilityStatus.Translated,
                provider is SqlAgentToolType.Oracle or SqlAgentToolType.MsSqlServer
                    ? "Definitely boolean UPDATE assignment expressions are rejected because the current Core target profile does not model a portable scalar SQL boolean for this provider."
                    : "Definitely boolean UPDATE assignment expressions use the provider's scalar boolean/value semantics."),
            new("dml.insert_select", "dml", SqlCapabilityStatus.Translated,
                "INSERT ... SELECT is supported when the source projection width is statically known and matches the target column count; CTE-free source queries are lowered through SqlKata's structured insert-query path."),
            new("dml.insert_select.cte_scope", "dml", SqlCapabilityStatus.Rejected,
                "An INSERT ... SELECT source with a root CTE is rejected because SqlKata CompileInsertQueryClause delegates to nested CompileSelectQuery and would omit the CTE definition; explicit CTE hoisting is not modeled yet."),
            new("dml.advanced", "dml", SqlCapabilityStatus.Rejected,
                "RETURNING/OUTPUT, UPSERT/ON CONFLICT/ON DUPLICATE KEY, and MERGE are not yet in the portable DML grammar; INSERT ... SELECT is tracked separately and supported."),
            new("dml.returning_output", "dml", SqlCapabilityStatus.Rejected,
                "RETURNING and OUTPUT result clauses are not yet represented by the portable DML AST."),
            new("dml.upsert_merge", "dml", SqlCapabilityStatus.Rejected,
                "UPSERT dialect forms and MERGE are not yet represented by the portable DML AST.")
        };

        return new ProviderSqlCapabilities(Version, provider, capabilities);
    }
}