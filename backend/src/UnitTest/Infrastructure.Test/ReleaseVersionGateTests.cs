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

    [Fact]
    public void BothReleasePublishers_VerifyVersionBeforeAnyArtifactPublication()
    {
        var root = LocateRepository();
        foreach (var (workflow, publication) in new[]
                 {
                     (".github/workflows/nuget-publish.yml", "Pack all public packages"),
                     (".github/workflows/docker-publish.yml", "Build and push Docker image")
                 })
        {
            var content = File.ReadAllText(Path.Combine(root, workflow));
            var gate = content.IndexOf("release-version --root . --tag", StringComparison.Ordinal);
            var publish = content.IndexOf(publication, StringComparison.Ordinal);
            Assert.True(gate >= 0 && publish > gate, $"{workflow}: require release-version validation before publishing");
        }
    }

    private static string LocateRepository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CHANGELOG.md")))
            directory = directory.Parent;
        return Assert.IsType<DirectoryInfo>(directory).FullName;
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
