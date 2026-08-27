namespace HsSqlAgent.SqlCore.Models;

/// <summary>
/// Single target-provider contract for standalone TIME values. Oracle has no standalone TIME type;
/// all other declared providers keep the existing native/bound TIME lowering behavior.
/// </summary>
internal static class SqlStandaloneTimeCapabilityRules
{
    internal static bool SupportsTarget(SqlAgentToolType provider) =>
        provider != SqlAgentToolType.Oracle;

    internal static string? TargetValidationError(
        SqlAgentToolType provider) =>
        SupportsTarget(provider)
            ? null
            : "Oracle has no standalone TIME data type. " +
              "SQL capability 'literal.time' is not supported by provider Oracle for this Core plan.";

    internal static SqlCapability MatrixCapability(
        SqlAgentToolType provider) =>
        new(
            "temporal.standalone_time",
            "temporal",
            SupportsTarget(provider)
                ? SqlCapabilityStatus.Translated
                : SqlCapabilityStatus.Rejected,
            SupportsTarget(provider)
                ? "TIME values are bound using the provider's native temporal parameter type."
                : "Oracle has no standalone TIME type; standalone TIME values are rejected.");
}
