namespace HsSqlAgent.SqlCore.Models;

/// <summary>
/// Single source/target contract for the currently modeled interval literal shape. The AST node
/// represents PostgreSQL-style INTERVAL 'literal' text only; provider-native interval grammars with
/// units/qualifiers require their own structured contract rather than reinterpretation here.
/// </summary>
internal static class SqlIntervalLiteralCapabilityRules
{
    internal static bool IsTargetSupported(SqlAgentToolType provider) =>
        provider == SqlAgentToolType.Postgres;

    internal static string? SourceValidationError(SqlAgentToolType sourceDialect) =>
        sourceDialect == SqlAgentToolType.Postgres
            ? null
            : $"INTERVAL 'literal' is not valid for declared source dialect {sourceDialect} in the Core source capability profile. " +
              "Core models this interval-literal shape as PostgreSQL source syntax; other dialect interval forms require their own structured translation contract.";

    internal static SqlCapability MatrixCapability(SqlAgentToolType provider) =>
        new(
            "expression.interval",
            "expression",
            IsTargetSupported(provider)
                ? SqlCapabilityStatus.Supported
                : SqlCapabilityStatus.Rejected,
            IsTargetSupported(provider)
                ? "PostgreSQL interval semantics are supported natively. Core canonicalizes INTERVAL 'literal' and emits the decoded interval value as a bound parameter cast to interval, so runtime data is kept out of target SQL text. Raw Core SQL accepts this PostgreSQL-style source literal only when the declared source dialect is PostgreSQL; structured Core input is independent of the raw source-syntax gate."
                : "PostgreSQL-style INTERVAL 'literal' has no declared target equivalent for this provider. Raw SQL that parses into this Core interval-literal shape is also rejected when the declared source dialect is non-PostgreSQL; provider-native interval forms such as MySQL INTERVAL expr unit require a separate structured translation contract.");
}
