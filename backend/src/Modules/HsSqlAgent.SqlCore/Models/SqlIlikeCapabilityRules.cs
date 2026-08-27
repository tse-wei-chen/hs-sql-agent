namespace HsSqlAgent.SqlCore.Models;

/// <summary>
/// Single source/target provider contract for ILIKE. Core currently treats ILIKE as PostgreSQL-only:
/// raw source SQL from other dialects fails at normalization, and non-PostgreSQL targets fail before
/// lowering rather than approximating case-insensitive matching with collation-dependent rewrites.
/// </summary>
internal static class SqlIlikeCapabilityRules
{
    internal static bool SupportsSourceSyntax(SqlAgentToolType sourceDialect) =>
        sourceDialect == SqlAgentToolType.Postgres;

    internal static bool SupportsTarget(SqlAgentToolType provider) =>
        provider == SqlAgentToolType.Postgres;

    internal static string? SourceValidationError(SqlAgentToolType sourceDialect) =>
        SupportsSourceSyntax(sourceDialect)
            ? null
            : $"ILIKE is PostgreSQL-specific and is not valid for source dialect {sourceDialect}.";
}
