namespace Auth.Service.Interfaces;

public interface ITokenRevocationService
{
    Task<bool> IsRevokedAsync(string jti, CancellationToken cancellationToken = default);
    Task RevokeAsync(string jti, DateTime expiresAt, CancellationToken cancellationToken = default);
}
