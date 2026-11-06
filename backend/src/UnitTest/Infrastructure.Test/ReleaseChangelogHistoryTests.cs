using Xunit;

namespace Infrastructure.Test;

public sealed class ReleaseChangelogHistoryTests
{
    [Fact]
    public void PublishedHistory_RemainsVisibleAlongsideTheNextUnreleasedVersion()
    {
        var changelog = File.ReadAllText(Path.Combine(RepositoryRoot(), "CHANGELOG.md"));
        Assert.StartsWith("# Changelog\n\n## [3.1.0] - Unreleased\n", changelog);
        Assert.Contains("## [3.0.0] - 2026-10-25", changelog);
        Assert.Contains("## [2.0.6] - Unreleased (proposed)", changelog);
        Assert.Contains("## [2.0.4] - 2026-09-07", changelog);
        Assert.Contains("## [1.0.0-alpha] - 2026-04-05", changelog);
    }

    [Fact]
    public void PostV300DevelopOnlyCapabilities_AreAttributedToV310NotThePublishedV300()
    {
        var changelog = File.ReadAllText(Path.Combine(RepositoryRoot(), "CHANGELOG.md"));
        var v31 = Section(changelog, "## [3.1.0]", "## [3.0.0]");
        var v30 = Section(changelog, "## [3.0.0]", "## [2.0.6]");

        Assert.Contains("### G10/Q02", v31);
        Assert.Contains("### G9 / M09", v31);
        Assert.Contains("### SQL Core G1/G2", v31);
        Assert.Contains("### G4: SQLite native DDL and live transactional Host adapter", v31);
        Assert.Contains("### Breaking changes and required migration", v30);
        Assert.Contains("### PostgreSQL old-worker cutover verification", v30);
        Assert.DoesNotContain("(develop only)", v30, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("This is a **major-version release candidate**", v30);
    }

    private static string Section(string content, string start, string end)
    {
        var begin = content.IndexOf(start, StringComparison.Ordinal);
        Assert.True(begin >= 0, $"Missing version marker {start}");
        var stop = content.IndexOf(end, begin + start.Length, StringComparison.Ordinal);
        Assert.True(stop > begin, $"Missing following version marker {end}");
        return content[begin..stop];
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CHANGELOG.md")))
            directory = directory.Parent;
        return Assert.IsType<DirectoryInfo>(directory).FullName;
    }
}
