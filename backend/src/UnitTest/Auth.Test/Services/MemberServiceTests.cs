using Auth.Service.Data;
using Auth.Service.Data.Entites;
using Auth.Service.Models;
using Auth.Service.Services;
using Moq;
using Moq.EntityFrameworkCore;
using Xunit;

namespace Auth.Test.Services;

public class MemberServiceTests
{
    private readonly Mock<IAuthContext> _contextMock;
    private readonly MemberService _service;

    public MemberServiceTests()
    {
        _contextMock = new Mock<IAuthContext>();
        _service = new MemberService(_contextMock.Object);
    }

    [Fact]
    public async Task GetMembersAsync_ReturnsAllMembersOrderedByMail()
    {
        var role = new Role { Id = 1, Name = "User" };
        var members = new List<Member>
        {
            new() { Id = 2, Mail = "b@test.com", Username = "b", PasswordHash = "h", MemberRoles = [new MemberRole { RoleId = 1, Role = role }] },
            new() { Id = 1, Mail = "a@test.com", Username = "a", PasswordHash = "h", MemberRoles = [] }
        };
        _contextMock.Setup(c => c.Members).ReturnsDbSet(members);

        var result = (await _service.GetMembersAsync()).ToList();

        Assert.Equal(2, result.Count);
        Assert.Equal("a@test.com", result[0].Mail);
        Assert.Equal("b@test.com", result[1].Mail);
    }

    [Fact]
    public async Task GetMembersAsync_IncludesRoleNames()
    {
        var role = new Role { Id = 1, Name = "Admin" };
        var members = new List<Member>
        {
            new() { Id = 1, Mail = "admin@test.com", Username = "admin", PasswordHash = "h",
                MemberRoles = [new MemberRole { RoleId = 1, Role = role }] }
        };
        _contextMock.Setup(c => c.Members).ReturnsDbSet(members);

        var result = (await _service.GetMembersAsync()).ToList();

        Assert.Contains("Admin", result[0].Roles);
        Assert.Contains(1, result[0].RoleIds);
    }

    [Fact]
    public async Task UpdateMemberRolesAsync_UpdatesRolesSuccessfully()
    {
        var role1 = new Role { Id = 1, Name = "Admin" };
        var role2 = new Role { Id = 2, Name = "User" };
        var roles = new List<Role> { role1, role2 };
        var memberRoles = new List<MemberRole>();
        var member = new Member { Id = 1, Mail = "user@test.com", Username = "user", PasswordHash = "h", MemberRoles = [] };
        var members = new List<Member> { member };

        _contextMock.Setup(c => c.Members).ReturnsDbSet(members);
        _contextMock.Setup(c => c.Roles).ReturnsDbSet(roles);
        _contextMock.Setup(c => c.MemberRoles).ReturnsDbSet(memberRoles);

        // Service adds to member.MemberRoles (navigation property), not _context.MemberRoles.
        // Sync to the backing list on SaveChanges so the re-query finds the data.
        _contextMock.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                memberRoles.Clear();
                foreach (var mr in member.MemberRoles)
                {
                    memberRoles.Add(new MemberRole
                    {
                        MemberId = mr.MemberId,
                        RoleId = mr.RoleId,
                        Role = roles.FirstOrDefault(r => r.Id == mr.RoleId)
                    });
                }
            })
            .ReturnsAsync(1);

        var result = await _service.UpdateMemberRolesAsync(1, new UpdateMemberRolesRequest { RoleIds = [1, 2] });

        Assert.Equal(1, result.Id);
        Assert.Contains("Admin", result.Roles);
        Assert.Contains("User", result.Roles);
        Assert.Equal(2, member.SecurityVersion);
    }

    [Fact]
    public async Task UpdateMemberRolesAsync_Throws_WhenMemberNotFound()
    {
        _contextMock.Setup(c => c.Members).ReturnsDbSet(new List<Member>());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.UpdateMemberRolesAsync(999, new UpdateMemberRolesRequest { RoleIds = [1] }));
    }

    [Fact]
    public async Task DeleteMemberAsync_DeletesMember()
    {
        var members = new List<Member> { new() { Id = 1, Mail = "user@test.com", Username = "user", PasswordHash = "h" } };
        _contextMock.Setup(c => c.Members).ReturnsDbSet(members);
        Mock.Get(_contextMock.Object.Members)
            .Setup(m => m.Remove(It.IsAny<Member>()))
            .Callback<Member>(r => members.Remove(r));
        _contextMock.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        await _service.DeleteMemberAsync(1);

        _contextMock.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        Assert.Empty(members);
    }

    [Fact]
    public async Task DeleteMemberAsync_Throws_WhenMemberNotFound()
    {
        _contextMock.Setup(c => c.Members).ReturnsDbSet(new List<Member>());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.DeleteMemberAsync(999));
    }

    [Fact]
    public async Task UpdateMemberStatusAsync_DisablesMemberAndInvalidatesSessions()
    {
        var member = new Member
        {
            Id = 1,
            Mail = "user@test.com",
            Username = "user",
            PasswordHash = "h",
            IsActive = true,
            SecurityVersion = 3,
            MemberRoles = []
        };
        _contextMock.Setup(c => c.Members).ReturnsDbSet(new List<Member> { member });
        _contextMock.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var result = await _service.UpdateMemberStatusAsync(
            1,
            new UpdateMemberStatusRequest { IsActive = false },
            TestContext.Current.CancellationToken);

        Assert.False(result.IsActive);
        Assert.False(member.IsActive);
        Assert.Equal(4, member.SecurityVersion);
    }

    [Fact]
    public async Task UpdateMemberStatusAsync_RejectsDisablingLastActiveSuperUser()
    {
        var superRole = new Role { Id = 1, Name = AuthService.SuperUserRoleName };
        var member = new Member
        {
            Id = 1,
            Mail = "admin@test.com",
            Username = "admin",
            PasswordHash = "h",
            IsActive = true,
            MemberRoles = [new MemberRole { RoleId = 1, Role = superRole }]
        };
        _contextMock.Setup(c => c.Members).ReturnsDbSet(new List<Member> { member });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.UpdateMemberStatusAsync(
                1,
                new UpdateMemberStatusRequest { IsActive = false },
                TestContext.Current.CancellationToken));

        Assert.Contains("active SuperUser", exception.Message);
        Assert.True(member.IsActive);
    }

    [Fact]
    public async Task UpdateMemberRolesAsync_RejectsUnknownRoleIds()
    {
        var member = new Member
        {
            Id = 1,
            Mail = "user@test.com",
            Username = "user",
            PasswordHash = "h",
            MemberRoles = []
        };
        _contextMock.Setup(c => c.Members).ReturnsDbSet(new List<Member> { member });
        _contextMock.Setup(c => c.Roles).ReturnsDbSet(new List<Role>
        {
            new() { Id = 1, Name = "User" }
        });

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.UpdateMemberRolesAsync(
                1,
                new UpdateMemberRolesRequest { RoleIds = [1, 999] },
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateMemberAsync_CreatesMemberWithSpecifiedRoles()
    {
        var members = new List<Member>();
        var roles = new List<Role> { new() { Id = 1, Name = "User" } };
        var memberRoles = new List<MemberRole>();

        _contextMock.Setup(c => c.Members).ReturnsDbSet(members);
        _contextMock.Setup(c => c.Roles).ReturnsDbSet(roles);
        _contextMock.Setup(c => c.MemberRoles).ReturnsDbSet(memberRoles);

        Mock.Get(_contextMock.Object.Members).Setup(m => m.Add(It.IsAny<Member>()))
            .Callback<Member>(m =>
            {
                m.Id = 1;
                members.Add(m);
            });
        Mock.Get(_contextMock.Object.MemberRoles).Setup(m => m.Add(It.IsAny<MemberRole>()))
            .Callback<MemberRole>(mr => memberRoles.Add(mr));

        _contextMock.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var id = await _service.CreateMemberAsync(new CreateMemberRequest
        {
            Email = "new@test.com",
            Password = "pass123",
            Username = "newuser",
            RoleIds = [1]
        }, TestContext.Current.CancellationToken);

        Assert.NotEqual(0, id);
        Assert.Single(members);
        Assert.Equal("new@test.com", members[0].Mail);
        Assert.Single(memberRoles);
    }

    [Fact]
    public async Task CreateMemberAsync_Throws_WhenEmailExists()
    {
        var members = new List<Member> { new() { Id = 1, Mail = "dup@test.com", PasswordHash = "h", Username = "dup" } };
        _contextMock.Setup(c => c.Members).ReturnsDbSet(members);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.CreateMemberAsync(new CreateMemberRequest { Email = "dup@test.com", Password = "pass", RoleIds = [] }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateMemberAsync_AssignsAllRoles_WhenAssignAllRolesTrue()
    {
        var members = new List<Member>();
        var roles = new List<Role>
        {
            new() { Id = 1, Name = "Admin" },
            new() { Id = 2, Name = "User" }
        };
        var memberRoles = new List<MemberRole>();

        _contextMock.Setup(c => c.Members).ReturnsDbSet(members);
        _contextMock.Setup(c => c.Roles).ReturnsDbSet(roles);
        _contextMock.Setup(c => c.MemberRoles).ReturnsDbSet(memberRoles);

        Mock.Get(_contextMock.Object.Members).Setup(m => m.Add(It.IsAny<Member>()))
            .Callback<Member>(m =>
            {
                m.Id = 1;
                members.Add(m);
            });
        Mock.Get(_contextMock.Object.MemberRoles).Setup(m => m.Add(It.IsAny<MemberRole>()))
            .Callback<MemberRole>(mr => memberRoles.Add(mr));

        _contextMock.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var id = await _service.CreateMemberAsync(new CreateMemberRequest
        {
            Email = "allroles@test.com",
            Password = "pass123",
            AssignAllRoles = true,
            RoleIds = []
        }, TestContext.Current.CancellationToken);

        Assert.NotEqual(0, id);
        Assert.Equal(2, memberRoles.Count);
    }

    [Fact]
    public async Task CreateMemberAsync_DefaultsUsernameFromEmail()
    {
        var members = new List<Member>();
        var roles = new List<Role>();

        _contextMock.Setup(c => c.Members).ReturnsDbSet(members);
        _contextMock.Setup(c => c.Roles).ReturnsDbSet(roles);
        _contextMock.Setup(c => c.MemberRoles).ReturnsDbSet(new List<MemberRole>());

        Mock.Get(_contextMock.Object.Members).Setup(m => m.Add(It.IsAny<Member>()))
            .Callback<Member>(m =>
            {
                m.Id = 1;
                members.Add(m);
            });

        _contextMock.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var id = await _service.CreateMemberAsync(new CreateMemberRequest
        {
            Email = "janedoe@test.com",
            Password = "pass123"
        }, TestContext.Current.CancellationToken);

        Assert.NotEqual(0, id);
        Assert.Equal("janedoe", members[0].Username);
    }
}
