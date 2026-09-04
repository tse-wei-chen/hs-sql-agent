using HsSqlAgent.Server.Controllers;
using HsSqlAgent.Server.Filters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace HsSqlAgent.Server.Extensions;

internal sealed class HsSqlAgentControllerSurfaceConvention(HsSqlAgentRegistrationBuilder registration)
    : IApplicationModelConvention
{
    private static readonly Type ServerAssemblyMarker = typeof(AuthController);

    public void Apply(ApplicationModel application)
    {
        for (var index = application.Controllers.Count - 1; index >= 0; index--)
        {
            var controller = application.Controllers[index];
            var controllerType = controller.ControllerType.AsType();
            if (controllerType.Assembly != ServerAssemblyMarker.Assembly)
                continue;

            if (!registration.IsRegistered("built-in-auth") &&
                (controllerType == typeof(AuthController) ||
                 controllerType == typeof(MemberController) ||
                 controllerType == typeof(RoleController)))
            {
                application.Controllers.RemoveAt(index);
                continue;
            }

            controller.Filters.Add(new ServiceFilterAttribute(typeof(HsSqlAgentValidationFilter)));
            controller.Filters.Add(new ServiceFilterAttribute(typeof(HsSqlAgentExceptionFilter)));
        }
    }
}
