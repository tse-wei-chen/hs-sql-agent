namespace HsSqlAgent.SqlCore.Models;

/// <summary>
/// Single target-provider contract for scalar boolean values. PostgreSQL, MySQL, SQLite, and
/// Firebird expose a modeled scalar boolean/value representation; Oracle and SQL Server keep
/// definitely-boolean SELECT projections and DML scalar assignments fail-closed.
/// </summary>
internal static class SqlScalarBooleanCapabilityRules
{
    internal static bool SupportsScalarBooleanValue(SqlAgentToolType provider) =>
        provider is not (SqlAgentToolType.Oracle or SqlAgentToolType.MsSqlServer);

    internal static string? TargetValidationError(
        SqlAgentToolType provider,
        string capability) =>
        SupportsScalarBooleanValue(provider)
            ? null
            : $"SQL capability '{capability}' is not supported by provider {provider} for this Core plan.";

    internal static SqlCapability ProjectionMatrixCapability(
        SqlAgentToolType provider) =>
        new(
            "expression.boolean_select",
            "expression",
            SupportsScalarBooleanValue(provider)
                ? SqlCapabilityStatus.Supported
                : SqlCapabilityStatus.Rejected,
            SupportsScalarBooleanValue(provider)
                ? "Boolean/comparison expressions can be projected in the SELECT list."
                : "Boolean/comparison expressions in the SELECT list are rejected; predicates remain supported.");

    internal static SqlCapability UpdateAssignmentMatrixCapability(
        SqlAgentToolType provider) =>
        new(
            "dml.update.boolean_assignment",
            "dml",
            SupportsScalarBooleanValue(provider)
                ? SqlCapabilityStatus.Translated
                : SqlCapabilityStatus.Rejected,
            SupportsScalarBooleanValue(provider)
                ? "Definitely boolean UPDATE assignment expressions use the provider's scalar boolean/value semantics."
                : "Definitely boolean UPDATE assignment expressions are rejected because the current Core target profile does not model a portable scalar SQL boolean for this provider.");
}
