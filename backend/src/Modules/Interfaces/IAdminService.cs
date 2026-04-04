using Modules.Models;

namespace Modules.Interfaces;

public interface IAdminService
{
    Task<bool> IsFirstRunAsync();
    Task<SignInVM> SignInAsync(SignInRequest request);
    Task ChangePasswordAsync(ChangePasswordRequest request, string userEmail);
    Task<SignInVM> RefreshTokenAsync(string id);
};