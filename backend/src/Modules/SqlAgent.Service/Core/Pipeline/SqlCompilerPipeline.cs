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

public sealed record SqlPlanValidationContext(
    string PolicyVersion,
    IReadOnlySet<string>? AllowedTables = null);

public sealed record ValidatedSqlPlan(
    SqlStatement Statement,
    QueryFacts Facts,
    SqlAgentToolType SourceDialect,
    SqlAgentToolType TargetProvider,
    string PolicyVersion);

public sealed record SqlExecutionPlanPolicy(
    int QueryMaxRows = 0);

public sealed record ExecutableSqlPlan(
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
    ValidatedSqlPlan Validate(
        CanonicalStatement statement,
        SqlPlanValidationContext context);
}

public interface ISqlExecutionPolicyRewriter
{
    ExecutableSqlPlan Rewrite(
        ValidatedSqlPlan plan,
        SqlExecutionPlanPolicy policy);
}

public interface IProviderLowerer
{
    SqlAgentToolType Provider { get; }
    CompiledSqlCommand Lower(ExecutableSqlPlan plan);
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
