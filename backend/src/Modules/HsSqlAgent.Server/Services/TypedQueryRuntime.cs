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
public sealed class TypedQueryRuntime(ISqlCompileEvidenceObserver? compileEvidenceObserver = null) : ITypedQueryRuntime
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
            return await QueryExecutor.ExecuteQueryAsync(
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
            var execution = await QueryExecutor.ExecuteQueryAsync(
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
