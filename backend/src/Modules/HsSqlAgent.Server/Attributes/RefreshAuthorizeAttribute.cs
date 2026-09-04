using HsSqlAgent.Server.Authorization;

namespace HsSqlAgent.Server.Attributes;

public sealed class RefreshAuthorizeAttribute()
    : HsSqlAgentAuthenticationAttribute(HsSqlAgentAuthenticationSchemes.Bearer, "refresh");
