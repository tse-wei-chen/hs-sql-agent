using HsSqlAgent.Server.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace HsSqlAgent.Server.Attributes;

public sealed class RefreshAuthorizeAttribute : AuthorizeAttribute
{
    public RefreshAuthorizeAttribute()
    {
        Policy = HsSqlAgentAuthorizationPolicies.RefreshToken;
    }
}
