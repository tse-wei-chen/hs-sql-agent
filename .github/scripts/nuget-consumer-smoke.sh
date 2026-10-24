#!/usr/bin/env bash
set -euo pipefail

package_source="$(cd "$1" && pwd)"

# Keep the first-party standalone/Docker composition on the same CI path as the public packages.
# This catches ToolBox references/configuration that Server-only tests cannot see.
dotnet build backend/src/ToolBox/ToolBox.csproj --configuration Release

grep -Fq \
  'COPY backend/src/Modules/HsSqlAgent.Hosting/HsSqlAgent.Hosting.csproj ./backend/src/Modules/HsSqlAgent.Hosting/' \
  Dockerfile

# Server depends on the transport-neutral approval contracts. Pack that dependency into the same
# local source even when a calling workflow still has an older inline public-package list.
if ! find "$package_source" -maxdepth 1 -name 'HsSqlAgent.Approvals.Abstractions.*.nupkg' ! -name '*.symbols.nupkg' -print -quit | grep -q .; then
  dotnet pack backend/src/Modules/HsSqlAgent.Approvals.Abstractions/HsSqlAgent.Approvals.Abstractions.csproj \
    --configuration Release \
    --output "$package_source"
fi

# The webhook adapter stays an independent package so modular Server consumers can opt into it.
if ! find "$package_source" -maxdepth 1 -name 'HsSqlAgent.Approvals.Webhook.*.nupkg' ! -name '*.symbols.nupkg' -print -quit | grep -q .; then
  dotnet pack backend/src/Modules/HsSqlAgent.Approvals.Webhook/HsSqlAgent.Approvals.Webhook.csproj \
    --configuration Release \
    --output "$package_source"
fi

# Standard hosting is the batteries-included NuGet composition used by ToolBox/Docker.
if ! find "$package_source" -maxdepth 1 -name 'HsSqlAgent.Hosting.*.nupkg' ! -name '*.symbols.nupkg' -print -quit | grep -q .; then
  dotnet pack backend/src/Modules/HsSqlAgent.Hosting/HsSqlAgent.Hosting.csproj \
    --configuration Release \
    --output "$package_source"
fi

server_package="$(find "$package_source" -maxdepth 1 -name 'HsSqlAgent.Server.*.nupkg' ! -name '*.symbols.nupkg' -print -quit)"
if [[ -z "$server_package" ]]; then
  echo "HsSqlAgent.Server package was not found in $package_source" >&2
  exit 1
fi

version="$(basename "$server_package")"
version="${version#HsSqlAgent.Server.}"
version="${version%.nupkg}"

major_version="${version%%.*}"
if ! [[ "$major_version" =~ ^[0-9]+$ ]] || (( major_version < 2 )); then
  echo "Breaking SqlCore F# rewrite must be packaged on major version 2 or later; got $version" >&2
  exit 1
fi

consumer_dir="$(mktemp -d)"
trap 'rm -rf "$consumer_dir"' EXIT

dotnet new web --framework net10.0 --no-restore --output "$consumer_dir"
cat > "$consumer_dir/NuGet.Config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local-packages" value="$package_source" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
EOF

# A batteries-included consumer installs only Hosting. Server and the official Webhook adapter
# must arrive transitively from the packed package graph.
dotnet add "$consumer_dir" package HsSqlAgent.Hosting --version "$version" --source "$package_source" --no-restore
cat > "$consumer_dir/Program.cs" <<'EOF'
using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using Common.Interfaces;
using Common.Services;
using HsSqlAgent.Approvals;
using HsSqlAgent.Approvals.Webhook;
using HsSqlAgent.Hosting;
using HsSqlAgent.Server.Extensions;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using SqlAgent.Service.Core.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

string[] expectedAssemblies =
[
    "HsSqlAgent.Hosting",
    "HsSqlAgent.Server",
    "HsSqlAgent.Approvals.Abstractions",
    "HsSqlAgent.Approvals.Webhook",
    "HsSqlAgent.SqlCore",
    "FSharp.Core",
    "HsSqlAgent.Provider.Abstractions",
    "HsSqlAgent.Provider.PostgreSql",
    "HsSqlAgent.Provider.MySql",
    "HsSqlAgent.Provider.Sqlite",
    "HsSqlAgent.Provider.SqlServer",
    "HsSqlAgent.Provider.Oracle",
    "HsSqlAgent.Provider.Firebird"
];

foreach (string assemblyName in expectedAssemblies)
{
    Assembly assembly = Assembly.Load(assemblyName);
    _ = assembly.GetExportedTypes();
    Console.WriteLine($"Loaded {assembly.GetName().Name} {assembly.GetName().Version}");
}

var services = new ServiceCollection();
var approvalRegistration = services.AddHsSqlAgentCore();
approvalRegistration.AddHsSqlAgentRuntime(options =>
    ConfigureSmokeSecretProtection(options.SecretProtection));
approvalRegistration.AddHsSqlAgentDmlApproval<SmokeApprovalProvider>();

_ = typeof(IDmlApprovalCompletionSink);
var durableCompletion = DmlApprovalCompletion.Approve(
    "dml_smoke",
    new string('A', 64),
    "smoke-reviewer",
    "EXT-SMOKE");
if (durableCompletion.Decision != DmlApprovalDecision.Approved)
    throw new InvalidOperationException("Packed approval contracts did not preserve the completion decision.");

var webhookBody = Encoding.UTF8.GetBytes("{\"requestId\":\"dml_smoke\"}");
var webhookSignature = WebhookApprovalSignature.Compute(
    "smoke-webhook-secret-that-is-at-least-32-bytes",
    WebhookApprovalEvents.ApprovalCompleted,
    1234567890,
    webhookBody);
if (!WebhookApprovalSignature.Verify(
        "smoke-webhook-secret-that-is-at-least-32-bytes",
        WebhookApprovalEvents.ApprovalCompleted,
        1234567890,
        webhookBody,
        webhookSignature))
    throw new InvalidOperationException("Packed webhook adapter signature contract failed.");

var standardBuilder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    EnvironmentName = Environments.Development
});
// Standard hosting binds key rings from configuration, so the packed consumer
// must provide test-only keys without weakening production fail-closed checks.
var standardSecrets = new SecretProtectionSettings();
ConfigureSmokeSecretProtection(standardSecrets);
standardBuilder.Configuration["SecretProtection:DatabasePasswords:ActiveKeyId"] = standardSecrets.DatabasePasswords.ActiveKeyId;
standardBuilder.Configuration["SecretProtection:DatabasePasswords:Keys:smoke-db-1"] = standardSecrets.DatabasePasswords.Keys["smoke-db-1"];
standardBuilder.Configuration["SecretProtection:DmlPayloads:ActiveKeyId"] = standardSecrets.DmlPayloads.ActiveKeyId;
standardBuilder.Configuration["SecretProtection:DmlPayloads:Keys:smoke-dml-1"] = standardSecrets.DmlPayloads.Keys["smoke-dml-1"];
standardBuilder.Configuration["SecretProtection:DmlFingerprintKey"] = standardSecrets.DmlFingerprintKey;
standardBuilder.Configuration["AdminDatabase:Provider"] = "Sqlite";
standardBuilder.Configuration["AdminDatabase:ConnectionString"] = "Data Source=hosting-smoke.db";
standardBuilder.Configuration["JwtSettings:SecretKey"] = new string('J', 64);
standardBuilder.Configuration["McpKeySettings:HmacSecretKey"] = new string('H', 64);
standardBuilder.Configuration["Mcp:PublicEndpoint"] = "http://localhost:8080/mcp";
standardBuilder.Configuration["DmlApproval:Provider"] = "Webhook";
standardBuilder.Configuration["DmlApproval:Webhook:Endpoint"] = "https://approval.example.test/requests";
standardBuilder.Configuration["DmlApproval:Webhook:CallbackUrl"] =
    "https://sql-agent.example.test/api/hs-sql-agent/approvals/webhook";
standardBuilder.Configuration["DmlApproval:Webhook:SigningSecret"] =
    "smoke-webhook-secret-that-is-at-least-32-bytes";

standardBuilder.AddHsSqlAgentStandardHost();
await using var standardApp = standardBuilder.Build();
standardApp.UseHsSqlAgentStandardHost();

if (standardApp.Services.GetService<IDmlApprovalProvider>() is not WebhookDmlApprovalProvider)
    throw new InvalidOperationException("Packed Hosting package did not select the Webhook approval provider.");

var validation = new SqlPlanValidationContext(
    "nuget-consumer-smoke-v2",
    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "users" });

var command = SqlCoreFacade.CompileQuery(
    "SELECT id FROM users WHERE id = 1",
    SqlAgentToolType.Postgres,
    SqlAgentToolType.Postgres,
    validation,
    new SqlExecutionPlanPolicy(10));

if (!command.Sql.Contains("SELECT", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Packed SqlCore facade did not produce SQL.");

if (!command.Parameters.Any(parameter =>
        parameter.Value is IConvertible
        && Convert.ToInt64(parameter.Value) == 1L))
    throw new InvalidOperationException("Packed SqlCore facade did not preserve the predicate literal as a parameter.");

if (command.Sql.Contains("= 1", StringComparison.Ordinal))
    throw new InvalidOperationException("Packed SqlCore facade inlined a predicate literal that must remain parameterized.");

Console.WriteLine($"Compiled public SqlCore query via packed Hosting dependency: {command.Sql}");

// An external ASP.NET Core project must be able to use the modular v3 package
// without inheriting the standalone host's identity, MVC or exception policy.
var modularBuilder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    EnvironmentName = Environments.Development
});
// Bind only a loopback ephemeral port; this must never depend on the
// machine's pre-existing listeners or hard-coded CI port availability.
modularBuilder.WebHost.UseUrls("http://127.0.0.1:0");
modularBuilder.Services.AddAuthentication(authentication =>
{
    authentication.DefaultScheme = "Host.Primary";
    authentication.DefaultAuthenticateScheme = "Host.Primary";
    authentication.DefaultChallengeScheme = "Host.Challenge";
})
.AddScheme<AuthenticationSchemeOptions, SmokeHostAuthenticationHandler>("Host.Primary", _ => { })
.AddScheme<AuthenticationSchemeOptions, SmokeHostAuthenticationHandler>("Host.Challenge", _ => { });
var hostPolicy = new AuthorizationPolicyBuilder()
    .RequireAuthenticatedUser()
    .RequireClaim("host-admin", "true")
    .Build();
modularBuilder.Services.AddAuthorization(options =>
{
    options.DefaultPolicy = hostPolicy;
    options.AddPolicy("Host.SqlAgentAdmin", hostPolicy);
});
modularBuilder.Services.AddControllers().AddJsonOptions(options =>
{
    // Deliberately unlike HsSqlAgent's own wire serialization.
    options.JsonSerializerOptions.PropertyNamingPolicy = null;
    options.JsonSerializerOptions.DictionaryKeyPolicy = null;
    options.JsonSerializerOptions.PropertyNameCaseInsensitive = false;
});

var hs = modularBuilder.Services.AddHsSqlAgentCore();
hs.AddHsSqlAgentRuntime(options =>
    ConfigureSmokeSecretProtection(options.SecretProtection));
hs.AddHsSqlAgentAdminStore(options =>
{
    options.Provider = "Sqlite";
    options.ConnectionString = "Data Source=modular-host-consumer-smoke.db";
});
hs.AddHsSqlAgentHostAuthorization("Host.SqlAgentAdmin");
hs.AddHsSqlAgentAdminApi();

// A separately installed integration package may begin its own builder;
// capabilities remain one registration per IServiceCollection.
var descriptorsBeforeSecondModule = modularBuilder.Services.Count;
modularBuilder.Services.AddHsSqlAgentCore().AddHsSqlAgentRuntime()
    .AddHsSqlAgentAdminApi();
if (modularBuilder.Services.Count != descriptorsBeforeSecondModule)
    throw new InvalidOperationException(
        "Second modular NuGet integration duplicated HsSqlAgent registrations.");

await using var modularApp = modularBuilder.Build();
modularApp.UseAuthentication();
modularApp.UseAuthorization();
modularApp.UseHsSqlAgentAdminApi();
modularApp.MapControllers();
modularApp.MapGet("/host/probe", () => "host-ok").RequireAuthorization();

// The packed consumer must use authenticated ciphertext with trusted record binding.
var crypto = modularApp.Services.GetRequiredService<ICryptoService>();
var protectedValue = crypto.EncryptText("nuget-credential", "db-password:smoke-binding");
if (protectedValue is null || !protectedValue.StartsWith("v2:smoke-db-1:", StringComparison.Ordinal)
    || crypto.DecryptText(protectedValue, "db-password:smoke-binding") != "nuget-credential")
    throw new InvalidOperationException("Packed consumer secret protection round-trip failed.");
try
{
    _ = crypto.DecryptText(protectedValue, "db-password:wrong-binding");
    throw new InvalidOperationException("Packed secret protection accepted a mismatched record binding.");
}
catch (CryptographicException)
{
    // Correct: AAD rejects a different record identifier.
}

var authentication = modularApp.Services
    .GetRequiredService<IOptions<AuthenticationOptions>>().Value;
if (authentication.DefaultScheme != "Host.Primary"
    || authentication.DefaultAuthenticateScheme != "Host.Primary"
    || authentication.DefaultChallengeScheme != "Host.Challenge")
    throw new InvalidOperationException("Modular NuGet mount replaced the host authentication defaults.");

var authorization = modularApp.Services
    .GetRequiredService<IOptions<AuthorizationOptions>>().Value;
if (!ReferenceEquals(hostPolicy, authorization.DefaultPolicy))
    throw new InvalidOperationException("Modular NuGet mount replaced the host authorization policy.");

var jsonOptions = modularApp.Services.GetRequiredService<IOptions<JsonOptions>>()
    .Value.JsonSerializerOptions;
if (jsonOptions.PropertyNamingPolicy is not null
    || jsonOptions.DictionaryKeyPolicy is not null
    || jsonOptions.PropertyNameCaseInsensitive)
    throw new InvalidOperationException("Modular NuGet mount overwrote the host MVC JSON defaults.");

var apiDescriptors = modularApp.Services
    .GetRequiredService<IActionDescriptorCollectionProvider>()
    .ActionDescriptors.Items.OfType<ControllerActionDescriptor>();
if (!apiDescriptors.Any(descriptor =>
        descriptor.ControllerTypeInfo.AsType().FullName == "HsSqlAgent.Server.Controllers.DbManagementController"))
    throw new InvalidOperationException("Modular NuGet mount did not publish the HsSqlAgent Admin API.");

var hostRoutes = ((IEndpointRouteBuilder)modularApp).DataSources
    .SelectMany(source => source.Endpoints)
    .OfType<RouteEndpoint>();
if (!hostRoutes.Any(endpoint => endpoint.RoutePattern.RawText == "/host/probe"))
    throw new InvalidOperationException("Modular NuGet mount lost the host application's own endpoint.");

// A descriptor-only smoke misses auth and route-mount failures. Execute real
// HTTP requests against the packed NuGet consumer with its host-owned schemes.
await modularApp.StartAsync();
try
{
    var server = modularApp.Services.GetRequiredService<IServer>();
    var addresses = server.Features.Get<IServerAddressesFeature>()
        ?? throw new InvalidOperationException("Test host did not expose its bound listener address.");
    var address = addresses.Addresses.Single(value =>
        value.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)
        && !value.EndsWith(":0", StringComparison.Ordinal));
    using var client = new HttpClient { BaseAddress = new Uri(address) };

    using var unauthenticatedHost = await client.GetAsync("/host/probe");
    if (unauthenticatedHost.StatusCode != HttpStatusCode.Unauthorized)
        throw new InvalidOperationException(
            $"Host policy was bypassed: unauthenticated host endpoint returned {(int)unauthenticatedHost.StatusCode}.");

    using var unauthenticatedAdmin = await client.GetAsync("/api/runtime/dml-approvals/reconciliation");
    if (unauthenticatedAdmin.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
        throw new InvalidOperationException(
            $"Modular Admin authorization was bypassed or misconfigured: status {(int)unauthenticatedAdmin.StatusCode}.");

    client.DefaultRequestHeaders.Add("X-Smoke-Host-User", "allow");
    using var authorizedHost = await client.GetAsync("/host/probe");
    if (authorizedHost.StatusCode != HttpStatusCode.OK
        || await authorizedHost.Content.ReadAsStringAsync() != "host-ok")
        throw new InvalidOperationException("Host-authenticated endpoint is no longer accessible after modular mount.");

    using var authorizedAdmin = await client.GetAsync("/api/runtime/dml-approvals/reconciliation?limit=2");
    if (authorizedAdmin.StatusCode != HttpStatusCode.OK)
        throw new InvalidOperationException(
            $"Host-authorized modular Admin API returned {(int)authorizedAdmin.StatusCode}.");
    using var payload = JsonDocument.Parse(await authorizedAdmin.Content.ReadAsStringAsync());
    if (payload.RootElement.ValueKind != JsonValueKind.Array)
        throw new InvalidOperationException("The mounted Admin API returned an unexpected JSON contract.");

    Console.WriteLine("Packed external ASP.NET Core host HTTP/auth/Admin API integration smoke passed.");
}
finally
{
    await modularApp.StopAsync();
}

var customStore = new SmokeCustomChallengeStore();
_ = customStore.TryPromoteAsync(Array.Empty<DmlApprovalChallenge>(),
    new DmlApprovalChallenge("plan", null, 0, "policy", "context",
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), "aggregate"));
Console.WriteLine("Packed modular HsSqlAgent.Server host and v3 custom approval store smoke passed.");

// Test-only ephemeral keys, independent across purposes and smoke registrations.
static void ConfigureSmokeSecretProtection(SecretProtectionSettings settings)
{
    settings.DatabasePasswords.ActiveKeyId = "smoke-db-1";
    settings.DatabasePasswords.Keys["smoke-db-1"] =
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    settings.DmlPayloads.ActiveKeyId = "smoke-dml-1";
    settings.DmlPayloads.Keys["smoke-dml-1"] =
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    settings.DmlFingerprintKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}

sealed class SmokeHostAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Smoke-Host-User", out var marker)
            || !string.Equals(marker.ToString(), "allow", StringComparison.Ordinal))
            return Task.FromResult(AuthenticateResult.NoResult());

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "smoke-host-user"),
            new Claim("host-admin", "true")
        ], Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

sealed class SmokeCustomChallengeStore : IDmlApprovalChallengeStore
{
    public ValueTask RegisterAsync(
        DmlApprovalChallenge challenge, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    public ValueTask<bool> TryConsumeAsync(
        DmlApprovalChallenge challenge, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(false);

    public ValueTask<bool> TryPromoteAsync(
        IReadOnlyList<DmlApprovalChallenge> children,
        DmlApprovalChallenge aggregate,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(false);
}

sealed class SmokeApprovalProvider : IDmlApprovalProvider
{
    public ValueTask<DmlApprovalResult> RequestApprovalAsync(
        DmlApprovalRequest request,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(DmlApprovalResult.Reject(request));
}
EOF

dotnet restore "$consumer_dir" --configfile "$consumer_dir/NuGet.Config" --no-cache
dotnet build "$consumer_dir" --configuration Release --no-restore
dotnet run --project "$consumer_dir" --configuration Release --no-build

# From 3.0.1 onward (including prereleases), the already-published 3.0.0
# NuGet packages are the non-breaking baseline for every packed public API.
bash .github/scripts/verify-v3-package-abi.sh "$package_source" "$version"
