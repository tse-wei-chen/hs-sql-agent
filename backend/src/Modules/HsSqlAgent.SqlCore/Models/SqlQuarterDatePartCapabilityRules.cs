namespace HsSqlAgent.SqlCore.Models;

public static class SqlQuarterDatePartCapabilityRules
{
    public static bool SupportsTarget(SqlAgentToolType provider) =>
        SqlDatePartCapabilityRules.SupportsTarget("QUARTER", provider);

    public static string? TargetValidationError(SqlAgentToolType provider) =>
        SqlDatePartCapabilityRules.TargetValidationError("QUARTER", provider);

    public static SqlCapability MatrixCapability(SqlAgentToolType provider) =>
        SqlDatePartCapabilityRules.QuarterMatrixCapability(provider);
}
