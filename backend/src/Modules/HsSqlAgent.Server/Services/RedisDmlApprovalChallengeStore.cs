using System.Text.Json;
using StackExchange.Redis;

namespace HsSqlAgent.Server.Services;

public sealed record RedisDmlApprovalChallengeOptions(
    string ConnectionString,
    string KeyPrefix);

/// <summary>
/// Distributed one-time DML approval challenge store. Registration is NX + TTL and consumption is
/// one atomic GET+DEL Lua operation so two server instances cannot both commit the same approval.
/// Stored payload equality is checked after the atomic consume, matching the fail-closed in-memory
/// semantics where a modified challenge burns the nonce instead of leaving it replayable.
/// </summary>
public sealed class RedisDmlApprovalChallengeStore : IDmlApprovalChallengeStore, IAsyncDisposable
{
    private const string ConsumeScript = """
        local value = redis.call('GET', KEYS[1])
        if not value then
            return false
        end
        redis.call('DEL', KEYS[1])
        return value
        """;

    private readonly RedisDmlApprovalChallengeOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Lazy<Task<ConnectionMultiplexer>>? _connection;
    private readonly IDatabase? _database;

    public RedisDmlApprovalChallengeStore(
        RedisDmlApprovalChallengeOptions options,
        TimeProvider? timeProvider = null)
    {
        _options = ValidateOptions(options);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _connection = new Lazy<Task<ConnectionMultiplexer>>(ConnectAsync);
    }

    internal RedisDmlApprovalChallengeStore(
        RedisDmlApprovalChallengeOptions options,
        IDatabase database,
        TimeProvider? timeProvider = null)
    {
        _options = ValidateOptions(options);
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask RegisterAsync(
        DmlApprovalChallenge challenge,
        CancellationToken cancellationToken = default)
    {
        ValidateChallenge(challenge);
        cancellationToken.ThrowIfCancellationRequested();

        var ttl = challenge.ExpiresAt - _timeProvider.GetUtcNow();
        if (ttl <= TimeSpan.Zero)
            throw new InvalidOperationException("Cannot register an expired DML approval challenge.");

        var database = await GetDatabaseAsync(cancellationToken);
        var payload = JsonSerializer.Serialize(challenge);
        var registered = await database.StringSetAsync(
                Key(challenge.Nonce),
                payload,
                ttl,
                When.NotExists,
                CommandFlags.None)
            .WaitAsync(cancellationToken);
        if (!registered)
            throw new InvalidOperationException("Duplicate DML approval challenge nonce.");
    }

    public async ValueTask<bool> TryConsumeAsync(
        DmlApprovalChallenge challenge,
        CancellationToken cancellationToken = default)
    {
        ValidateChallenge(challenge);
        cancellationToken.ThrowIfCancellationRequested();

        var database = await GetDatabaseAsync(cancellationToken);
        var result = await database.ScriptEvaluateAsync(
                ConsumeScript,
                [Key(challenge.Nonce)],
                [])
            .WaitAsync(cancellationToken);
        if (result.IsNull)
            return false;

        var payload = result.ToString();
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        DmlApprovalChallenge? registered;
        try
        {
            registered = JsonSerializer.Deserialize<DmlApprovalChallenge>(payload);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "Stored DML approval challenge payload is invalid.",
                exception);
        }
        return registered == challenge;
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is { IsValueCreated: true } && _connection.Value.IsCompletedSuccessfully)
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

    private async Task<IDatabase> GetDatabaseAsync(CancellationToken cancellationToken)
    {
        if (_database is not null)
            return _database;
        var connection = await _connection!.Value.WaitAsync(cancellationToken);
        return connection.GetDatabase();
    }

    private RedisKey Key(string nonce) => _options.KeyPrefix + nonce;

    private static RedisDmlApprovalChallengeOptions ValidateOptions(
        RedisDmlApprovalChallengeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
            throw new InvalidOperationException("DML approval Redis connection string is required.");
        if (string.IsNullOrWhiteSpace(options.KeyPrefix))
            throw new InvalidOperationException("DML approval Redis key prefix is required.");
        return options;
    }

    private static void ValidateChallenge(DmlApprovalChallenge challenge)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        if (string.IsNullOrWhiteSpace(challenge.Nonce))
            throw new InvalidOperationException("DML approval challenge nonce is missing.");
    }
}
