using Auth.Service.Models;

namespace Auth.Service.Interfaces;

public interface IEnterpriseIdentityService
{
    Task<string> CreateExternalLoginCodeAsync(string provider, string subject, string email, string? name, IReadOnlyCollection<string> externalRoles, CancellationToken cancellationToken = default);
    Task<AuthResult> ExchangeExternalLoginCodeAsync(string code, CancellationToken cancellationToken = default, string? ipAddress = null, string? userAgent = null);
}
