using System.Collections;
using Admin.Service.Models;
using HsSqlAgent.Provider.Abstractions;
using HsSqlAgent.Server.Models;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Binding;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Models;
using HsSqlAgent.SqlCore.SqlParsing;

namespace HsSqlAgent.Server.Services;

/// <summary>
/// Compile-only SQL simulator used by the Admin security surface. It intentionally never executes
/// SQL or opens database connections; the caller supplies a runtime-verified target profile.
/// </summary>
internal sealed class SqlExplainCompiler(ISqlCompileEvidenceObserver? compileEvidenceObserver = null)
{
    private readonly ISqlCompileEvidenceObserver? _compileEvidenceObserver = compileEvidenceObserver;

    internal SqlExplainResponse Explain(
        ISqlProvider provider,
        SqlExplainRequest request,
        SqlAgentToolType sourceDialect,
        SecurityPolicyModel policy,
        IReadOnlySet<string>? allowedTables,
        SqlProviderCapabilityProfile targetProfile,
        SqlExplainScope scope)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(targetProfile);

        var statementType = NormalizeStatementType(request.StatementType);
        return statementType switch
        {
            "Query" => ExplainQuery(
                provider,
                request,
                sourceDialect,
                policy,
                allowedTables,
                targetProfile,
                scope),
            "DML" => ExplainDml(
                provider,
                request,
                sourceDialect,
                policy,
                allowedTables,
                targetProfile,
                scope),
            _ => Failed(
                request,
                sourceDialect,
                provider.Type,
                policy,
                scope,
                "validation.statement_type",
                "StatementType must be either 'Query' or 'DML'.",
                "Input")
        };
    }

    private SqlExplainResponse ExplainQuery(
        ISqlProvider provider,
        SqlExplainRequest request,
        SqlAgentToolType sourceDialect,
        SecurityPolicyModel policy,
        IReadOnlySet<string>? allowedTables,
        SqlProviderCapabilityProfile targetProfile,
        SqlExplainScope scope)
    {
        try
        {
            var compilation = new TypedQueryRuntime(_compileEvidenceObserver).CompileWithFacts(
                provider,
                request.Sql,
                sourceDialect,
                policy,
                allowedTables,
                targetProfile);

            return Succeeded(
                request,
                sourceDialect,
                provider.Type,
                policy,
                scope,
                [SqlExplainModelMapper.Statement(1, compilation.Command, compilation.Facts)]);
        }
        catch (Exception exception)
        {
            return Failed(
                request,
                sourceDialect,
                provider.Type,
                policy,
                scope,
                exception);
        }
    }

    private SqlExplainResponse ExplainDml(
        ISqlProvider provider,
        SqlExplainRequest request,
        SqlAgentToolType sourceDialect,
        SecurityPolicyModel policy,
        IReadOnlySet<string>? allowedTables,
        SqlProviderCapabilityProfile targetProfile,
        SqlExplainScope scope)
    {
        try
        {
            var sourceProfile = sourceDialect == provider.Type ? targetProfile : null;
            var parsedBatch = CoreDmlBatchTextParser.ParseDmlBatch(
                request.Sql,
                sourceDialect,
                sourceProfile);
            var validationContext = new SqlPlanValidationContext(
                TypedQueryRuntime.ComputePolicyVersion(policy, allowedTables),
                allowedTables);
            var dmlPolicy = new DmlCompilationPolicy(
                policy.RequireWhereForUpdate,
                policy.RequireWhereForDelete,
                policy.AllowFullTableUpdate,
                policy.AllowFullTableDelete);

            var statements = new List<SqlExplainStatement>(parsedBatch.Count);
            for (var index = 0; index < parsedBatch.Count; index++)
            {
                var parsed = parsedBatch.Statements[index];
                TypedDmlRuntime.EnsureSupportedStatement(parsed.Statement);
                CompiledSqlCommand command;
                try
                {
                    command = SqlCoreFacade.CompileDml(
                        parsed,
                        provider.Type,
                        validationContext,
                        dmlPolicy,
                        targetProfile,
                        conflictTargetAssurance: null);
                    _compileEvidenceObserver?.Observe(command.CompileEvidence);
                }
                catch (Exception exception)
                {
                    _compileEvidenceObserver?.Observe(exception);
                    throw;
                }
                statements.Add(SqlExplainModelMapper.Statement(index + 1, command));
            }

            return Succeeded(
                request,
                sourceDialect,
                provider.Type,
                policy,
                scope,
                statements);
        }
        catch (Exception exception)
        {
            return Failed(
                request,
                sourceDialect,
                provider.Type,
                policy,
                scope,
                exception);
        }
    }

    internal static SqlExplainResponse Denied(
        SqlExplainRequest request,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetProvider,
        SecurityPolicyModel policy,
        SqlExplainScope scope,
        string code,
        string message) =>
        Failed(
            request,
            sourceDialect,
            targetProvider,
            policy,
            scope,
            code,
            message,
            "Authorization");

    internal static string NormalizeStatementType(string? value) =>
        string.Equals(value, "DML", StringComparison.OrdinalIgnoreCase)
            ? "DML"
            : string.Equals(value, "Query", StringComparison.OrdinalIgnoreCase)
                ? "Query"
                : value?.Trim() ?? string.Empty;

    private static SqlExplainResponse Succeeded(
        SqlExplainRequest request,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetProvider,
        SecurityPolicyModel policy,
        SqlExplainScope scope,
        IReadOnlyList<SqlExplainStatement> statements) =>
        new(
            true,
            "compile-only",
            false,
            NormalizeStatementType(request.StatementType),
            sourceDialect.ToString(),
            targetProvider.ToString(),
            scope,
            SqlExplainModelMapper.Policy(policy),
            statements,
            null);

    private static SqlExplainResponse Failed(
        SqlExplainRequest request,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetProvider,
        SecurityPolicyModel policy,
        SqlExplainScope scope,
        Exception exception)
    {
        var evidence = FindEvidence(exception);
        var diagnostics = FindDiagnostics(exception)
            .Select(SqlExplainModelMapper.Diagnostic)
            .ToArray();
        var code = evidence?.DecisionCode
                   ?? diagnostics.FirstOrDefault()?.Code
                   ?? exception switch
                   {
                       UnauthorizedAccessException => "authorization.denied",
                       ArgumentException => "validation.invalid_argument",
                       NotSupportedException => "capability.unsupported",
                       TimeoutException => "execution.timeout",
                       _ => "sql.explain_failed"
                   };
        var stage = evidence?.DecisionBoundary.ToString()
                    ?? diagnostics.FirstOrDefault()?.Stage
                    ?? "Simulation";

        return Failed(
            request,
            sourceDialect,
            targetProvider,
            policy,
            scope,
            code,
            exception.Message,
            stage,
            diagnostics,
            SqlExplainModelMapper.Evidence(evidence),
            exception is TimeoutException);
    }

    private static SqlExplainResponse Failed(
        SqlExplainRequest request,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetProvider,
        SecurityPolicyModel policy,
        SqlExplainScope scope,
        string code,
        string message,
        string stage,
        IReadOnlyList<SqlExplainDiagnostic>? diagnostics = null,
        SqlExplainEvidence? evidence = null,
        bool retryable = false) =>
        new(
            false,
            "compile-only",
            false,
            NormalizeStatementType(request.StatementType),
            sourceDialect.ToString(),
            targetProvider.ToString(),
            scope,
            SqlExplainModelMapper.Policy(policy),
            [],
            new SqlExplainFailure(
                code,
                message,
                stage,
                retryable,
                diagnostics ?? [],
                evidence));

    private static SqlCompileEvidence? FindEvidence(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            var evidence = SqlCompileEvidence.TryGetFromException(current);
            if (evidence is not null) return evidence;
        }
        return null;
    }

    private static IReadOnlyList<SqlDiagnostic> FindDiagnostics(Exception exception)
    {
        var diagnostics = new List<SqlDiagnostic>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case SqlParseException parse when parse.Diagnostic is not null:
                    diagnostics.Add(parse.Diagnostic);
                    break;
                case SqlCompilationException compilation when compilation.Diagnostic is not null:
                    diagnostics.Add(compilation.Diagnostic);
                    break;
            }

            foreach (DictionaryEntry entry in current.Data)
                if (entry.Value is SqlDiagnostic diagnostic)
                    diagnostics.Add(diagnostic);
        }

        return diagnostics
            .GroupBy(
                diagnostic => $"{diagnostic.Code}|{diagnostic.Stage}|{diagnostic.Span?.Start}|{diagnostic.Span?.Length}",
                StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
    }
}
