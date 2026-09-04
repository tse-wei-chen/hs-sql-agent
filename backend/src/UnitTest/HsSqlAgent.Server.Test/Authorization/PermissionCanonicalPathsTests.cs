using Auth.Service.Authorization;
using HsSqlAgent.Server.Authorization;
using Microsoft.AspNetCore.Mvc.Filters;
using Xunit;

namespace HsSqlAgent.Server.Test.Authorization;

public class PermissionCanonicalPathsTests
{
    [Fact]
    public void Catalog_IsUniqueAndCanonical()
    {
        Assert.Equal(PermissionCanonicalPaths.All.Count, PermissionCanonicalPaths.All.Distinct(StringComparer.Ordinal).Count());
        Assert.All(PermissionCanonicalPaths.All, path =>
        {
            Assert.StartsWith("/", path, StringComparison.Ordinal);
            Assert.Equal(path, PermissionCanonicalPaths.Normalize(path));
        });
    }

    [Theory]
    [InlineData("auth/role")]
    [InlineData("/Auth/Role")]
    [InlineData("/auth//role")]
    [InlineData("/auth/role/")]
    public void RequireCanonical_RejectsNonCanonicalPaths(string path)
    {
        Assert.Throws<ArgumentException>(() => PermissionCanonicalPaths.RequireCanonical(path));
    }

    [Fact]
    public void HasPermission_RejectsRouteLikeNonCanonicalPath()
    {
        Assert.Throws<ArgumentException>(() => new HasPermissionAttribute("api/auth/role", "view"));
    }

    [Fact]
    public void HasPermission_ComposesCanonicalPermissionKeyWithoutNamedPolicyMetadata()
    {
        var attribute = new HasPermissionAttribute(PermissionCanonicalPaths.Roles, "view");

        Assert.Equal("/auth/role.view", attribute.Permission);
        Assert.IsAssignableFrom<IFilterFactory>(attribute);
    }
}
