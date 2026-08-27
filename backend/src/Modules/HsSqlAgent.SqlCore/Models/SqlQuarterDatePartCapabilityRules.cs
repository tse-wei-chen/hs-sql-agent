namespace HsSqlAgent.SqlCore.Models;

public static class SqlQuarterDatePartCapabilityRules
{
    public static bool SupportsTarget(SqlAgentToolType provider) =>
        provider == SqlAgentToolType.Postgres;

    public static string? TargetValidationError(SqlAgentToolType provider) =>
        SupportsTarget(provider)
            ? null
            : "SQL capability 'temporal.date_part.quarter' is not supported by provider " +
              provider +
              " for this Core plan.";

    public static SqlCapability MatrixCapability(SqlAgentToolType provider) =>
        new(
            "temporal.date_part.quarter",
            "temporal",
            SupportsTarget(provider)
                ? SqlCapabilityStatus.Supported
                : SqlCapabilityStatus.Rejected,
            SupportsTarget(provider)
                ? "PostgreSQL EXTRACT(QUARTER FROM value) is represented by the canonical date-part family and rendered natively."
                : "QUARTER date-part lowering remains fail-closed for this target until an explicit provider semantic contract is declared.");
}
