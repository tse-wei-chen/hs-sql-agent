using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Core.Binding;
using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Enums;

namespace SqlAgent.Service.Core.Pipeline;

public sealed record ParsedStatement(
    SqlStatement Statement,
    SqlAgentToolType SourceDialect);

public sealed record BoundStatement(
    SqlStatement Statement,
    QueryFacts Facts,
    SqlAgentToolType SourceDialect);

public sealed record CanonicalStatement(
    SqlStatement Statement,
    QueryFacts Facts,
    SqlAgentToolType SourceDialect,
    SqlAgentToolType TargetProvider);

public sealed record ValidatedSqlPlan(
    SqlStatement Statement,
    QueryFacts Facts,
    SqlAgentToolType SourceDialect,
    SqlAgentToolType TargetProvider,
    string PolicyVersion);

public interface ISqlBinder
{
    BoundStatement Bind(ParsedStatement statement);
}

public interface ISqlNormalizer
{
    CanonicalStatement Normalize(BoundStatement statement, SqlAgentToolType targetProvider);
}

public interface ISqlPlanValidator
{
    ValidatedSqlPlan Validate(CanonicalStatement statement, string policyVersion);
}

public interface IProviderLowerer
{
    SqlAgentToolType Provider { get; }
    CompiledSqlCommand Lower(ValidatedSqlPlan plan);
}

public interface ISqlCommandExecutor
{
    Task<QueryExecutionResult> ExecuteQueryAsync(
        CompiledSqlCommand command,
        string connectionString,
        CancellationToken cancellationToken = default);
}

public sealed record QueryExecutionResult(
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    int RowCount,
    TimeSpan Duration,
    IReadOnlyList<string> Diagnostics);
