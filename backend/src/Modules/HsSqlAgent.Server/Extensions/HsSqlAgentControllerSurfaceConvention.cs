using HsSqlAgent.Server.Controllers;
using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace HsSqlAgent.Server.Extensions;

internal sealed class HsSqlAgentControllerSurfaceConvention(HsSqlAgentRegistrationBuilder registration)
    : IApplicationModelConvention
{
    public void Apply(ApplicationModel application)
    {
        if (registration.IsRegistered("built-in-auth"))
            return;

        for (var index = application.Controllers.Count - 1; index >= 0; index--)
        {
            if (application.Controllers[index].ControllerType.AsType() == typeof(AuthController))
                application.Controllers.RemoveAt(index);
        }
    }
}
