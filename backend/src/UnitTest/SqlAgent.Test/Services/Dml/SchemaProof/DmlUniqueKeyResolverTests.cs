using Moq;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class DmlUniqueKeyResolverTests
{
    [Fact]
    public async Task ResolveAsync_MatchesSimpleCompositeKeyButPreservesOtherUniqueConflicts()
    {
        var metadata = new Mock<IProviderMetadataReader>();
        metadata
            .Setup(x => x.GetUniqueKeysAsync(
                "connection",
                "public",
                "users",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new DatabaseUniqueKeyMetadata("public", "users", "pk_users", true, ["id"]),
                new DatabaseUniqueKeyMetadata("public", "users", "uq_users_tenant_email", false, ["tenant_id", "email"]),
                new DatabaseUniqueKeyMetadata(
                    "public",
                    "users",
                    "uq_users_lower_name",
                    false,
                    [],
                    HasExpressions: true)
            ]);

        var resolution = await new DmlUniqueKeyResolver(metadata.Object).ResolveAsync(
            "connection",
            "public",
            "users",
            ["email", "tenant_id"],
            TestContext.Current.CancellationToken);

        Assert.Equal("uq_users_tenant_email", resolution.MatchedKey.Name);
        Assert.False(resolution.MatchedKey.IsPrimaryKey);
        Assert.Equal(3, resolution.EnforcedKeys.Count);
        Assert.False(resolution.IsSoleEnforcedUniqueKey);
        Assert.True(resolution.HasUnsupportedEnforcedUniqueKeys);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task ResolveAsync_RicherTargetKeyShape_RemainsFailClosed(
        bool partial,
        bool expressions,
        bool prefix)
    {
        var metadata = new Mock<IProviderMetadataReader>();
        metadata
            .Setup(x => x.GetUniqueKeysAsync(
                "connection",
                "public",
                "users",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new DatabaseUniqueKeyMetadata(
                    "public",
                    "users",
                    "uq_users_email",
                    false,
                    ["email"],
                    IsPartial: partial,
                    HasExpressions: expressions,
                    HasPrefixKeyParts: prefix)
            ]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DmlUniqueKeyResolver(metadata.Object).ResolveAsync(
                "connection",
                "public",
                "users",
                ["email"],
                TestContext.Current.CancellationToken));

        Assert.Contains("simple enforced unique key", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("richer key shape", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_UnenforcedUniqueKey_DoesNotCountAsConflictProof()
    {
        var metadata = new Mock<IProviderMetadataReader>();
        metadata
            .Setup(x => x.GetUniqueKeysAsync(
                "connection",
                "public",
                "users",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new DatabaseUniqueKeyMetadata("public", "users", "uq_disabled", false, ["email"], IsEnforced: false),
                new DatabaseUniqueKeyMetadata("public", "users", "pk_users", true, ["id"])
            ]);

        var resolution = await new DmlUniqueKeyResolver(metadata.Object).ResolveAsync(
            "connection",
            "public",
            "users",
            ["id"],
            TestContext.Current.CancellationToken);

        Assert.True(resolution.IsSoleEnforcedUniqueKey);
        Assert.Single(resolution.EnforcedKeys);
        Assert.Equal("pk_users", resolution.MatchedKey.Name);
    }

    [Fact]
    public async Task ResolveAsync_DuplicateTargetColumns_AreRejectedBeforeMetadataRead()
    {
        var metadata = new Mock<IProviderMetadataReader>();

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            new DmlUniqueKeyResolver(metadata.Object).ResolveAsync(
                "connection",
                "public",
                "users",
                ["id", "ID"],
                TestContext.Current.CancellationToken));

        Assert.Contains("duplicates", error.Message, StringComparison.OrdinalIgnoreCase);
        metadata.Verify(x => x.GetUniqueKeysAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
