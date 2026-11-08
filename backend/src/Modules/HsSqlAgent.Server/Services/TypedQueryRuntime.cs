using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Binding;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Admin.Service.Models;
using SqlAgent.Service.Core.Execution;

namespace HsSqlAgent.Server.Services;

public sealed record QueryExecutionWithFacts(
    QueryExecutionResult Execution,
    QueryFacts Facts);

public interface ITypedQueryRuntime
{
    Task<QueryExecutionResult> ExecuteAsync(
        ISqlProvider provider,
        string connectionString,
        string sql,
        SqlAgentToolType sourceDialect,
        SecurityPolicyModel policy,
        IReadOnlySet<string>? allowedTables,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional additive runtime capability for callers that need compiler-derived query facts from the
/// exact parser/binder pass used to build the executable command.
/// </summary>
public interface ITypedQueryRuntimeFacts
{
    Task<QueryExecutionWithFacts> ExecuteWithFactsAsync(
        ISqlProvider provider,
        string connectionString,
        string sql,
        SqlAgentToolType sourceDialect,
        SecurityPolicyModel policy,
        IReadOnlySet<string>? allowedTables,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Server-side SELECT execution boundary. SQL text enters the F# compiler facade directly; callers
/// cannot construct or mutate a compatibility AST to bypass binding, validation, policy, or rendering.
/// </summary>
public sealed class TypedQueryRuntime(ISqlCompileEvidenceObserver? compileEvidenceObserver = null)
    : ITypedQueryRuntime, ITypedQueryRuntimeFacts
{
    private static readonly CompiledSqlCommandExecutor QueryExecutor = new();
    private readonly ISqlCompileEvidenceObserver? _compileEvidenceObserver = compileEvidenceObserver;
    public CompiledSqlCommand Compile(
        ISqlProvider provider,
        string sql,
        SqlAgentToolType sourceDialect,
        SecurityPolicyModel policy,
        IReadOnlySet<string>? allowedTables) =>
        Compile(provider, sql, sourceDialect, policy, allowedTables, targetProfile: null);

    internal CompiledSqlCommand Compile(
        ISqlProvider provider,
        string sql,
        SqlAgentToolType sourceDialect,
        SecurityPolicyModel policy,
        IReadOnlySet<string>? allowedTables,
        SqlProviderCapabilityProfile? targetProfile)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ArgumentNullException.ThrowIfNull(policy);

        var validationContext = new SqlPlanValidationContext(
            ComputePolicyVersion(policy, allowedTables),
            allowedTables);
        var executionPolicy = new SqlExecutionPlanPolicy(policy.QueryMaxRows);

        if (targetProfile is not null
            && sourceDialect == provider.Type)
        {
            return ObserveCompile(() => SqlCoreFacade.CompileQuery(
                sql,
                sourceDialect,
                provider.Type,
                validationContext,
                executionPolicy,
                targetProfile,
                targetProfile));
        }

        return ObserveCompile(() => SqlCoreFacade.CompileQuery(
            sql,
            sourceDialect,
            provider.Type,
            validationContext,
            executionPolicy,
            targetProfile));
    }

    internal CompiledQueryWithFacts CompileWithFacts(
        ISqlProvider provider,
        string sql,
        SqlAgentToolType sourceDialect,
        SecurityPolicyModel policy,
        IReadOnlySet<string>? allowedTables,
        SqlProviderCapabilityProfile? targetProfile)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ArgumentNullException.ThrowIfNull(policy);

        var validationContext = new SqlPlanValidationContext(
            ComputePolicyVersion(policy, allowedTables),
            allowedTables);
        var executionPolicy = new SqlExecutionPlanPolicy(policy.QueryMaxRows);
        var sourceProfile = targetProfile is not null && sourceDialect == provider.Type
            ? targetProfile
            : null;

        return ObserveCompileWithFacts(() => SqlCoreFacade.CompileQueryWithFacts(
            sql,
            sourceDialect,
            provider.Type,
            validationContext,
            executionPolicy,
            sourceProfile,
            targetProfile));
    }

    private CompiledQueryWithFacts ObserveCompileWithFacts(Func<CompiledQueryWithFacts> compile)
    {
        try
        {
            var result = compile();
            _compileEvidenceObserver?.Observe(result.Command.CompileEvidence);
            return result;
        }
        catch (Exception exception)
        {
            _compileEvidenceObserver?.Observe(exception);
            throw;
        }
    }

    private CompiledSqlCommand ObserveCompile(Func<CompiledSqlCommand> compile)
    {
        try
        {
            var command = compile();
            _compileEvidenceObserver?.Observe(command.CompileEvidence);
            return command;
        }
        catch (Exception exception)
        {
            _compileEvidenceObserver?.Observe(exception);
            throw;
        }
    }

    // SQLite functions are connection-local. An embedding host can replace a
    // built-in such as ABS with an arbitrary callback (including a hidden table
    // read). F# canonical function grammar does not prove that the open physical
    // connection still resolves the function to the original SQLite built-in.
    //
    // PRAGMA function_list is read from the *same connection* as execution.
    // Inspect rows in CLR code, not SQL joins/functions that might themselves be
    // overridden. No user SQL is parsed or interpolated into this attestation.
    private static async Task VerifySqliteBuiltInFunctionIdentityAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using var registry = connection.CreateCommand();
        registry.CommandText = "PRAGMA function_list";
        registry.CommandTimeout = 30;
        await using var reader = await registry.ExecuteReaderAsync(cancellationToken);

        if (reader.FieldCount < 5)
            throw new InvalidOperationException(
                "SQLite Query cannot verify the physical function registry.");

        var builtIns = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        var callbacks = new List<(string Name, int Arguments)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(0);
            var native = Convert.ToInt32(reader.GetValue(1)) != 0;
            var arity = Convert.ToInt32(reader.GetValue(4));

            if (native)
            {
                if (!builtIns.TryGetValue(name, out var signatures))
                    builtIns[name] = signatures = new HashSet<int>();
                signatures.Add(arity);
            }
            else
            {
                callbacks.Add((name, arity));
            }
        }

        if (builtIns.Count == 0)
            throw new InvalidOperationException(
                "SQLite Query cannot verify the physical built-in function registry.");

        foreach (var (name, arity) in callbacks)
        {
            if (builtIns.TryGetValue(name, out var nativeSignatures)
                && nativeSignatures.Any(nativeArity =>
                    nativeArity == arity || nativeArity == -1 || arity == -1))
                throw new UnauthorizedAccessException(
                    "SQLite Query cannot attest a built-in function identity because "
                    + "the active connection registers a colliding application-defined function. "
                    + "Use a connection without built-in function overrides.");
        }
    }

    // PostgreSQL SELECT is not intrinsically read-only: VOLATILE functions can write
    // relations or advance sequences. The server enforces an actual transaction fence,
    // including inside user-defined functions; compiler function lists are not authority.
    // Other providers keep their current execution path until independently proven.
    private static async Task<QueryExecutionResult> ExecuteVerifiedQueryAsync(
        ISqlProvider provider,
        CompiledSqlCommand command,
        DbConnection connection,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        if (provider.Type == SqlAgentToolType.Sqlite)
            await VerifySqliteBuiltInFunctionIdentityAsync(connection, cancellationToken);

        if (provider.Type != SqlAgentToolType.Postgres)
            return await QueryExecutor.ExecuteQueryAsync(
                command, connection, timeoutSeconds, cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var fence = connection.CreateCommand())
        {
            fence.Transaction = transaction;
            fence.CommandText = "SET TRANSACTION READ ONLY";
            fence.CommandTimeout = timeoutSeconds > 0 ? timeoutSeconds : 30;
            await fence.ExecuteNonQueryAsync(cancellationToken);
        }

        // A connection may explicitly place an attacker-writable schema before
        // pg_catalog. Such a schema can shadow built-in operators (including '='),
        // causing approved SQL to execute a user function against excluded tables.
        // Keep the caller's table search path after pg_catalog, but never permit
        // it to override catalog operators/types during this one Query transaction.
        // set_config(true) is transaction-local; every function below is schema-pinned,
        // and no user-controlled string is interpolated into executable SQL text.
        await using (var searchPathFence = connection.CreateCommand())
        {
            searchPathFence.Transaction = transaction;
            searchPathFence.CommandText =
                "SELECT pg_catalog.set_config('search_path', " +
                "pg_catalog.concat('pg_catalog, ', pg_catalog.current_setting('search_path')), true)";
            searchPathFence.CommandTimeout = timeoutSeconds > 0 ? timeoutSeconds : 30;
            await searchPathFence.ExecuteScalarAsync(cancellationToken);
        }

        var execution = await QueryExecutor.ExecuteQueryInTransactionAsync(
            command, connection, timeoutSeconds, transaction, cancellationToken);
        await transaction.RollbackAsync(cancellationToken);
        return execution;
    }

    public async Task<QueryExecutionResult> ExecuteAsync(
        ISqlProvider provider,
        string connectionString,
        string sql,
        SqlAgentToolType sourceDialect,
        SecurityPolicyModel policy,
        IReadOnlySet<string>? allowedTables,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        await using var connection = provider.Connections.Create(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DbException exception)
        {
            throw provider.Errors.Map(exception, "query");
        }

        var verifiedProfile = RuntimeServerProfileVerifier.Capture(provider.Type, connection);
        var command = Compile(
            provider,
            sql,
            sourceDialect,
            policy,
            allowedTables,
            verifiedProfile.TargetProfile);

        try
        {
            return await ExecuteVerifiedQueryAsync(
                provider,
                command,
                connection,
                policy.QueryTimeoutSeconds,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DbException exception)
        {
            throw provider.Errors.Map(exception, "query");
        }
    }

    public async Task<QueryExecutionWithFacts> ExecuteWithFactsAsync(
        ISqlProvider provider,
        string connectionString,
        string sql,
        SqlAgentToolType sourceDialect,
        SecurityPolicyModel policy,
        IReadOnlySet<string>? allowedTables,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        await using var connection = provider.Connections.Create(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DbException exception)
        {
            throw provider.Errors.Map(exception, "query");
        }

        var verifiedProfile = RuntimeServerProfileVerifier.Capture(provider.Type, connection);
        var compilation = CompileWithFacts(
            provider,
            sql,
            sourceDialect,
            policy,
            allowedTables,
            verifiedProfile.TargetProfile);

        try
        {
            var execution = await ExecuteVerifiedQueryAsync(
                provider,
                compilation.Command,
                connection,
                policy.QueryTimeoutSeconds,
                cancellationToken);
            return new QueryExecutionWithFacts(execution, compilation.Facts);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DbException exception)
        {
            throw provider.Errors.Map(exception, "query");
        }
    }

    internal static SqlProviderCapabilityProfile CreateVerifiedTargetProfile(
        SqlAgentToolType provider,
        System.Data.Common.DbConnection openConnection) =>
        RuntimeServerProfileVerifier.Capture(provider, openConnection).TargetProfile;

    internal static string ComputePolicyVersion(
        SecurityPolicyModel policy,
        IReadOnlySet<string>? allowedTables)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var tables = allowedTables is null
            ? string.Empty
            : string.Join(',', allowedTables.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        var material =
            $"maxRows={policy.QueryMaxRows};" +
            $"timeout={policy.QueryTimeoutSeconds};" +
            $"updatedTicks={policy.UpdatedAt?.ToUniversalTime().Ticks ?? 0L};" +
            $"tables={tables}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }
}
