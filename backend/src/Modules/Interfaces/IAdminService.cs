using Modules.Models;

namespace Modules.Interfaces;

public interface IAdminService
{
    Task<bool> IsFirstRunAsync();
    Task<PermissionVM> SignInAsync(SignInRequest request);
    Task<PermissionVM> SignUpAsync(SignUpRequest request);
    Task ChangePasswordAsync(ChangePasswordRequest request, string userEmail);
    Task<PermissionVM> RefreshTokenAsync(string id);
};