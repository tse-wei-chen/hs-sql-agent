using HsSqlAgent.Approvals;
using HsSqlAgent.Approvals.Webhook;
using HsSqlAgent.Hosting;
using HsSqlAgent.Server.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace HsSqlAgent.Server.Test.Hosting;

public sealed class HsSqlAgentStandardHostTests
{
    private const string Secret = "standard-host-secret-that-is-at-least-32-bytes";

    [Fact]
    public void StandardHost_DefaultsToMcpElicitationWithoutRegisteringProvider()
    {
        var builder = CreateBuilder();

        builder.AddHsSqlAgentStandardHost();

        Assert.DoesNotContain(
            builder.Services,
            descriptor => descriptor.ServiceType == typeof(IDmlApprovalProvider));
    }

    [Fact]
    public void StandardHost_WebhookSelectorRegistersAndBindsWebhookProvider()
    {
        var builder = CreateBuilder();
        builder.Configuration["DmlApproval:Provider"] = "Webhook";
        builder.Configuration["DmlApproval:Webhook:Endpoint"] =
            "https://approval.example.test/hssqlagent/requests";
        builder.Configuration["DmlApproval:Webhook:CallbackUrl"] =
            "https://sql-agent.example.test/api/hs-sql-agent/approvals/webhook";
        builder.Configuration["DmlApproval:Webhook:SigningSecret"] = Secret;

        builder.AddHsSqlAgentStandardHost();

        using var services = builder.Services.BuildServiceProvider();
        Assert.IsType<WebhookDmlApprovalProvider>(services.GetRequiredService<IDmlApprovalProvider>());

        var options = services.GetRequiredService<IOptions<WebhookApprovalOptions>>().Value;
        Assert.Equal(
            "https://approval.example.test/hssqlagent/requests",
            options.Endpoint?.AbsoluteUri);
        Assert.Equal(
            "https://sql-agent.example.test/api/hs-sql-agent/approvals/webhook",
            options.CallbackUrl?.AbsoluteUri);
        Assert.Equal(Secret, options.SigningSecret);
    }

    [Fact]
    public void StandardHost_RejectsUnsupportedApprovalProvider()
    {
        var builder = CreateBuilder();
        builder.Configuration["DmlApproval:Provider"] = "AnythingElse";

        var exception = Assert.Throws<InvalidOperationException>(
            () => builder.AddHsSqlAgentStandardHost());

        Assert.Contains("McpElicitation", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Webhook", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StandardHost_RejectsPreRegisteredCustomApprovalProvider()
    {
        var builder = CreateBuilder();
        builder.Services.AddSingleton<IDmlApprovalProvider, ExistingProvider>();

        Assert.Throws<InvalidOperationException>(
            () => builder.AddHsSqlAgentStandardHost());
    }

    [Fact]
    public void PackageBoundary_ServerDoesNotReferenceHostingOrWebhook()
    {
        var references = typeof(HsSqlAgentRegistrationBuilder)
            .Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name)
            .ToArray();

        Assert.DoesNotContain("HsSqlAgent.Hosting", references);
        Assert.DoesNotContain("HsSqlAgent.Approvals.Webhook", references);
    }

    [Fact]
    public void PackageBoundary_HostingReferencesServerAndWebhook()
    {
        var references = typeof(HsSqlAgentStandardHostExtensions)
            .Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name)
            .ToArray();

        Assert.Contains("HsSqlAgent.Server", references);
        Assert.Contains("HsSqlAgent.Approvals.Webhook", references);
    }

    private static WebApplicationBuilder CreateBuilder()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });

        builder.Configuration["AdminDatabase:Provider"] = "Sqlite";
        builder.Configuration["AdminDatabase:ConnectionString"] = "Data Source=standard-host-test.db";
        builder.Configuration["JwtSettings:SecretKey"] = new string('J', 64);
        builder.Configuration["McpKeySettings:HmacSecretKey"] = new string('H', 64);
        builder.Configuration["Mcp:PublicEndpoint"] = "http://localhost:8080/mcp";
        return builder;
    }

    private sealed class ExistingProvider : IDmlApprovalProvider
    {
        public ValueTask<DmlApprovalResult> RequestApprovalAsync(
            DmlApprovalRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(DmlApprovalResult.Reject(request));
    }
}
