using Microsoft.AspNetCore.Authorization;

namespace HsSqlAgent.Server.Attributes;

public sealed class RefreshAuthorizeAttribute : AuthorizeAttribute
{
    public RefreshAuthorizeAttribute()
    {
        Policy = "RefreshTokenPolicy";
    }
}
