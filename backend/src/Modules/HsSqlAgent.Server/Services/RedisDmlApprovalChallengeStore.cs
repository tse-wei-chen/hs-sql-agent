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

    // One Lua operation transitions every child into a single aggregate key.
    // All keys share a Redis Cluster hash slot, so no partial child deletion or
    // split-brain promotion is possible even with concurrent server instances.
    private const string PromoteScript = """
        local count = tonumber(ARGV[1])
        if redis.call('EXISTS', KEYS[count + 1]) == 1 then
            return 0
        end
        for i = 1, count do
            if redis.call('GET', KEYS[i]) ~= ARGV[i + 1] then
                return 0
            end
        end
        -- Redis Lua returns a status reply table ({ ok = 'OK' }) for SET.
        -- A plain string comparison always fails and must not be used here.
        local registered = redis.call('SET',
            KEYS[count + 1], ARGV[count + 2], 'PX', ARGV[count + 3], 'NX')
        if not registered or registered['ok'] ~= 'OK' then
            return 0
        end
        for i = 1, count do
            redis.call('DEL', KEYS[i])
        end
        return 1
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

    public async ValueTask<bool> TryPromoteAsync(
        IReadOnlyList<DmlApprovalChallenge> children,
        DmlApprovalChallenge aggregate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(children);
        ValidateChallenge(aggregate);
        cancellationToken.ThrowIfCancellationRequested();
        if (children.Count == 0)
            throw new InvalidOperationException("Cannot promote an empty DML approval batch.");

        var seen = new HashSet<string>(StringComparer.Ordinal) { aggregate.Nonce };
        foreach (var child in children)
        {
            ValidateChallenge(child);
            if (!seen.Add(child.Nonce))
                throw new InvalidOperationException("Atomic DML approval promotion requires distinct nonces.");
        }

        var now = _timeProvider.GetUtcNow();
        if (aggregate.ExpiresAt <= now || children.Any(x => x.ExpiresAt <= now))
            return false;

        var lifetimeMs = (long)Math.Ceiling((aggregate.ExpiresAt - now).TotalMilliseconds);
        if (lifetimeMs <= 0)
            return false;
        var keys = children.Select(x => Key(x.Nonce))
            .Append(Key(aggregate.Nonce)).ToArray();
        var args = new RedisValue[children.Count + 3];
        args[0] = children.Count;
        for (var i = 0; i < children.Count; i++)
            args[i + 1] = JsonSerializer.Serialize(children[i]);
        args[children.Count + 1] = JsonSerializer.Serialize(aggregate);
        args[children.Count + 2] = lifetimeMs;

        var database = await GetDatabaseAsync(cancellationToken);
        var result = await database.ScriptEvaluateAsync(
                PromoteScript, keys, args, CommandFlags.None)
            .WaitAsync(cancellationToken);
        return result.ToString() == "1";
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

    // Redis Cluster requires every key touched by one Lua script to share
    // a hash slot. Prefix still isolates deployments; the fixed hash tag
    // deliberately scopes atomic approval transfers to one Redis shard.
    private RedisKey Key(string nonce) => _options.KeyPrefix + "{dml-approval}:" + nonce;

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
