namespace HsSqlAgent.SqlCore.Models;

/// <summary>
/// Firebird introduced TIME WITH TIME ZONE and TIMESTAMP WITH TIME ZONE in 4.0. Any target CAST
/// that relies on those native types must therefore carry explicit runtime proof instead of
/// assuming that a Firebird deployment supports them.
/// </summary>
internal static class SqlFirebirdTimeZoneTypeCapabilityRules
{
    internal static readonly Version MinimumVersion = new(4, 0);

    internal static bool RequiresTargetProfileValidation(
        SqlAgentToolType provider) =>
        provider == SqlAgentToolType.Firebird;

    internal static bool SupportsTargetProfile(
        SqlProviderCapabilityProfile? targetProfile) =>
        targetProfile is
        {
            Provider: SqlAgentToolType.Firebird,
            ServerVersion: { } version
        }
        && version.CompareTo(MinimumVersion) >= 0;

    internal static string? CastTargetValidationError(
        SqlAgentToolType provider,
        SqlProviderCapabilityProfile? targetProfile,
        string typeName)
    {
        if (provider != SqlAgentToolType.Firebird
            || !IsTimeZoneTargetType(typeName)
            || SupportsTargetProfile(targetProfile))
        {
            return null;
        }

        return
            "SQL capability 'temporal.firebird_time_zone_type' requires an explicit Firebird " +
            $"target capability profile with ServerVersion {MinimumVersion} or newer for CAST " +
            $"target type '{typeName}' because TIME WITH TIME ZONE and TIMESTAMP WITH TIME ZONE " +
            "were introduced in Firebird 4.0.";
    }

    private static bool IsTimeZoneTargetType(string typeName)
    {
        var normalized = string.Join(
            ' ',
            typeName.Trim()
                .ToUpperInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        if (!normalized.EndsWith(" WITH TIME ZONE", StringComparison.Ordinal))
            return false;

        var separator = normalized.IndexOf(' ');
        var head = separator < 0 ? normalized : normalized[..separator];
        return head == "TIME"
            || head.StartsWith("TIME(", StringComparison.Ordinal)
            || head == "TIMESTAMP"
            || head.StartsWith("TIMESTAMP(", StringComparison.Ordinal);
    }
}
