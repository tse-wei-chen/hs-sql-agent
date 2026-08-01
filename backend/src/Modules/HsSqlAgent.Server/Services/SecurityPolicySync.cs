using System.Text.Json;
using Admin.Service.Data;
using Admin.Service.Data.Entites;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace HsSqlAgent.Server.Services;

public sealed class NoOpSecurityPolicyChangePublisher : ISecurityPolicyChangePublisher
{
    public Task PublishAsync(
        SecurityPolicyModel policy,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed record RedisSecurityPolicySyncOptions(
    string ConnectionString,
    string CacheKey,
    string Channel);

public sealed class RedisSecurityPolicyChangeBus :
    ISecurityPolicyChangePublisher,
    IHostedService,
    IAsyncDisposable
{
    private readonly RedisSecurityPolicySyncOptions _options;
    private readonly ISecurityPolicyRuntimeState _runtimeState;
    private readonly ILogger<RedisSecurityPolicyChangeBus> _logger;
    private readonly Lazy<Task<ConnectionMultiplexer>> _connection;
    private RedisChannel _channel;

    public RedisSecurityPolicyChangeBus(
        RedisSecurityPolicySyncOptions options,
        ISecurityPolicyRuntimeState runtimeState,
        ILogger<RedisSecurityPolicyChangeBus> logger)
    {
        _options = options;
        _runtimeState = runtimeState;
        _logger = logger;
        _channel = RedisChannel.Literal(options.Channel);
        _connection = new Lazy<Task<ConnectionMultiplexer>>(ConnectAsync);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var connection = await _connection.Value.WaitAsync(cancellationToken);
            await connection.GetSubscriber().SubscribeAsync(_channel, HandlePolicyChanged);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(exception, "Unable to subscribe to security policy changes.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_connection.IsValueCreated || !_connection.Value.IsCompletedSuccessfully)
            return;

        var connection = await _connection.Value;
        await connection.GetSubscriber().UnsubscribeAsync(_channel);
    }

    public async Task PublishAsync(
        SecurityPolicyModel policy,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = JsonSerializer.Serialize(policy);
            var connection = await _connection.Value.WaitAsync(cancellationToken);
            var database = connection.GetDatabase();
            var subscriber = connection.GetSubscriber();
            await Task.WhenAll(
                database.StringSetAsync(_options.CacheKey, payload),
                subscriber.PublishAsync(_channel, payload));
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The database remains authoritative. The polling synchronizer repairs a
            // missed notification, so a committed admin update must not be reported as failed.
            _logger.LogError(exception, "Unable to publish the security policy change.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection.IsValueCreated && _connection.Value.IsCompletedSuccessfully)
        {
            var connection = await _connection.Value;
            await connection.DisposeAsync();
        }
    }

    private Task<ConnectionMultiplexer> ConnectAsync()
    {
        var configuration = ConfigurationOptions.Parse(_options.ConnectionString);
        configuration.AbortOnConnectFail = false;
        return ConnectionMultiplexer.ConnectAsync(configuration);
    }

    private void HandlePolicyChanged(RedisChannel channel, RedisValue value)
    {
        try
        {
            var policy = JsonSerializer.Deserialize<SecurityPolicyModel>(value.ToString());
            if (policy is null)
                return;

            var current = _runtimeState.GetCurrent();
            if (current.UpdatedAt is null || policy.UpdatedAt > current.UpdatedAt)
                _runtimeState.SetCurrent(policy);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unable to apply a security policy change message.");
        }
    }
}

public sealed class SecurityPolicyDatabaseSynchronizer(
    IAdminContext context,
    ISecurityPolicyRuntimeState runtimeState)
{
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var entity = await context.SecurityPolicySettings
            .AsNoTracking()
            .SingleAsync(x => x.Id == SecurityPolicySettings.SingletonId, cancellationToken);
        var latest = SecurityPolicyModel.FromEntity(entity);
        var current = runtimeState.GetCurrent();

        if (current.UpdatedAt is null || latest.UpdatedAt > current.UpdatedAt)
            runtimeState.SetCurrent(latest);
    }
}

public sealed record SecurityPolicyRefreshOptions(TimeSpan Interval);

public sealed class SecurityPolicyRefreshBackgroundService(
    IServiceScopeFactory scopeFactory,
    SecurityPolicyRefreshOptions options,
    ILogger<SecurityPolicyRefreshBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var synchronizer = scope.ServiceProvider
                    .GetRequiredService<SecurityPolicyDatabaseSynchronizer>();
                await synchronizer.RefreshAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Unable to refresh the security policy from the admin database.");
            }
        }
    }
}
