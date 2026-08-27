namespace HsSqlAgent.SqlCore.Models;

/// <summary>
/// Single provider contract for explicit NULLS FIRST/LAST ordering. PostgreSQL, SQLite, Oracle,
/// and Firebird accept the modeled modifier natively. MySQL and SQL Server require Core's
/// semantics-preserving rewrite for the proven subset, reject raw modifier spelling at the source
/// boundary, and retain a post-rewrite validation backstop for shapes that cannot be transformed
/// without duplicating expression evaluation or changing ORDER BY scope.
/// </summary>
internal static class SqlNullOrderingCapabilityRules
{
    internal static bool RequiresTargetRewrite(SqlAgentToolType provider) =>
        provider is SqlAgentToolType.MySQL or SqlAgentToolType.MsSqlServer;

    internal static bool SupportsRawSourceModifier(SqlAgentToolType provider) =>
        !RequiresTargetRewrite(provider);

    internal static string? SourceValidationError(
        SqlAgentToolType sourceDialect,
        NullOrderingKind nullOrdering)
    {
        if (nullOrdering == NullOrderingKind.Default
            || SupportsRawSourceModifier(sourceDialect))
        {
            return null;
        }

        var modifier = nullOrdering == NullOrderingKind.First
            ? "NULLS FIRST"
            : "NULLS LAST";
        return $"ORDER BY modifier '{modifier}' is not valid for declared source dialect {sourceDialect} in the Core source capability profile.";
    }

    internal static SqlCapability MatrixCapability(SqlAgentToolType provider) =>
        RequiresTargetRewrite(provider)
            ? new(
                "ordering.nulls",
                "ordering",
                SqlCapabilityStatus.Translated,
                "Structured ASC NULLS FIRST and DESC NULLS LAST are canonicalized to the provider's identical native default ordering and the unsupported modifier is omitted. " +
                "ASC NULLS LAST and DESC NULLS FIRST are translated with a CASE null-rank only when ORDER BY is a direct row-source column, including window ordering and nested DML SELECTs. " +
                "DISTINCT statement tails, set-operation tails, projection alias references, and computed expressions remain fail-closed so Core does not duplicate arbitrary expression evaluation or violate provider ORDER BY select-list rules. " +
                "Raw MySQL/SQL Server source syntax with NULLS modifiers is rejected at the source-dialect boundary.")
            : new(
                "ordering.nulls",
                "ordering",
                SqlCapabilityStatus.Supported,
                "NULLS FIRST/LAST is emitted natively.");
}
