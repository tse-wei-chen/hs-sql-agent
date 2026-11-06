using HsSqlAgent.TestGovernance;
using Xunit;

namespace Infrastructure.Test;

public sealed class ReleaseVersionGateTests
{
    [Fact]
    public void MatchingStableReleaseTagAndCurrentChangelog_AreAccepted()
    {
        using var fixture = new ReleaseFixture();
        Assert.Equal("3.1.0", ReleaseVersionGate.Verify(fixture.Root, "v3.1.0"));
    }

    [Theory]
    [InlineData("v3.0.0")]
    [InlineData("3.1.0")]
    [InlineData("v3.1.0-rc.1")]
    [InlineData("v3.1.0;echo")]
    [InlineData("")]
    public void MismatchedOrMalformedReleaseTag_FailsClosed(string tag)
    {
        using var fixture = new ReleaseFixture();
        Assert.Throws<GovernanceException>(() => ReleaseVersionGate.Verify(fixture.Root, tag));
    }

    [Fact]
    public void ChangelogVersionMismatch_FailsClosed()
    {
        using var fixture = new ReleaseFixture();
        fixture.Write("CHANGELOG.md", "# Changelog\n\n## [3.0.0] - Unreleased\n");
        Assert.Throws<GovernanceException>(() => ReleaseVersionGate.Verify(fixture.Root, "v3.1.0"));
    }

    [Fact]
    public void MissingOrDuplicateVersionPrefix_FailsClosed()
    {
        using var fixture = new ReleaseFixture();
        fixture.Write("backend/Directory.Build.props", "<Project />");
        Assert.Throws<GovernanceException>(() => ReleaseVersionGate.Verify(fixture.Root, "v3.1.0"));
        fixture.Write("backend/Directory.Build.props",
            "<Project><PropertyGroup><VersionPrefix>3.1.0</VersionPrefix>" +
            "<VersionPrefix>3.1.0</VersionPrefix></PropertyGroup></Project>");
        Assert.Throws<GovernanceException>(() => ReleaseVersionGate.Verify(fixture.Root, "v3.1.0"));
    }

    [Fact]
    public void NonStablePackageVersion_FailsClosed()
    {
        using var fixture = new ReleaseFixture();
        fixture.Write("backend/Directory.Build.props",
            "<Project><PropertyGroup><VersionPrefix>3.1.0-rc.1</VersionPrefix></PropertyGroup></Project>");
        Assert.Throws<GovernanceException>(() => ReleaseVersionGate.Verify(fixture.Root, "v3.1.0-rc.1"));
    }

    [Fact]
    public void DevelopReleaseCandidate_HasMatching31Metadata()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CHANGELOG.md")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        Assert.Equal("3.1.0", ReleaseVersionGate.Verify(directory.FullName, "v3.1.0"));
    }

    private sealed class ReleaseFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "hssqlagent-release-version-" + Guid.NewGuid().ToString("N"));

        public ReleaseFixture()
        {
            Write("backend/Directory.Build.props",
                "<Project><PropertyGroup><VersionPrefix Condition=\"'$(VersionPrefix)' == ''\">3.1.0</VersionPrefix></PropertyGroup></Project>");
            Write("CHANGELOG.md", "# Changelog\n\n## [3.1.0] - Unreleased\n");
        }

        public void Write(string path, string content)
        {
            var file = Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, content);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
