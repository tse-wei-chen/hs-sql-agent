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
    public const string Version = "2026-08-22.3";

    public static ProviderSqlCapabilities ForProvider(SqlAgentToolType provider)
    {
        var capabilities = new List<SqlCapability>
        {
            new("select.basic", "query", SqlCapabilityStatus.Translated,
                "SELECT/JOIN/WHERE/GROUP BY/HAVING/ORDER BY within the tested structured subset."),
            new("select.cte_set", "query", SqlCapabilityStatus.Translated,
                "CTE and UNION/INTERSECT/EXCEPT within the tested structured subset."),
            new("expression.arithmetic", "expression", SqlCapabilityStatus.Translated,
                "+, -, *, and / are preserved by the AST/compiler."),
            new("expression.modulo", "expression",
                provider is SqlAgentToolType.Oracle or SqlAgentToolType.Firebird
                    ? SqlCapabilityStatus.Translated
                    : SqlCapabilityStatus.Supported,
                provider is SqlAgentToolType.Oracle or SqlAgentToolType.Firebird
                    ? "The % operator is translated to MOD(left, right)."
                    : "The provider-native % operator is emitted."),
            new("expression.concat", "expression",
                provider is SqlAgentToolType.MySQL or SqlAgentToolType.MsSqlServer
                    ? SqlCapabilityStatus.Translated
                    : SqlCapabilityStatus.Supported,
                provider == SqlAgentToolType.MySQL
                    ? "The || operator is translated to CONCAT(left, right)."
                    : provider == SqlAgentToolType.MsSqlServer
                        ? "The || operator is translated to +."
                        : "The provider-native || operator is emitted."),
            new("expression.boolean_select", "expression",
                provider is SqlAgentToolType.Oracle or SqlAgentToolType.MsSqlServer
                    ? SqlCapabilityStatus.Rejected
                    : SqlCapabilityStatus.Supported,
                provider is SqlAgentToolType.Oracle or SqlAgentToolType.MsSqlServer
                    ? "Boolean/comparison expressions in the SELECT list are rejected; predicates remain supported."
                    : "Boolean/comparison expressions can be projected in the SELECT list."),
            new("expression.cast", "expression", SqlCapabilityStatus.Translated,
                "CAST and PostgreSQL :: input are represented as Cast AST and compiled as CAST(... AS type)."),
            new("expression.interval", "expression",
                provider == SqlAgentToolType.Postgres ? SqlCapabilityStatus.Supported : SqlCapabilityStatus.Rejected,
                provider == SqlAgentToolType.Postgres
                    ? "PostgreSQL INTERVAL 'literal' is preserved."
                    : "INTERVAL is rejected until an equivalent provider translation contract is implemented."),
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
                "DAY-based DATEADD and DATEDIFF are translated for all providers; unsupported units fail closed per provider."),
            new("temporal.date_format", "temporal",
                provider == SqlAgentToolType.Firebird ? SqlCapabilityStatus.Rejected : SqlCapabilityStatus.Translated,
                provider == SqlAgentToolType.Firebird
                    ? "Portable date formatting is rejected because no complete translation is declared."
                    : "Portable date-format tokens are translated to provider-native tokens."),
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
                        : "Simple JSON paths containing only root, property, and array-index segments are normalized and translated."),
            new("json.path.simple", "json", SqlCapabilityStatus.Translated,
                "Only constant paths composed of $, .property, and [array-index] segments are accepted; recursive descent, wildcards, filters, quoted names, and dynamic paths are rejected."),
            new("json.set", "json",
                provider is SqlAgentToolType.Oracle or SqlAgentToolType.Firebird
                    ? SqlCapabilityStatus.Rejected : SqlCapabilityStatus.Translated,
                provider is SqlAgentToolType.Oracle or SqlAgentToolType.Firebird
                    ? "Portable JSON mutation has no declared equivalent for this provider."
                    : "Portable JSON mutation is rendered with provider-native functions."),
            new("regex.match", "regex",
                provider is SqlAgentToolType.Postgres or SqlAgentToolType.MySQL or SqlAgentToolType.Oracle
                    ? SqlCapabilityStatus.Translated : SqlCapabilityStatus.Rejected,
                provider is SqlAgentToolType.Postgres or SqlAgentToolType.MySQL or SqlAgentToolType.Oracle
                    ? "REGEXP_LIKE is rendered using the provider's declared regex function."
                    : "Regex matching is rejected because no reliable native equivalent is declared."),
            new("window.basic", "window", SqlCapabilityStatus.Translated,
                "OVER with PARTITION BY and ORDER BY is represented structurally."),
            new("window.frame", "window", SqlCapabilityStatus.Translated,
                "ROWS/RANGE frames and both bounds are represented and compiled."),
            new("ordering.nulls", "ordering",
                provider is SqlAgentToolType.MySQL or SqlAgentToolType.MsSqlServer
                    ? SqlCapabilityStatus.Rejected
                    : SqlCapabilityStatus.Supported,
                provider is SqlAgentToolType.MySQL or SqlAgentToolType.MsSqlServer
                    ? "NULLS FIRST/LAST is rejected until an equivalent ordering rewrite is implemented."
                    : "NULLS FIRST/LAST is emitted natively."),
            new("parameter.unbound", "parameter", SqlCapabilityStatus.Rejected,
                "Unbound ?, :name, @name, $1, and {{name}} parameters are rejected; Custom Tool parameters are rendered first."),
            new("dml.basic", "dml", SqlCapabilityStatus.Translated,
                "Basic INSERT VALUES, UPDATE, and DELETE use the structured DML path."),
            new("dml.advanced", "dml", SqlCapabilityStatus.Rejected,
                "INSERT SELECT, RETURNING/OUTPUT, UPSERT, and MERGE are not yet in the portable DML grammar.")
        };

        return new ProviderSqlCapabilities(Version, provider, capabilities);
    }
}
