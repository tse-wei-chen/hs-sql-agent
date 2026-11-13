using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Binding;
using System.Data;
using System.Data.Common;
using HsSqlAgent.Provider.Abstractions;
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

        var effectiveTables = NormalizeAllowedTables(allowedTables);
        var validationContext = new SqlPlanValidationContext(
            ComputePolicyVersion(policy, effectiveTables),
            effectiveTables);
        var executionPolicy = CreateQueryPolicy(provider.Type, policy, effectiveTables);

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

        var effectiveTables = NormalizeAllowedTables(allowedTables);
        var validationContext = new SqlPlanValidationContext(
            ComputePolicyVersion(policy, effectiveTables),
            effectiveTables);
        var executionPolicy = CreateQueryPolicy(provider.Type, policy, effectiveTables);
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

    // One semantic scope for the public CLR Host, compiler facade, policy
    // fingerprint, and physical provider proof. A supplied empty set is
    // equivalent to no whitelist; it must NEVER disable an unbounded-Query
    // safety guard after the F# facade normalizes it to None.
    private static IReadOnlySet<string>? NormalizeAllowedTables(
        IReadOnlySet<string>? allowedTables) =>
        allowedTables is { Count: > 0 } ? allowedTables : null;

    private static SqlExecutionPlanPolicy CreateQueryPolicy(
        SqlAgentToolType provider,
        SecurityPolicyModel securityPolicy,
        IReadOnlySet<string>? effectiveTables) =>
        new(
            securityPolicy.QueryMaxRows,
            effectiveTables is null
                && provider is (SqlAgentToolType.MySQL
                    or SqlAgentToolType.Oracle or SqlAgentToolType.Firebird)
                // All three use a native read-only Query transaction, but
                // cannot prove purity of an arbitrary schema-qualified native
                // routine: MySQL TEMPORARY writes and Oracle/Firebird
                // autonomous or external side effects escape that guarantee.
                // Host-only typed policy; standalone SqlCore grammar unchanged.
                ? SqlQueryNativeFunctionPolicy.RejectOpaqueQualifiedOrQuoted
                : SqlQueryNativeFunctionPolicy.PreserveCompilerGrammar);

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
    private static async Task<List<(string Name, int Arguments)>> VerifySqliteBuiltInFunctionIdentityAsync(
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

        return callbacks;
    }

    // A SQLite Query SELECT can call an embedding-host callback that opens a
    // completely separate writable connection. PRAGMA query_only on the
    // original physical connection cannot prevent those writes. Do not infer
    // function purity from SELECT, readonly transactions or name spelling.
    //
    // EXPLAIN compiles (does not execute) the exact authorized, parameterized
    // SQL on the SAME physical connection after query_only is enabled. Every
    // invoked function opcode is compared with that connection's actual
    // PRAGMA function_list identities. Uncalled extensions remain permitted;
    // a called application-defined function is denied BEFORE any Query row
    // can execute. A malformed/unsupported proof fails closed.
    private static async Task VerifySqliteInvokedHostCallbacksAsync(
        CompiledSqlCommand compiled,
        DbConnection connection,
        IReadOnlyList<(string Name, int Arguments)> callbacks,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        if (callbacks.Count == 0)
            return;

        await using var explain = connection.CreateCommand();
        explain.CommandText = "EXPLAIN " + compiled.Sql;
        explain.CommandTimeout = timeoutSeconds > 0 ? timeoutSeconds : 30;
        foreach (var parameter in compiled.Parameters)
        {
            var name = parameter.Name.Trim();
            if (name.Length == 0)
                throw new InvalidOperationException(
                    "SQLite Query cannot attest an unnamed compiled parameter.");
            var bound = explain.CreateParameter();
            bound.ParameterName = name[0] is '@' or ':' or '$' ? name : "@" + name;
            bound.Value = parameter.Value ?? DBNull.Value;
            explain.Parameters.Add(bound);
        }

        await using var reader = await explain.ExecuteReaderAsync(cancellationToken);
        if (reader.FieldCount < 6)
            throw new InvalidOperationException(
                "SQLite Query cannot attest the compiled function execution program.");

        while (await reader.ReadAsync(cancellationToken))
        {
            var opcode = reader.GetString(1);
            if (opcode is not ("Function" or "PureFunc" or "AggStep"
                or "AggFinal" or "AggValue" or "AggInverse"))
                continue;

            if (reader.IsDBNull(5))
                throw new InvalidOperationException(
                    "SQLite Query cannot attest a function opcode without an identity.");
            var signature = reader.GetString(5);
            var open = signature.LastIndexOf('(');
            var close = signature.LastIndexOf(')');
            if (open <= 0 || close != signature.Length - 1
                || !int.TryParse(signature.AsSpan(open + 1, close - open - 1),
                    out var arguments))
                throw new InvalidOperationException(
                    "SQLite Query cannot attest a compiled function signature.");

            var name = signature[..open];
            // Some SQLite opcode forms prefix the function name with a
            // numeric execution context, e.g. 0:function_name(2).
            var separator = name.LastIndexOf(':');
            if (separator >= 0)
                name = name[(separator + 1)..];
            if (name.Length == 0)
                throw new InvalidOperationException(
                    "SQLite Query cannot attest an empty function identity.");

            if (callbacks.Any(callback =>
                string.Equals(callback.Name, name, StringComparison.OrdinalIgnoreCase)
                && (callback.Arguments == arguments || callback.Arguments == -1)))
                throw new UnauthorizedAccessException(
                    "SQLite Query cannot execute an application-defined function "
                    + "on a connection with unproven callback side effects.");
        }
    }

    // SQLite's compiled SELECT family cannot prove that a host-registered function
    // will not issue INSERT/UPDATE/DELETE through this same physical connection.
    // PRAGMA query_only is a connection-local SQLite engine write fence, not a
    // function sandbox: a callback could modify the setting, open another writable
    // connection, or perform external effects. Embedding hosts must still restrict
    // function registrations and the underlying SQLite file permissions.
    //
    // Capture and restore the prior value so Query does not silently leave a
    // host-owned or pooled physical connection in a different permission mode.
    private static async Task<bool> ReadSqliteQueryOnlyAsync(
        DbConnection connection, int timeoutSeconds, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA query_only";
        command.CommandTimeout = timeoutSeconds > 0 ? timeoutSeconds : 30;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value switch
        {
            0L or 0 => false,
            1L or 1 => true,
            _ => throw new InvalidOperationException(
                "SQLite Query could not attest the physical read-only connection setting.")
        };
    }

    private static async Task SetSqliteQueryOnlyAsync(
        DbConnection connection, bool enabled, int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = enabled ? "PRAGMA query_only = ON" : "PRAGMA query_only = OFF";
        command.CommandTimeout = timeoutSeconds > 0 ? timeoutSeconds : 30;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<QueryExecutionResult> ExecuteSqliteReadOnlyQueryAsync(
        CompiledSqlCommand command, DbConnection connection, int timeoutSeconds,
        IReadOnlyList<(string Name, int Arguments)> callbacks,
        CancellationToken cancellationToken)
    {
        var alreadyReadOnly = await ReadSqliteQueryOnlyAsync(
            connection, timeoutSeconds, cancellationToken);
        var changed = false;
        try
        {
            if (!alreadyReadOnly)
            {
                // Even a provider exception during the setting operation may
                // leave the pragma applied. Always attempt independent cleanup.
                changed = true;
                await SetSqliteQueryOnlyAsync(
                    connection, true, timeoutSeconds, cancellationToken);
            }

            if (!await ReadSqliteQueryOnlyAsync(connection, timeoutSeconds, cancellationToken))
                throw new InvalidOperationException(
                    "SQLite Query requires an attested physical read-only connection.");

            await VerifySqliteInvokedHostCallbacksAsync(
                command, connection, callbacks, timeoutSeconds, cancellationToken);
            return await QueryExecutor.ExecuteQueryAsync(
                command, connection, timeoutSeconds, cancellationToken);
        }
        finally
        {
            if (changed)
            {
                // The caller's cancellation must not leave a reusable host
                // connection permanently query_only. A short independent
                // cleanup bound also avoids awaiting a misbehaving provider
                // indefinitely. A failed restore fails closed.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await SetSqliteQueryOnlyAsync(connection, false, timeoutSeconds, cleanup.Token)
                    .WaitAsync(cleanup.Token);
                if (await ReadSqliteQueryOnlyAsync(connection, timeoutSeconds, cleanup.Token))
                    throw new InvalidOperationException(
                        "SQLite Query failed to restore the physical connection setting.");
            }
        }
    }

    // PostgreSQL SELECT is not intrinsically read-only: VOLATILE functions can write
    // relations or advance sequences. The server enforces an actual transaction fence,
    // including inside user-defined functions; compiler function lists are not authority.
    // A read-only transaction does not prove safety of independent connections or external UDF effects.
    private static async Task<QueryExecutionResult> ExecuteVerifiedQueryAsync(
        ISqlProvider provider,
        CompiledSqlCommand command,
        DbConnection connection,
        int timeoutSeconds,
        bool requiresSqlServerPrincipalProof,
        CancellationToken cancellationToken)
    {
        // An absent table whitelist cannot serve as physical SQL Server
        // least-privilege evidence. Verify the effective connected principal
        // on this same open database connection before executing the SELECT.
        // This is deliberately not an application-level SQL text blacklist.
        if (provider.Type == SqlAgentToolType.MsSqlServer
            && requiresSqlServerPrincipalProof)
            await SqlServerQueryPrincipalGuard.VerifyAsync(
                connection, timeoutSeconds, cancellationToken);

        if (provider.Type == SqlAgentToolType.Sqlite)
        {
            var callbacks = await VerifySqliteBuiltInFunctionIdentityAsync(
                connection, cancellationToken);
            return await ExecuteSqliteReadOnlyQueryAsync(
                command, connection, timeoutSeconds, callbacks, cancellationToken);
        }

        // A MySQL SELECT may invoke a stored function that writes normal
        // tables, including through a Query-only MCP key with no whitelist.
        // MySQL's next-transaction READ ONLY access mode must be set before
        // BeginTransactionAsync on the exact physical execution connection.
        // Never run the compiled SQL unfenced if the setup fails.
        //
        // READ ONLY does not prohibit temporary-table writes or arbitrary
        // external UDF effects; least-privilege DB users remain necessary.
        if (provider.Type == SqlAgentToolType.MySQL)
        {
            await using (var fence = connection.CreateCommand())
            {
                fence.CommandText = "SET TRANSACTION READ ONLY";
                fence.CommandTimeout = timeoutSeconds > 0 ? timeoutSeconds : 30;
                await fence.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var readOnlyTransaction =
                await connection.BeginTransactionAsync(cancellationToken);
            var result = await QueryExecutor.ExecuteQueryInTransactionAsync(
                command, connection, timeoutSeconds, readOnlyTransaction, cancellationToken);
            await readOnlyTransaction.RollbackAsync(cancellationToken);
            return result;
        }

        // Oracle and Firebird already expose an audited provider-native read-only
        // transaction for DML preview. Reuse that exact native mechanism for
        // Query; a compiled SELECT cannot substitute for physical write fencing.
        // Fail closed if an embedding provider does not offer the contract.
        //
        // Oracle/Firebird autonomous transactions and external function effects
        // remain outside this transaction fence; use a restricted DB principal.
        if (provider.Type is SqlAgentToolType.Oracle or SqlAgentToolType.Firebird)
        {
            if (provider is not IProviderDmlPreviewTransactionSource readOnlySource)
                throw new InvalidOperationException(
                    "Oracle and Firebird Query require a provider-native read-only transaction source.");

            await using var readOnlyTransaction = await readOnlySource.PreviewTransactions.BeginAsync(
                connection, provider.Type, IsolationLevel.ReadCommitted, cancellationToken);
            var result = await QueryExecutor.ExecuteQueryInTransactionAsync(
                command, connection, timeoutSeconds, readOnlyTransaction, cancellationToken);
            await readOnlyTransaction.RollbackAsync(cancellationToken);
            return result;
        }

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
        var (execution, _) = await ExecuteCoreAsync(
            provider, connectionString, sql, sourceDialect, policy,
            allowedTables, includeFacts: false, cancellationToken);
        return execution;
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
        var (execution, facts) = await ExecuteCoreAsync(
            provider, connectionString, sql, sourceDialect, policy,
            allowedTables, includeFacts: true, cancellationToken);
        return new QueryExecutionWithFacts(
            execution, facts ?? throw new InvalidOperationException(
                "Query Facts were not returned from the verified compiler pass."));
    }

    // Both public Query entrypoints share exactly the same physical connection,
    // permission normalization, verified runtime profile, provider fence and
    // error behavior. The no-facts path does not incur Query Facts inspection.
    private async Task<(QueryExecutionResult Execution, QueryFacts? Facts)> ExecuteCoreAsync(
        ISqlProvider provider,
        string connectionString,
        string sql,
        SqlAgentToolType sourceDialect,
        SecurityPolicyModel policy,
        IReadOnlySet<string>? allowedTables,
        bool includeFacts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var effectiveTables = NormalizeAllowedTables(allowedTables);
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
        CompiledSqlCommand command;
        QueryFacts? facts = null;
        if (includeFacts)
        {
            var compiled = CompileWithFacts(
                provider, sql, sourceDialect, policy, effectiveTables,
                verifiedProfile.TargetProfile);
            command = compiled.Command;
            facts = compiled.Facts;
        }
        else
        {
            command = Compile(
                provider, sql, sourceDialect, policy, effectiveTables,
                verifiedProfile.TargetProfile);
        }

        try
        {
            var execution = await ExecuteVerifiedQueryAsync(
                provider, command, connection, policy.QueryTimeoutSeconds,
                requiresSqlServerPrincipalProof: effectiveTables is null,
                cancellationToken);
            return (execution, facts);
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
