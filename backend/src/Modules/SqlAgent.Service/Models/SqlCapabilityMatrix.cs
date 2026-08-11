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
    public const string Version = "2026-08-10.1";

    public static ProviderSqlCapabilities ForProvider(SqlAgentToolType provider)
    {
        if (provider == SqlAgentToolType.Global)
            throw new ArgumentOutOfRangeException(nameof(provider), provider, "Global is not a SQL execution provider.");

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
