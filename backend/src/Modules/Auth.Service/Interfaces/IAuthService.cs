using Auth.Service.Models;

namespace Auth.Service.Interfaces;

public interface IAuthService
{
    Task<bool> IsFirstRunAsync(CancellationToken cancellationToken = default);
    Task<AuthResult> SignInAsync(SignInRequest request, CancellationToken cancellationToken = default, string? ipAddress = null, string? userAgent = null);
    Task<AuthResult> SignUpFirstAdminAsync(SignUpRequest request, CancellationToken cancellationToken = default, string? ipAddress = null, string? userAgent = null);
    Task<AuthResult> BeginMemberSignInAsync(int memberId, CancellationToken cancellationToken = default, string? ipAddress = null, string? userAgent = null);
    Task<AuthResult> CompleteMfaSignInAsync(int memberId, int securityVersion, CancellationToken cancellationToken = default, string? ipAddress = null, string? userAgent = null);
    Task<AuthResult> RefreshTokenAsync(string id, int securityVersion, Guid sessionId, string refreshTokenId, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<AuthSessionVM>> GetSessionsAsync(int memberId, Guid currentSessionId, CancellationToken cancellationToken = default);
    Task RevokeSessionAsync(int memberId, Guid sessionId, string reason, CancellationToken cancellationToken = default);
    Task RevokeAllSessionsAsync(int memberId, Guid? exceptSessionId, string reason, CancellationToken cancellationToken = default);
}
