using Auth.Service.Models;

namespace Auth.Service.Interfaces;

public interface IPasswordResetService
{
    Task RequestAsync(ForgotPasswordRequest request, CancellationToken cancellationToken = default);
    Task ResetAsync(ResetPasswordRequest request, CancellationToken cancellationToken = default);
}
