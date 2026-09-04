using FluentValidation;
using HsSqlAgent.Server.Filters;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HsSqlAgent.Server.Test.Extensions;

public class HsSqlAgentValidationFilterTests
{
    [Fact]
    public async Task InvalidHsArgument_IsValidatedAndShortCircuited()
    {
        var services = new ServiceCollection();
        services.AddControllers();
        services.AddSingleton<IValidator<ProbeRequest>, ProbeRequestValidator>();
        using var provider = services.BuildServiceProvider();

        var httpContext = new DefaultHttpContext { RequestServices = provider };
        var actionContext = new ActionContext(
            httpContext,
            new RouteData(),
            new ActionDescriptor(),
            new ModelStateDictionary());
        var executing = new ActionExecutingContext(
            actionContext,
            [],
            new Dictionary<string, object?> { ["request"] = new ProbeRequest() },
            new object());
        var nextCalled = false;

        await new HsSqlAgentValidationFilter().OnActionExecutionAsync(
            executing,
            () =>
            {
                nextCalled = true;
                return Task.FromResult(new ActionExecutedContext(actionContext, [], new object()));
            });

        Assert.False(nextCalled);
        Assert.False(executing.ModelState.IsValid);
        Assert.NotNull(executing.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsType<BadRequestObjectResult>(executing.Result).StatusCode);
    }

    private sealed class ProbeRequest
    {
        public string Name { get; init; } = string.Empty;
    }

    private sealed class ProbeRequestValidator : AbstractValidator<ProbeRequest>
    {
        public ProbeRequestValidator()
        {
            RuleFor(request => request.Name).NotEmpty();
        }
    }
}
