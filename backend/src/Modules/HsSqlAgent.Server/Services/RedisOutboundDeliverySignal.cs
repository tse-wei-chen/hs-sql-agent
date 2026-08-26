using System.Threading.Channels;
using Admin.Service.Interfaces;
using StackExchange.Redis;

namespace HsSqlAgent.Server.Services;

public sealed record RedisOutboundDeliverySignalOptions(
    string ConnectionString,
    string ChannelName);

public sealed class RedisOutboundDeliverySignal :
    IOutboundDeliverySignal,
    IHostedService,
    IAsyncDisposable
{
    private readonly RedisOutboundDeliverySignalOptions _options;
    private readonly ILogger<RedisOutboundDeliverySignal> _logger;
    private readonly Channel<byte> _channel = Channel.CreateUnbounded<byte>(new UnboundedChannelOptions
    {
        SingleReader = true
    });
    private readonly Lazy<Task<ConnectionMultiplexer>> _connection;
    private readonly RedisChannel _redisChannel;

    public RedisOutboundDeliverySignal(
        RedisOutboundDeliverySignalOptions options,
        ILogger<RedisOutboundDeliverySignal> logger)
    {
        _options = options;
        _logger = logger;
        _redisChannel = RedisChannel.Literal(options.ChannelName);
        _connection = new Lazy<Task<ConnectionMultiplexer>>(ConnectAsync);
    }

    public void Notify()
    {
        _channel.Writer.TryWrite(0);
        _ = Task.Run(async () =>
        {
            try
            {
                if (_connection.IsValueCreated)
                {
                    var conn = await _connection.Value;
                    await conn.GetSubscriber().PublishAsync(_redisChannel, "notify");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to publish OutboundDelivery notification over Redis.");
            }
        });
    }

    public ValueTask<bool> WaitAsync(CancellationToken cancellationToken) =>
        _channel.Reader.WaitToReadAsync(cancellationToken);

    public bool TryRead() => _channel.Reader.TryRead(out _);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var connection = await _connection.Value.WaitAsync(cancellationToken);
            await connection.GetSubscriber().SubscribeAsync(_redisChannel, (_, _) =>
            {
                _channel.Writer.TryWrite(0);
            });
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Unable to subscribe to Redis OutboundDelivery channel.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_connection.IsValueCreated && _connection.Value.IsCompletedSuccessfully)
        {
            var connection = await _connection.Value;
            await connection.GetSubscriber().UnsubscribeAsync(_redisChannel);
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
}
