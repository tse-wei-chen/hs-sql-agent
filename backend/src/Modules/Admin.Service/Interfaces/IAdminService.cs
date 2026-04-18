using Admin.Service.Models;

namespace Admin.Service.Interfaces;

public interface IAdminService
{
    Task<bool> IsFirstRunAsync();
    Task<PermissionVM> SignInAsync(SignInRequest request);
    Task<PermissionVM> SignUpAsync(SignUpRequest request);
    Task<PermissionVM> RefreshTokenAsync(string id);
};
