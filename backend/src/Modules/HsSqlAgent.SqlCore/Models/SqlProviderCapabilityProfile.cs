using SqlAgent.Service.Enums;

namespace SqlAgent.Service.Models;

/// <summary>
/// Declared runtime capability profile for one SQL provider. The same immutable profile shape can
/// describe either the parsed source session or the compilation target, depending on the compiler
/// argument that receives it. Provider/version/session-dependent capabilities remain fail-closed
/// unless the required profile value is explicitly present.
/// </summary>
public sealed record SqlProviderCapabilityProfile(
    SqlAgentToolType Provider,
    Version? ServerVersion = null,
    int? CompatibilityLevel = null,
    IReadOnlySet<string>? SessionModes = null,
    IReadOnlyDictionary<string, string>? SessionSettings = null)
{
    public bool HasSessionMode(string mode) =>
        !string.IsNullOrWhiteSpace(mode)
        && SessionModes?.Any(candidate => string.Equals(
            candidate,
            mode,
            StringComparison.OrdinalIgnoreCase)) == true;

    public string? GetSessionSetting(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || SessionSettings is null)
            return null;

        foreach (var pair in SessionSettings)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        }
        return null;
    }
}
