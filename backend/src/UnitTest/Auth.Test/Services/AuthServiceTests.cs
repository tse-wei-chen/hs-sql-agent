using Auth.Service.Data;
using Auth.Service.Data.Entites;
using Auth.Service.Models;
using Auth.Service.Services;
using Microsoft.Extensions.Options;
using Moq;
using Moq.EntityFrameworkCore;
using Xunit;

namespace Auth.Test.Services;

public class AuthServiceTests
{
    private readonly Mock<IAuthContext> _contextMock;
    private readonly IOptions<JwtSettings> _jwtSettings;
    private readonly AuthService _service;

    public AuthServiceTests()
    {
        _contextMock = new Mock<IAuthContext>();
        _jwtSettings = Options.Create(new JwtSettings
        {
            SecretKey = "ThisIsATestSecretKeyThatIsAtLeast32BytesLong!",
            Issuer = "Test",
            Audience = "Test",
            AccessTokenExpirationMinutes = 1,
            RefreshTokenExpirationDays = 30
        });
        _service = new AuthService(_contextMock.Object, _jwtSettings);
    }

    [Fact]
    public async Task IsFirstRunAsync_ReturnsTrue_WhenNoMembers()
    {
        _contextMock.Setup(c => c.Members).ReturnsDbSet(new List<Member>());
        Assert.True(await _service.IsFirstRunAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsFirstRunAsync_ReturnsFalse_WhenMembersExist()
    {
        var members = new List<Member> { new() { Id = 1, Mail = "test@test.com", PasswordHash = "hash", Username = "test" } };
        _contextMock.Setup(c => c.Members).ReturnsDbSet(members);
        Assert.False(await _service.IsFirstRunAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SignInAsync_ReturnsAuthResult_WhenCredentialsValid()
    {
        var password = "password123";
        var hash = BCrypt.Net.BCrypt.HashPassword(password);
        var member = new Member { Id = 1, Mail = "user@test.com", PasswordHash = hash, Username = "user" };

        _contextMock.Setup(c => c.Members).ReturnsDbSet(new List<Member> { member });
        _contextMock.Setup(c => c.MemberRoles).ReturnsDbSet(new List<MemberRole>());
        _contextMock.Setup(c => c.PermissionActions).ReturnsDbSet(new List<PermissionAction>());

        var result = await _service.SignInAsync(new SignInRequest { Email = "user@test.com", Password = password }, TestContext.Current.CancellationToken);

        Assert.Equal("user", result.UserName);
        Assert.Equal("user@test.com", result.Email);
        Assert.NotNull(result.AccessToken);
        Assert.NotNull(result.RefreshToken);
    }

    [Fact]
    public async Task SignInAsync_Throws_WhenEmailNotFound()
    {
        _contextMock.Setup(c => c.Members).ReturnsDbSet(new List<Member>());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _service.SignInAsync(new SignInRequest { Email = "nonexistent@test.com", Password = "pwd" }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SignInAsync_Throws_WhenPasswordWrong()
    {
        var member = new Member { Id = 1, Mail = "user@test.com", PasswordHash = BCrypt.Net.BCrypt.HashPassword("correct"), Username = "user" };
        _contextMock.Setup(c => c.Members).ReturnsDbSet(new List<Member> { member });

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _service.SignInAsync(new SignInRequest { Email = "user@test.com", Password = "wrong" }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SignUpFirstAdminAsync_CreatesSuperUserRoleAndMember()
    {
        var members = new List<Member>();
        var roles = new List<Role>();
        var memberRoles = new List<MemberRole>();
        var permissionActions = new List<PermissionAction>();

        _contextMock.Setup(c => c.Members).ReturnsDbSet(members);
        _contextMock.Setup(c => c.Roles).ReturnsDbSet(roles);
        _contextMock.Setup(c => c.MemberRoles).ReturnsDbSet(memberRoles);
        _contextMock.Setup(c => c.PermissionActionTemplates).ReturnsDbSet(new List<PermissionActionTemplate>());
        _contextMock.Setup(c => c.PermissionActions).ReturnsDbSet(permissionActions);

        // Capture Add so DbSet queries can find created entities
        Mock.Get(_contextMock.Object.Members)
            .Setup(m => m.Add(It.IsAny<Member>()))
            .Callback<Member>(m =>
            {
                m.Id = 1;
                members.Add(m);
            });
        Mock.Get(_contextMock.Object.Roles)
            .Setup(m => m.Add(It.IsAny<Role>()))
            .Callback<Role>(r => roles.Add(r));
        Mock.Get(_contextMock.Object.MemberRoles)
            .Setup(m => m.Add(It.IsAny<MemberRole>()))
            .Callback<MemberRole>(mr => memberRoles.Add(mr));

        _contextMock.SetupSequence(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1)
            .ReturnsAsync(1)
            .ReturnsAsync(1);

        var result = await _service.SignUpFirstAdminAsync(new SignUpRequest { Email = "admin@test.com", Password = "admin123" }, TestContext.Current.CancellationToken);

        Assert.Equal("admin", result.UserName);
        Assert.Equal("admin@test.com", result.Email);
        Assert.NotNull(result.AccessToken);
        Assert.NotNull(result.RefreshToken);
        Assert.Single(roles);
        Assert.Equal("SuperUser", roles[0].Name);
    }

    [Fact]
    public async Task SignUpFirstAdminAsync_Throws_WhenMembersAlreadyExist()
    {
        var members = new List<Member> { new() { Id = 1, Mail = "existing@test.com", PasswordHash = "h", Username = "existing" } };
        _contextMock.Setup(c => c.Members).ReturnsDbSet(members);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SignUpFirstAdminAsync(new SignUpRequest { Email = "admin@test.com", Password = "admin123" }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RefreshTokenAsync_ReturnsAuthResult_WhenMemberExists()
    {
        var member = new Member { Id = 1, Mail = "user@test.com", PasswordHash = "hash", Username = "user" };
        _contextMock.Setup(c => c.Members).ReturnsDbSet(new List<Member> { member });
        _contextMock.Setup(c => c.MemberRoles).ReturnsDbSet(new List<MemberRole>());
        _contextMock.Setup(c => c.PermissionActions).ReturnsDbSet(new List<PermissionAction>());

        var result = await _service.RefreshTokenAsync("1", TestContext.Current.CancellationToken);

        Assert.Equal("user", result.UserName);
        Assert.NotNull(result.AccessToken);
        Assert.NotNull(result.RefreshToken);
    }

    [Fact]
    public async Task RefreshTokenAsync_Throws_WhenIdNotParseable()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.RefreshTokenAsync("not-a-number", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RefreshTokenAsync_Throws_WhenMemberNotFound()
    {
        _contextMock.Setup(c => c.Members).ReturnsDbSet(new List<Member>());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _service.RefreshTokenAsync("999", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BuildAuthResultAsync_IncludesRolesAndPermissions()
    {
        var role = new Role { Id = 1, Name = "Admin" };
        var member = new Member { Id = 1, Mail = "admin@test.com", PasswordHash = BCrypt.Net.BCrypt.HashPassword("pass"), Username = "admin" };
        var memberRoles = new List<MemberRole>
        {
            new() { MemberId = 1, RoleId = 1, Member = member, Role = role }
        };
        var perm = new Permission { Id = 1, Name = "Home", Path = "/home" };
        var action = new AuthAction { Id = 1, Code = "view", Name = "view" };
        var permActions = new List<PermissionAction>
        {
            new() { Id = 1, RoleId = 1, PermissionId = 1, ActionId = 1, Role = role, Permission = perm, Action = action }
        };

        member.MemberRoles = memberRoles;

        _contextMock.Setup(c => c.Members).ReturnsDbSet(new List<Member> { member });
        _contextMock.Setup(c => c.MemberRoles).ReturnsDbSet(memberRoles);
        _contextMock.Setup(c => c.PermissionActions).ReturnsDbSet(permActions);

        var result = await _service.SignInAsync(new SignInRequest { Email = "admin@test.com", Password = "pass" }, TestContext.Current.CancellationToken);

        Assert.Contains("Admin", result.Roles);
        var homePerm = Assert.Single(result.Permissions);
        Assert.Equal("/home", homePerm.Path);
        Assert.Contains(homePerm.Actions, a => a.Code == "view");
    }

    [Fact]
    public async Task TokenGeneration_ReturnsDifferentAccessAndRefreshTokens()
    {
        var member = new Member { Id = 42, Mail = "dev@test.com", PasswordHash = BCrypt.Net.BCrypt.HashPassword("pass"), Username = "dev" };
        _contextMock.Setup(c => c.Members).ReturnsDbSet(new List<Member> { member });
        _contextMock.Setup(c => c.MemberRoles).ReturnsDbSet(new List<MemberRole>());
        _contextMock.Setup(c => c.PermissionActions).ReturnsDbSet(new List<PermissionAction>());

        var result = await _service.SignInAsync(new SignInRequest { Email = "dev@test.com", Password = "pass" }, TestContext.Current.CancellationToken);

        Assert.NotNull(result.AccessToken);
        Assert.NotNull(result.RefreshToken);
        Assert.NotEqual(result.AccessToken, result.RefreshToken);
    }
}
