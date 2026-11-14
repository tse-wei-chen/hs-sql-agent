using System.Data.Common;
using System.Net.Http.Json;
using Admin.Service.Models;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FirebirdSql.Data.FirebirdClient;
using HsSqlAgent.Provider.Abstractions;
using HsSqlAgent.Provider.Firebird;
using HsSqlAgent.Provider.MySql;
using HsSqlAgent.Provider.Oracle;
using HsSqlAgent.Provider.SqlServer;
using HsSqlAgent.Server.Services;
using Microsoft.Data.SqlClient;
using MySql.Data.MySqlClient;
using Oracle.ManagedDataAccess.Client;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

/// <summary>
/// A real Toxiproxy Testcontainer cuts the production Provider's connection, while
/// an independent witness bypasses the proxy to the CI job's actual Docker database.
/// Each provider job owns its database; none starts a second heavyweight DB image.
/// Missing connection configuration is a hard failure, not a test skip.
/// </summary>
public sealed class ProviderTcpPartitionFixture : IAsyncLifetime
{
    private const int ProxyPort = 8666;
    private readonly IContainer _proxy = new ContainerBuilder("ghcr.io/shopify/toxiproxy:2.12.0")
        .WithExtraHost("host.docker.internal", "host-gateway")
        .WithPortBinding(8474, true)
        .WithPortBinding(ProxyPort, true)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(request => request.ForPort(8474).ForPath("/version")))
        .Build();

    public string Host => _proxy.Hostname;
    public int MappedPort => _proxy.GetMappedPublicPort(ProxyPort);

    public ValueTask InitializeAsync() => new(_proxy.StartAsync(TestContext.Current.CancellationToken));
    public ValueTask DisposeAsync() => _proxy.DisposeAsync();

    public async Task<string> CreateRouteAsync(int serverPort, CancellationToken ct)
    {
        var name = "provider_" + Guid.NewGuid().ToString("N");
        using var http = Admin();
        using var response = await http.PostAsJsonAsync("proxies", new
        {
            name,
            listen = "0.0.0.0:" + ProxyPort,
            upstream = "host.docker.internal:" + serverPort,
            enabled = true
        }, ct);
        response.EnsureSuccessStatusCode();
        return name;
    }

    public async Task SetEnabledAsync(string name, bool enabled, CancellationToken ct)
    {
        using var http = Admin();
        using var response = await http.PostAsJsonAsync(
            "proxies/" + name, new { enabled }, ct);
        response.EnsureSuccessStatusCode();
    }

    private HttpClient Admin() => new()
    {
        BaseAddress = new Uri("http://" + Host + ":" + _proxy.GetMappedPublicPort(8474) + "/"),
        Timeout = TimeSpan.FromSeconds(10)
    };
}

public sealed class ProviderTcpPartitionTestcontainersTests(ProviderTcpPartitionFixture fixture)
    : IClassFixture<ProviderTcpPartitionFixture>
{
    [Fact]
    public Task MySql_HealthyDbBehindCut_FailsClosedAndRecovers() =>
        ProveAsync("TEST_MYSQL_CONNECTION", 3306, new MySqlProvider(),
            SqlAgentToolType.MySQL, "SELECT 1",
            (source, host, port) =>
            {
                var builder = new MySqlConnectionStringBuilder(source)
                {
                    Server = host,
                    Port = (uint)port,
                    Pooling = false,
                    ConnectionTimeout = 5
                };
                return builder.ConnectionString;
            });

    [Fact]
    public Task SqlServer_HealthyDbBehindCut_FailsClosedAndRecovers() =>
        ProveAsync("TEST_SQLSERVER_CONNECTION", 1433, new MsSqlServerProvider(),
            SqlAgentToolType.MsSqlServer, "SELECT 1",
            (source, host, port) =>
            {
                var builder = new SqlConnectionStringBuilder(source)
                {
                    DataSource = host + "," + port,
                    Pooling = false,
                    ConnectTimeout = 5
                };
                return builder.ConnectionString;
            },
            // This test isolates transport, not least-privilege authentication.
            // A privileged CI sa account without a whitelist is intentionally
            // rejected by the SQL Server Query runtime's separate identity gate.
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dbo.transport_probe" });

    [Fact]
    public Task Oracle_HealthyDbBehindCut_FailsClosedAndRecovers() =>
        ProveAsync("TEST_ORACLE_CONNECTION", 1521, new OracleProvider(),
            SqlAgentToolType.Oracle, "SELECT 1 FROM DUAL",
            (source, host, port) =>
            {
                var builder = new OracleConnectionStringBuilder(source)
                {
                    DataSource = host + ":" + port + "/FREEPDB1",
                    Pooling = false
                };
                return builder.ConnectionString;
            });

    [Fact]
    public Task Firebird_HealthyDbBehindCut_FailsClosedAndRecovers() =>
        ProveAsync("TEST_FIREBIRD_CONNECTION", 3050, new FirebirdProvider(),
            SqlAgentToolType.Firebird, "SELECT 1 FROM RDB$DATABASE",
            (source, host, port) =>
            {
                var builder = new FbConnectionStringBuilder(source)
                {
                    DataSource = host,
                    Port = port,
                    Pooling = false
                };
                return builder.ConnectionString;
            });

    private async Task ProveAsync(
        string connectionVariable, int dbPort, ISqlProvider provider,
        SqlAgentToolType dialect, string safeSql,
        Func<string, string, int, string> proxied,
        IReadOnlySet<string>? allowedTables = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var direct = Environment.GetEnvironmentVariable(connectionVariable);
        Assert.False(string.IsNullOrWhiteSpace(direct),
            connectionVariable + " must point to a real service container (no skip).");
        var route = await fixture.CreateRouteAsync(dbPort, ct);
        var routed = proxied(direct!, fixture.Host, fixture.MappedPort);
        var runtime = new TypedQueryRuntime();
        var policy = new SecurityPolicyModel { QueryMaxRows = 5, QueryTimeoutSeconds = 10 };

        // The real native Query must be functional through the proxy before
        // the fault; otherwise a compile / credentials failure could fake success.
        await ExecuteTypedAsync(routed);
        await using var witness = provider.Connections.Create(direct!);
        await witness.OpenAsync(ct);
        Assert.Equal(1, Convert.ToInt32(await ScalarAsync(witness, safeSql, ct)));

        try
        {
            await fixture.SetEnabledAsync(route, false, ct);
            // The database itself is healthy. Only the published TCP proxy path
            // used by the production Query is severed.
            Assert.Equal(1, Convert.ToInt32(await ScalarAsync(witness, safeSql, ct)));
            var failure = await Record.ExceptionAsync(() => ExecuteTypedAsync(routed));
            Assert.NotNull(failure);
            Assert.IsNotType<TimeoutException>(failure);
            Assert.False(failure is OperationCanceledException && ct.IsCancellationRequested,
                "Test cancellation is not proof of a provider network fault.");
        }
        finally
        {
            using var restore = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await fixture.SetEnabledAsync(route, true, restore.Token);
        }

        // A genuine successful Query after restoration prevents an always-failing
        // stub, exhausted credentials, or a permanently broken fixture passing.
        await ExecuteTypedAsync(routed);

        async Task ExecuteTypedAsync(string connectionString) =>
            _ = await runtime.ExecuteAsync(provider, connectionString, safeSql,
                dialect, policy, allowedTables, ct)
                .WaitAsync(TimeSpan.FromSeconds(18), ct);
    }

    private static async Task<object?> ScalarAsync(
        DbConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 10;
        return await cmd.ExecuteScalarAsync(ct);
    }
}
