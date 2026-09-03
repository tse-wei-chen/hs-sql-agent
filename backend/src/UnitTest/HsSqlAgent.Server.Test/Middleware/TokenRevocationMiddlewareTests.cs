using System.Data.Common;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Auth.Service.Data;
using Auth.Service.Data.Entites;
using Auth.Service.Interfaces;
using Auth.Service.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using HsSqlAgent.Server.Middleware;
using Moq.EntityFrameworkCore;
using Xunit;

namespace HsSqlAgent.Server.Test.Middleware;

public class TokenRevocationMiddlewareTests
{
    private static readonly Guid ActiveSessionId = Guid.NewGuid();
    private readonly Mock<ITokenRevocationService> _revocationMock;
    private readonly Mock<IAuthContext> _authContextMock;

    public TokenRevocationMiddlewareTests()
    {
        _revocationMock = new Mock<ITokenRevocationService>();
        _authContextMock = new Mock<IAuthContext>();

        var member = new Member
        {
            Id = 1,
            Username = "user",
            Mail = "user@test.com",
            NormalizedMail = "USER@TEST.COM",
            PasswordHash = "hash",
            IsActive = true,
            SecurityVersion = 1
        };
        var session = new AuthSession
        {
            Id = ActiveSessionId,
            MemberId = member.Id,
            Member = member,
            ExpiresAt = DateTime.UtcNow.AddDays(1)
        };
        member.AuthSessions.Add(session);

        _authContextMock.Setup(x => x.Members).ReturnsDbSet(new List<Member> { member });
        _authContextMock.Setup(x => x.AuthSessions).ReturnsDbSet(new List<AuthSession> { session });
    }

    [Fact]
    public async Task InvokeAsync_Returns401_WhenJtiIsRevoked()
    {
        var jti = "revoked-jti";
        var context = CreateContextWithJti(jti);
        _revocationMock.Setup(r => r.IsRevokedAsync(jti, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };

        var middleware = new TokenRevocationMiddleware(next);
        await middleware.InvokeAsync(context, _revocationMock.Object, _authContextMock.Object);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_CallsNext_WhenJtiIsNotRevoked()
    {
        var jti = "valid-jti";
        var context = CreateContextWithJti(jti);
        _revocationMock.Setup(r => r.IsRevokedAsync(jti, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };

        var middleware = new TokenRevocationMiddleware(next);
        await middleware.InvokeAsync(context, _revocationMock.Object, _authContextMock.Object);

        Assert.NotEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_CallsNext_WhenNoJtiClaim()
    {
        var context = CreateContextWithoutJti();
        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };

        var middleware = new TokenRevocationMiddleware(next);
        await middleware.InvokeAsync(context, _revocationMock.Object, _authContextMock.Object);

        Assert.True(nextCalled);
        _revocationMock.Verify(r => r.IsRevokedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InvokeAsync_CallsNext_WhenJtiIsEmpty()
    {
        var context = CreateContextWithJti("");
        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };

        var middleware = new TokenRevocationMiddleware(next);
        await middleware.InvokeAsync(context, _revocationMock.Object, _authContextMock.Object);

        Assert.True(nextCalled);
        _revocationMock.Verify(r => r.IsRevokedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }



    [Fact]
    public async Task InvokeAsync_CachedAuthState_AvoidsAuthDatabaseQuery()
    {
        var context = CreateContextWithJti("cached-jti");
        _revocationMock.Setup(r => r.IsRevokedAsync("cached-jti", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var strictContext = new Mock<IAuthContext>(MockBehavior.Strict);
        var runtimeCache = new Mock<IAuthRuntimeStateCache>(MockBehavior.Strict);
        runtimeCache.Setup(cache => cache.GetOrLoadAsync(
                strictContext.Object,
                1,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthRuntimeState
            {
                Exists = true,
                IsActive = true,
                SecurityVersion = 1,
                ActiveSessions =
                [
                    new AuthRuntimeSessionState
                    {
                        Id = ActiveSessionId,
                        ExpiresAt = DateTime.UtcNow.AddMinutes(5)
                    }
                ]
            });
        var nextCalled = false;
        var middleware = new TokenRevocationMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(
            context,
            _revocationMock.Object,
            strictContext.Object,
            runtimeCache.Object);

        Assert.True(nextCalled);
        runtimeCache.VerifyAll();
        strictContext.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task InvokeAsync_CacheBarrier_FailsClosedWithoutDatabaseRead()
    {
        var context = CreateContextWithJti("barrier-jti");
        context.Response.Body = new MemoryStream();
        _revocationMock.Setup(r => r.IsRevokedAsync("barrier-jti", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var strictContext = new Mock<IAuthContext>(MockBehavior.Strict);
        var runtimeCache = new Mock<IAuthRuntimeStateCache>(MockBehavior.Strict);
        runtimeCache.Setup(cache => cache.GetOrLoadAsync(
                strictContext.Object,
                1,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthRuntimeState
            {
                Exists = true,
                IsBarrier = true,
                BarrierReason = "security state changing"
            });
        var middleware = new TokenRevocationMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(
            context,
            _revocationMock.Object,
            strictContext.Object,
            runtimeCache.Object);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        strictContext.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task InvokeAsync_AuthenticatedRequest_LoadsMemberAndSessionStateWithOneSqlCommand()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var counter = new CommandCountingInterceptor();
        var options = new DbContextOptionsBuilder<AuthContext>()
            .UseSqlite(connection)
            .AddInterceptors(counter)
            .Options;
        await using var authContext = new AuthContext(options);
        await authContext.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var member = new Member
        {
            Id = 1,
            Username = "user",
            Mail = "user@test.com",
            NormalizedMail = "USER@TEST.COM",
            PasswordHash = "hash",
            IsActive = true,
            SecurityVersion = 1
        };
        authContext.Members.Add(member);
        authContext.AuthSessions.Add(new AuthSession
        {
            Id = ActiveSessionId,
            MemberId = member.Id,
            ExpiresAt = DateTime.UtcNow.AddDays(1),
            CurrentRefreshTokenHash = new string('a', 64)
        });
        await authContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        authContext.ChangeTracker.Clear();
        counter.Reset();

        var context = CreateContextWithJti("valid-jti");
        _revocationMock.Setup(r => r.IsRevokedAsync("valid-jti", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var nextCalled = false;
        var middleware = new TokenRevocationMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, _revocationMock.Object, authContext);

        Assert.True(nextCalled);
        Assert.Equal(1, counter.ReaderCommandCount);
    }

    [Fact]
    public async Task InvokeAsync_ReturnsSessionExpired_WhenSessionIsMissing_ButMemberIsValid()
    {
        var member = new Member
        {
            Id = 1,
            Username = "user",
            Mail = "user@test.com",
            NormalizedMail = "USER@TEST.COM",
            PasswordHash = "hash",
            IsActive = true,
            SecurityVersion = 1
        };
        _authContextMock.Setup(x => x.Members).ReturnsDbSet(new List<Member> { member });
        _authContextMock.Setup(x => x.AuthSessions).ReturnsDbSet(new List<AuthSession>());

        var context = CreateContextWithJti("valid-jti");
        context.Response.Body = new MemoryStream();
        _revocationMock.Setup(r => r.IsRevokedAsync("valid-jti", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var middleware = new TokenRevocationMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context, _revocationMock.Object, _authContextMock.Object);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        var body = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.Contains("session_expired", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_Returns401_WhenSecurityVersionIsStale()
    {
        _authContextMock.Setup(x => x.Members).ReturnsDbSet(new List<Member>
        {
            new() { Id = 1, Username = "user", Mail = "user@test.com", PasswordHash = "hash", IsActive = true, SecurityVersion = 2 }
        });
        var context = CreateContextWithJti("valid-jti");
        _revocationMock.Setup(r => r.IsRevokedAsync("valid-jti", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var nextCalled = false;
        var middleware = new TokenRevocationMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, _revocationMock.Object, _authContextMock.Object);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.False(nextCalled);
    }


    private sealed class CommandCountingInterceptor : DbCommandInterceptor
    {
        private int _readerCommandCount;

        public int ReaderCommandCount => Volatile.Read(ref _readerCommandCount);

        public void Reset() => Interlocked.Exchange(ref _readerCommandCount, 0);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _readerCommandCount);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private static DefaultHttpContext CreateContextWithJti(string jti)
    {
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Jti, jti),
            new Claim(JwtRegisteredClaimNames.Typ, "access"),
            new Claim(JwtRegisteredClaimNames.Sub, "1"),
            new Claim(JwtRegisteredClaimNames.Typ, "access"),
            new Claim(AuthService.SecurityVersionClaim, "1"),
            new Claim(AuthService.SessionIdClaim, ActiveSessionId.ToString())
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);
        return new DefaultHttpContext { User = principal };
    }

    private static DefaultHttpContext CreateContextWithoutJti()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(JwtRegisteredClaimNames.Sub, "1"),
            new Claim(AuthService.SecurityVersionClaim, "1"),
            new Claim(AuthService.SessionIdClaim, ActiveSessionId.ToString())
        ], "TestAuth");
        var principal = new ClaimsPrincipal(identity);
        return new DefaultHttpContext { User = principal };
    }
}
