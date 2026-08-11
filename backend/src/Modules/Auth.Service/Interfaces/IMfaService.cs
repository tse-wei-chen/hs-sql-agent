using Auth.Service.Models;

namespace Auth.Service.Interfaces;

public interface IMfaService
{
    Task<MfaSetupVM> BeginSetupAsync(int memberId, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<string>> ConfirmSetupAsync(int memberId, string code, CancellationToken cancellationToken = default);
    Task<bool> VerifyAsync(int memberId, string code, CancellationToken cancellationToken = default);
    Task DisableAsync(int memberId, string code, CancellationToken cancellationToken = default);
    Task<MfaStatusVM> GetStatusAsync(int memberId, CancellationToken cancellationToken = default);
}
