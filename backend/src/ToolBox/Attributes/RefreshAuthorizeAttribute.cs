using Microsoft.AspNetCore.Authorization;

namespace ToolBox.Attributes;

public sealed class RefreshAuthorizeAttribute : AuthorizeAttribute
{
	public RefreshAuthorizeAttribute()
	{
		Policy = "RefreshTokenPolicy";
	}
}
