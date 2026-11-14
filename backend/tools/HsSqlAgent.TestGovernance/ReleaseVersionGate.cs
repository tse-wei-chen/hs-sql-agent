using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace HsSqlAgent.TestGovernance;

/// <summary>
/// Fail closed before release publication: the GitHub tag must identify the same
/// stable version as the single first-party NuGet VersionPrefix and changelog.
/// This does not create a tag, publish artifacts or authorize a release.
/// </summary>
public static class ReleaseVersionGate
{
    private static readonly Regex StableVersion = new(
        @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ChangelogHeading = new(
        @"^## \[(?<version>[^\]]+)\] - ",
        RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string Verify(string repositoryRoot, string tag)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot))
            throw new GovernanceException("Release gate requires an explicit repository root");

        var props = XDocument.Load(Path.Combine(repositoryRoot, "backend", "Directory.Build.props"));
        var candidates = props.Descendants()
            .Where(element => element.Name.LocalName == "VersionPrefix")
            .ToArray();
        if (candidates.Length != 1)
            throw new GovernanceException($"Expected exactly one shared NuGet VersionPrefix, found {candidates.Length}");

        var version = candidates[0].Value.Trim();
        if (!StableVersion.IsMatch(version))
            throw new GovernanceException($"Stable release requires a numeric X.Y.Z VersionPrefix, got '{version}'");

        var expectedTag = "v" + version;
        if (!string.Equals(tag, expectedTag, StringComparison.Ordinal))
            throw new GovernanceException($"Release tag '{tag}' does not match shared NuGet version '{expectedTag}'");

        var changelog = File.ReadAllText(Path.Combine(repositoryRoot, "CHANGELOG.md"));
        var match = ChangelogHeading.Match(changelog);
        if (!match.Success || !string.Equals(match.Groups["version"].Value, version, StringComparison.Ordinal))
            throw new GovernanceException($"First CHANGELOG version must match shared NuGet version '{version}'");

        return version;
    }
}
