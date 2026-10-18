using HsSqlAgent.Server.Controllers;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.OpenApi;

var builder = WebApplication.CreateBuilder(args);

// Specification-only host. Does not register any database, MCP runtime, external auth,
// Admin UI, or production endpoint. MVC metadata is extracted from the real controllers.
builder.Services
    .AddControllers()
    .AddApplicationPart(typeof(ManagementApiKeysController).Assembly);

builder.Services.AddOpenApi("admin", options =>
{
    options.ShouldInclude = description =>
        description.ActionDescriptor is ControllerActionDescriptor action &&
        action.ControllerTypeInfo.Assembly == typeof(ManagementApiKeysController).Assembly;
});

var app = builder.Build();
app.MapControllers();
app.MapOpenApi();

// In the build-time GetDocument host this is a mock server; no public Swagger UI is created.
await app.RunAsync();
