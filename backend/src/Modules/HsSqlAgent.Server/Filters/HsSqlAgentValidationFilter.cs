using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace HsSqlAgent.Server.Filters;

/// <summary>
/// Runs FluentValidation only for HsSqlAgent controller action arguments. The filter is attached by
/// HsSqlAgentControllerSurfaceConvention and never participates in the host application's controllers.
/// </summary>
internal sealed class HsSqlAgentValidationFilter : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var services = context.HttpContext.RequestServices;
        var cancellationToken = context.HttpContext.RequestAborted;

        foreach (var argument in context.ActionArguments.Values.Where(value => value is not null))
        {
            var argumentType = argument!.GetType();
            var validatorType = typeof(IValidator<>).MakeGenericType(argumentType);
            if (services.GetService(validatorType) is not IValidator validator)
                continue;

            IValidationContext validationContext = new ValidationContext<object>(argument);
            var result = await validator.ValidateAsync(validationContext, cancellationToken);
            foreach (var failure in result.Errors)
            {
                context.ModelState.AddModelError(
                    failure.PropertyName ?? string.Empty,
                    failure.ErrorMessage);
            }
        }

        if (!context.ModelState.IsValid)
        {
            var apiBehavior = services.GetRequiredService<IOptions<ApiBehaviorOptions>>().Value;
            context.Result = apiBehavior.InvalidModelStateResponseFactory(context);
            return;
        }

        await next();
    }
}
