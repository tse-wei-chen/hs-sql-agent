using Auth.Service.Models;

namespace Auth.Service.Interfaces;

public interface IAuthService
{
    Task<bool> IsFirstRunAsync(CancellationToken cancellationToken = default);
    Task<AuthResult> SignInAsync(SignInRequest request, CancellationToken cancellationToken = default);
    Task<AuthResult> SignUpFirstAdminAsync(SignUpRequest request, CancellationToken cancellationToken = default);
    Task<AuthResult> RefreshTokenAsync(string id, int securityVersion, CancellationToken cancellationToken = default);
}
