using Auth.Service.Data;
using Auth.Service.Data.Entites;
using Auth.Service.Models;
using Auth.Service.Services;
using Moq;
using Moq.EntityFrameworkCore;
using Xunit;

namespace Auth.Test.Services;

public class RoleServiceTests
{
    private readonly Mock<IAuthContext> _contextMock;
    private readonly RoleService _service;

    public RoleServiceTests()
    {
        _contextMock = new Mock<IAuthContext>();
        _contextMock.Setup(c => c.Members).ReturnsDbSet(new List<Member>());
        _service = new RoleService(_contextMock.Object);
    }

    [Fact]
    public async Task GetRolesAsync_ReturnsAllRolesOrderedByName()
    {
        var roles = new List<Role>
        {
            new() { Id = 2, Name = "Editor", PermissionActions = [] },
            new() { Id = 1, Name = "Admin", PermissionActions = [] }
        };
        _contextMock.Setup(c => c.Roles).ReturnsDbSet(roles);

        var result = (await _service.GetRolesAsync()).ToList();

        Assert.Equal(2, result.Count);
        Assert.Equal("Admin", result[0].Name);
        Assert.Equal("Editor", result[1].Name);
    }

    [Fact]
    public async Task GetRolesAsync_IncludesPermissionActions()
    {
        var pa = new PermissionAction { Id = 1, PermissionId = 10, ActionId = 20 };
        var roles = new List<Role>
        {
            new() { Id = 1, Name = "Admin", PermissionActions = [pa] }
        };
        _contextMock.Setup(c => c.Roles).ReturnsDbSet(roles);

        var result = (await _service.GetRolesAsync()).ToList();

        var permissionAction = Assert.Single(result[0].PermissionActions);
        Assert.Equal(10, permissionAction.PermissionId);
        Assert.Equal(20, permissionAction.ActionId);
    }

    [Fact]
    public async Task UpsertRoleAsync_CreatesNewRole()
    {
        var templates = new List<PermissionActionTemplate>
        {
            new() { Id = 1, PermissionId = 10, ActionId = 20 }
        };
        // ReturnsDbSet re-query will find this role (Id=0 matches new Role() from service)
        var roles = new List<Role>
        {
            new() { Id = 0, Name = "NewRole", Description = "A new role", PermissionActions = [] }
        };

        _contextMock.Setup(c => c.Roles).ReturnsDbSet(roles);
        _contextMock.Setup(c => c.PermissionActionTemplates).ReturnsDbSet(templates);
        _contextMock.Setup(c => c.PermissionActions).ReturnsDbSet(new List<PermissionAction>());
        _contextMock.SetupSequence(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1)
            .ReturnsAsync(1);

        var result = await _service.UpsertRoleAsync(null, new RolePayload
        {
            Name = "NewRole",
            Description = "A new role",
            PermissionActions = [new PermissionActionSelection { PermissionId = 10, ActionId = 20 }]
        });

        Assert.Equal("NewRole", result.Name);
        Assert.Equal("A new role", result.Description);
    }

    [Fact]
    public async Task UpsertRoleAsync_UpdatesExistingRole()
    {
        var existingRole = new Role { Id = 1, Name = "OldName", PermissionActions = [] };
        var roles = new List<Role> { existingRole };
        var templates = new List<PermissionActionTemplate>
        {
            new() { Id = 1, PermissionId = 10, ActionId = 20 }
        };

        _contextMock.Setup(c => c.Roles).ReturnsDbSet(roles);
        _contextMock.Setup(c => c.PermissionActionTemplates).ReturnsDbSet(templates);
        _contextMock.Setup(c => c.PermissionActions).ReturnsDbSet(new List<PermissionAction>());
        _contextMock.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var result = await _service.UpsertRoleAsync(1, new RolePayload
        {
            Name = "UpdatedName",
            Description = "Updated",
            PermissionActions = [new PermissionActionSelection { PermissionId = 10, ActionId = 20 }]
        });

        Assert.Equal("UpdatedName", result.Name);
    }

    [Fact]
    public async Task UpsertRoleAsync_InvalidatesSessionsForAssignedMembers()
    {
        var existingRole = new Role { Id = 1, Name = "Operator", PermissionActions = [] };
        var member = new Member
        {
            Id = 7,
            Username = "operator",
            Mail = "operator@test.com",
            PasswordHash = "h",
            SecurityVersion = 4,
            MemberRoles = [new MemberRole { RoleId = 1, Role = existingRole }]
        };
        _contextMock.Setup(c => c.Roles).ReturnsDbSet(new List<Role> { existingRole });
        _contextMock.Setup(c => c.Members).ReturnsDbSet(new List<Member> { member });
        _contextMock.Setup(c => c.PermissionActionTemplates)
            .ReturnsDbSet(new List<PermissionActionTemplate>());
        _contextMock.Setup(c => c.PermissionActions)
            .ReturnsDbSet(new List<PermissionAction>());
        _contextMock.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        await _service.UpsertRoleAsync(1, new RolePayload
        {
            Name = "Operator",
            PermissionActions = []
        });

        Assert.Equal(5, member.SecurityVersion);
    }

    [Fact]
    public async Task UpsertRoleAsync_Throws_WhenNameIsSuperUser()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.UpsertRoleAsync(null, new RolePayload { Name = "SuperUser", PermissionActions = [] }));
    }

    [Fact]
    public async Task UpsertRoleAsync_Throws_WhenUpdatingSuperUserRole()
    {
        var superRole = new Role { Id = 1, Name = "SuperUser", PermissionActions = [] };
        _contextMock.Setup(c => c.Roles).ReturnsDbSet(new List<Role> { superRole });
        _contextMock.Setup(c => c.PermissionActionTemplates).ReturnsDbSet(new List<PermissionActionTemplate>());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.UpsertRoleAsync(1, new RolePayload { Name = "Admin", PermissionActions = [] }));
    }

    [Fact]
    public async Task UpsertRoleAsync_Throws_OnDuplicateName()
    {
        var roles = new List<Role>
        {
            new() { Id = 1, Name = "Admin", PermissionActions = [] },
            new() { Id = 2, Name = "ExistingRole", PermissionActions = [] }
        };
        _contextMock.Setup(c => c.Roles).ReturnsDbSet(roles);
        _contextMock.Setup(c => c.PermissionActionTemplates).ReturnsDbSet(new List<PermissionActionTemplate>());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.UpsertRoleAsync(1, new RolePayload { Name = "ExistingRole", PermissionActions = [] }));
    }

    [Fact]
    public async Task RemoveRoleAsync_DeletesRole()
    {
        var roles = new List<Role> { new() { Id = 1, Name = "Viewer" } };
        _contextMock.Setup(c => c.Roles).ReturnsDbSet(roles);
        Mock.Get(_contextMock.Object.Roles)
            .Setup(m => m.Remove(It.IsAny<Role>()))
            .Callback<Role>(r => roles.Remove(r));
        _contextMock.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        await _service.RemoveRoleAsync(1);

        _contextMock.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        Assert.Empty(roles);
    }

    [Fact]
    public async Task RemoveRoleAsync_Throws_WhenRoleNotFound()
    {
        _contextMock.Setup(c => c.Roles).ReturnsDbSet(new List<Role>());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.RemoveRoleAsync(999));
    }

    [Fact]
    public async Task RemoveRoleAsync_Throws_WhenRoleIsSuperUser()
    {
        var roles = new List<Role> { new() { Id = 1, Name = "SuperUser" } };
        _contextMock.Setup(c => c.Roles).ReturnsDbSet(roles);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.RemoveRoleAsync(1));
    }

    [Fact]
    public async Task UpsertRoleAsync_FiltersInvalidPermissionActions()
    {
        var templates = new List<PermissionActionTemplate>
        {
            new() { Id = 1, PermissionId = 10, ActionId = 20 }
        };
        var roles = new List<Role>
        {
            new() { Id = 0, Name = "TestRole", PermissionActions = [] }
        };

        _contextMock.Setup(c => c.Roles).ReturnsDbSet(roles);
        _contextMock.Setup(c => c.PermissionActionTemplates).ReturnsDbSet(templates);
        _contextMock.Setup(c => c.PermissionActions).ReturnsDbSet(new List<PermissionAction>());
        _contextMock.SetupSequence(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1)
            .ReturnsAsync(1);

        var result = await _service.UpsertRoleAsync(null, new RolePayload
        {
            Name = "TestRole",
            PermissionActions = [
                new PermissionActionSelection { PermissionId = 10, ActionId = 20 },
                new PermissionActionSelection { PermissionId = 99, ActionId = 99 }
            ]
        });

        Assert.Equal("TestRole", result.Name);
    }

    [Fact]
    public async Task GetPermissionActionTemplatesAsync_ReturnsOrderedTemplates()
    {
        var perm1 = new Permission { Id = 2, Name = "Users", Path = "/auth/user" };
        var perm2 = new Permission { Id = 1, Name = "Home", Path = "/home" };
        var act1 = new AuthAction { Id = 1, Code = "view", Name = "view" };
        var act2 = new AuthAction { Id = 2, Code = "edit", Name = "edit" };

        var templates = new List<PermissionActionTemplate>
        {
            new() { Id = 2, PermissionId = 2, ActionId = 2, Permission = perm1, Action = act2 },
            new() { Id = 1, PermissionId = 1, ActionId = 1, Permission = perm2, Action = act1 }
        };
        _contextMock.Setup(c => c.PermissionActionTemplates).ReturnsDbSet(templates);

        var result = (await _service.GetPermissionActionTemplatesAsync()).ToList();

        Assert.Equal(2, result.Count);
        Assert.Equal("/auth/user", result[0].Permission?.Path);
        Assert.Equal("edit", result[0].Action?.Code);
        Assert.Equal("/home", result[1].Permission?.Path);
        Assert.Equal("view", result[1].Action?.Code);
    }
}
