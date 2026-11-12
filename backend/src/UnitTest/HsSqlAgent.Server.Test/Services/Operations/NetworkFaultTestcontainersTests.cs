using System.Diagnostics;
using System.Net.Http.Json;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using HsSqlAgent.Provider.PostgreSql;
using HsSqlAgent.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using SqlAgent.Service.Core.Execution;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

/// <summary>
/// A real TCP proxy in front of two independent Docker-backed databases.
/// The test application reaches PostgreSQL / Redis exclusively through
/// Toxiproxy, while physical witnesses use their direct Docker host ports.
/// The proxy reaches mapped host ports through Docker host-gateway; no custom
/// Docker network or Testcontainers network lifecycle is required.
/// </summary>
public sealed class NetworkFaultFixture : IAsyncLifetime
{
    private const int PostgresProxyPort = 8666;
    private const int RedisProxyPort = 8667;

    private readonly PostgreSqlContainer _postgres;
    private readonly IContainer _redis;
    private readonly IContainer _proxy;

    public NetworkFaultFixture()
    {
        Mark("fixture construction begins");
        _postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("hsql_network")
            .WithUsername("fault_operator")
            .WithPassword("Fault_2026_Test!")
            .Build();
        _redis = new ContainerBuilder("redis:7-alpine")
            .WithPortBinding(6379, true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilInternalTcpPortIsAvailable(6379))
            .Build();
        _proxy = new ContainerBuilder("ghcr.io/shopify/toxiproxy:2.12.0")
            .WithExtraHost("host.docker.internal", "host-gateway")
            .WithPortBinding(8474, true)
            .WithPortBinding(PostgresProxyPort, true)
            .WithPortBinding(RedisProxyPort, true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilInternalTcpPortIsAvailable(8474))
            .Build();
        Mark("fixture construction complete");
    }

    internal static void Mark(string stage) =>
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "hsql-network-fault-trace.log"),
            DateTimeOffset.UtcNow.ToString("O") + " " + stage + Environment.NewLine);

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        Mark("PostgreSQL container start");
        await _postgres.StartAsync(ct);
        Mark("PostgreSQL container ready");
        await _redis.StartAsync(ct);
        Mark("Redis container ready");
        await _proxy.StartAsync(ct);
        Mark("Toxiproxy container ready");
        using var admin = AdminClient();
        await CreateProxyAsync(admin, "postgres", PostgresProxyPort,
            "host.docker.internal:" + _postgres.GetMappedPublicPort(5432), ct);
        await CreateProxyAsync(admin, "redis", RedisProxyPort,
            "host.docker.internal:" + _redis.GetMappedPublicPort(6379), ct);
        Mark("both TCP routes ready");
    }

    public async ValueTask DisposeAsync()
    {
        Mark("fixture disposal starts");
        await _proxy.DisposeAsync();
        Mark("Toxiproxy disposed");
        await _redis.DisposeAsync();
        Mark("Redis disposed");
        await _postgres.DisposeAsync();
        Mark("PostgreSQL disposed");
    }

    public string DirectPostgres(string application) =>
        new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString())
        {
            ApplicationName = application,
            Pooling = false,
            CommandTimeout = 90,
            Timeout = 10
        }.ConnectionString;

    public string ProxiedPostgres(string application) =>
        new NpgsqlConnectionStringBuilder(DirectPostgres(application))
        {
            Host = _proxy.Hostname,
            Port = _proxy.GetMappedPublicPort(PostgresProxyPort)
        }.ConnectionString;

    public string DirectRedis() =>
        _redis.Hostname + ":" + _redis.GetMappedPublicPort(6379)
        + ",abortConnect=false,connectTimeout=1000,asyncTimeout=1000,syncTimeout=1000";

    public string ProxiedRedis() =>
        _proxy.Hostname + ":" + _proxy.GetMappedPublicPort(RedisProxyPort)
        + ",abortConnect=false,connectTimeout=1000,asyncTimeout=1000,syncTimeout=1000";

    public async Task SetProxyAsync(string name, bool enabled, CancellationToken ct)
    {
        using var admin = AdminClient();
        using var response = await admin.PostAsJsonAsync(
            "proxies/" + name, new { enabled }, ct);
        response.EnsureSuccessStatusCode();
    }

    private HttpClient AdminClient() => new()
    {
        BaseAddress = new Uri("http://" + _proxy.Hostname + ":"
            + _proxy.GetMappedPublicPort(8474) + "/"),
        Timeout = TimeSpan.FromSeconds(10)
    };

    private static async Task CreateProxyAsync(
        HttpClient admin, string name, int listen, string upstream, CancellationToken ct)
    {
        using var response = await admin.PostAsJsonAsync("proxies", new
        {
            name,
            listen = "0.0.0.0:" + listen,
            upstream,
            enabled = true
        }, ct);
        response.EnsureSuccessStatusCode();
    }
}

/// <summary>
/// #575 live TCP-partition tests, not synthetic exceptions and not a
/// simulated lost acknowledgement injected after the native driver returns.
/// Physical witnesses bypass the proxy and independently observe the DB.
/// This does NOT prove a COMMIT response was lost after durable success.
/// </summary>
public sealed class NetworkFaultTestcontainersTests(NetworkFaultFixture fixture)
    : IClassFixture<NetworkFaultFixture>
{
    [Fact]
    public async Task Postgres_QueryNativeSocketCut_FailsAndRecoversWithoutChangingServer()
    {
        var ct = TestContext.Current.CancellationToken;
        NetworkFaultFixture.Mark("Query physical case starts");
        var application = "hsql_proxy_query_" + Guid.NewGuid().ToString("N")[..8];
        await using var witness = new NpgsqlConnection(fixture.DirectPostgres("proxy_query_witness"));
        await witness.OpenAsync(ct);
        var runtime = new TypedQueryRuntime();
        var policy = new SecurityPolicyModel { QueryMaxRows = 10, QueryTimeoutSeconds = 90 };

        var running = runtime.ExecuteAsync(new PostgresProvider(),
            fixture.ProxiedPostgres(application), "SELECT pg_catalog.pg_sleep(60)",
            SqlAgentToolType.Postgres, policy, allowedTables: null, ct);
        await WaitForNativeBackendAsync(witness, application, "pg_sleep", false, ct);
        NetworkFaultFixture.Mark("Query native backend observed");
        try
        {
            // Disable the real TCP proxy, rather than killing PostgreSQL or
            // mocking a DbCommand. An independent witness remains healthy.
            await fixture.SetProxyAsync("postgres", false, ct);
            NetworkFaultFixture.Mark("PostgreSQL TCP route disabled");
            Assert.Equal(1, Convert.ToInt32(await ScalarAsync(witness, "SELECT 1", ct)));
            var failure = await Record.ExceptionAsync(async () =>
                _ = await running.WaitAsync(TimeSpan.FromSeconds(20), ct));
            Assert.NotNull(failure);
            Assert.IsNotType<TimeoutException>(failure);
            NetworkFaultFixture.Mark("Query transport failure asserted");
        }
        finally
        {
            await fixture.SetProxyAsync("postgres", true, CancellationToken.None);
            NetworkFaultFixture.Mark("PostgreSQL TCP route restored");
        }

        // The same production Query runtime must work after transport restoration.
        _ = await runtime.ExecuteAsync(new PostgresProvider(),
            fixture.ProxiedPostgres("hsql_proxy_recovered_" + Guid.NewGuid().ToString("N")[..8]),
            "SELECT 1", SqlAgentToolType.Postgres, policy, allowedTables: null, ct);
    }

    [Fact]
    public async Task Redis_TcpPartitionRevokesExistingLease_AndFailsClosedAcrossInstances()
    {
        var ct = TestContext.Current.CancellationToken;
        NetworkFaultFixture.Mark("Redis physical case starts");
        var key = "hsql:redis-network-fault:" + Guid.NewGuid().ToString("N");
        await using var physicalWitness = await ConnectionMultiplexer.ConnectAsync(
            fixture.DirectRedis());
        var physical = physicalWitness.GetDatabase();
        var state = new Mock<ISecurityPolicyRuntimeState>();
        state.Setup(x => x.GetCurrent()).Returns(
            new SecurityPolicyModel { MaxConcurrentSql = 1 });
        var options = new RedisSqlConcurrencyOptions(
            fixture.ProxiedRedis(), key, TimeSpan.FromSeconds(3),
            RateLimiterFailureMode.FailClosed);
        await using var first = new RedisSqlExecutionConcurrencyLimiter(
            state.Object, options, NullLogger<RedisSqlExecutionConcurrencyLimiter>.Instance);
        await using var second = new RedisSqlExecutionConcurrencyLimiter(
            state.Object, options, NullLogger<RedisSqlExecutionConcurrencyLimiter>.Instance);

        var lease = await first.TryAcquireAsync(ct);
        Assert.NotNull(lease);
        await using (lease)
        {
            var revoked = Assert.IsAssignableFrom<IRevocableSqlExecutionLease>(lease);
            Assert.Equal(1L, await physical.SortedSetLengthAsync(key));
            Assert.Null(await second.TryAcquireAsync(ct));

            try
            {
                await fixture.SetProxyAsync("redis", false, ct);
                NetworkFaultFixture.Mark("Redis TCP route disabled");
                // Redis stays up; only the transport paths of both limiter
                // processes are severed. The DB witness can still read key.
                Assert.Equal(1L, await physical.SortedSetLengthAsync(key));
                await Task.Delay(TimeSpan.FromMilliseconds(3500), ct);
                Assert.True(revoked.RevocationToken.IsCancellationRequested);
                var refused = await second.TryAcquireAsync(ct)
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(12), ct);
                Assert.Null(refused);
            }
            finally
            {
                await fixture.SetProxyAsync("redis", true, CancellationToken.None);
                NetworkFaultFixture.Mark("Redis TCP route restored");
            }
            Assert.True(revoked.RevocationToken.IsCancellationRequested,
                "A network partition cannot un-revoke an admitted SQL operation.");
        }

        // Let stale Redis expiry/release settle before a new independent
        // process acquires. Reconnect cannot resurrect the old lease token.
        await using var resumed = new RedisSqlExecutionConcurrencyLimiter(
            state.Object, options, NullLogger<RedisSqlExecutionConcurrencyLimiter>.Instance);
        await using var next = await resumed.TryAcquireAsync(ct);
        Assert.NotNull(next);
    }

    private static async Task<int> WaitForNativeBackendAsync(
        NpgsqlConnection witness, string app, string queryFragment,
        bool commit, CancellationToken ct)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(20))
        {
            await using var command = new NpgsqlCommand("""
                SELECT pid FROM pg_catalog.pg_stat_activity
                WHERE application_name = @app
                  AND state = 'active'
                  AND pid <> pg_catalog.pg_backend_pid()
                  AND (CASE WHEN @commit THEN query ~* '^COMMIT'
                       ELSE query ILIKE '%' || @fragment || '%' END)
                  AND (NOT @commit OR wait_event_type = 'Lock')
                LIMIT 1
                """, witness);
            command.Parameters.AddWithValue("app", app);
            command.Parameters.AddWithValue("commit", commit);
            command.Parameters.AddWithValue("fragment", queryFragment);
            var value = await command.ExecuteScalarAsync(ct);
            if (value is int pid)
                return pid;
            await Task.Delay(100, ct);
        }
        throw new TimeoutException(
            "No native PostgreSQL " + (commit ? "COMMIT lock wait" : "running SQL")
            + " reached the physical witness; network fault was not proven.");
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<object?> ScalarAsync(
        NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(ct);
    }
}
