namespace HsSqlAgent.SqlCore.Models;

/// <summary>
/// Single source/target contract for modulo. Oracle and Firebird use MOD(left, right) for the
/// canonical operation and therefore reject raw '%' source spelling. Other modeled providers accept
/// and emit the native '%' operator.
/// </summary>
internal static class SqlModuloCapabilityRules
{
    internal static bool UsesFunctionSyntax(SqlAgentToolType provider) =>
        provider is SqlAgentToolType.Oracle or SqlAgentToolType.Firebird;

    internal static bool SupportsRawPercentSource(SqlAgentToolType sourceDialect) =>
        !UsesFunctionSyntax(sourceDialect);

    internal static string? SourceValidationError(SqlAgentToolType sourceDialect) =>
        SupportsRawPercentSource(sourceDialect)
            ? null
            : $"Operator '%' is not valid portable source syntax for {sourceDialect}; use the provider's MOD function instead.";

    internal static SqlCapability MatrixCapability(SqlAgentToolType provider) =>
        UsesFunctionSyntax(provider)
            ? new(
                "expression.modulo",
                "expression",
                SqlCapabilityStatus.Translated,
                "Canonical modulo is rendered as MOD(left, right); source-dialect validation rejects a native % spelling where that spelling is invalid.")
            : new(
                "expression.modulo",
                "expression",
                SqlCapabilityStatus.Supported,
                "The provider-native % operator is emitted.");
}
