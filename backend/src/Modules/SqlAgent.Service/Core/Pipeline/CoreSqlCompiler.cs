using SqlAgent.Service.Core.Analysis;
using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Core.Binding;
using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Lowering;
using SqlAgent.Service.Core.Mapping;
using SqlAgent.Service.Core.Normalization;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;

namespace SqlAgent.Service.Core.Pipeline;

/// <summary>
/// Compiler pipeline entry point. The typed boundary starts at <see cref="ParsedStatement"/> so
/// binding, normalization, validation, policy rewriting and lowering cannot be invoked with a
/// transport DTO. The QueryDefinition overload is a temporary strangler adapter only.
/// </summary>
public sealed class CoreSqlCompiler(
    ISqlBinder binder,
    ISqlNormalizer normalizer,
    ISqlPlanValidator validator,
    ISqlExecutionPolicyRewriter policyRewriter)
{
    private readonly ISqlBinder _binder = binder;
    private readonly ISqlNormalizer _normalizer = normalizer;
    private readonly ISqlPlanValidator _validator = validator;
    private readonly ISqlExecutionPolicyRewriter _policyRewriter = policyRewriter;

    public static CoreSqlCompiler CreateDefault() => new(
        new SqlAstBinder(),
        CoreSqlNormalizer.CreateDefault(),
        new CoreSqlPlanValidator(),
        new CoreSqlExecutionPolicyRewriter());

    /// <summary>
    /// Temporary DTO adapter retained while external structured-query callers migrate to an
    /// explicit DTO-to-Core parsing/mapping boundary. It never mutates the supplied DTO.
    /// </summary>
    [Obsolete("Map QueryDefinition to ParsedStatement first, then call Compile(ParsedStatement, ...).")]
    public CompiledSqlCommand Compile(
        QueryDefinition definition,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetProvider,
        SqlPlanValidationContext validationContext,
        SqlExecutionPlanPolicy executionPolicy)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var parsed = new ParsedStatement(
            QueryDefinitionCoreMapper.Map(definition),
            sourceDialect);
        return Compile(parsed, targetProvider, validationContext, executionPolicy);
    }

    public CompiledSqlCommand Compile(
        ParsedStatement parsed,
        SqlAgentToolType targetProvider,
        SqlPlanValidationContext validationContext,
        SqlExecutionPlanPolicy executionPolicy)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(validationContext);
        ArgumentNullException.ThrowIfNull(executionPolicy);

        var bound = _binder.Bind(parsed);
        var canonical = _normalizer.Normalize(bound, targetProvider);
        var validated = _validator.Validate(canonical, validationContext);
        var executable = _policyRewriter.Rewrite(validated, executionPolicy);
        ValidateSqlKataBackendCompatibility(executable.Statement);
        return new SqlKataProviderLowerer(targetProvider).Lower(executable);
    }

    private static void ValidateSqlKataBackendCompatibility(SqlStatement statement)
    {
        switch (statement)
        {
            case SelectStatement select:
                foreach (var cte in select.Ctes)
                    ValidateSqlKataBackendCompatibility(cte.Query);
                if (select.From is DerivedTableSource derived)
                    ValidateSqlKataBackendCompatibility(derived.Query);
                foreach (var join in select.Joins)
                    if (join.Source is DerivedTableSource joinedDerived)
                        ValidateSqlKataBackendCompatibility(joinedDerived.Query);
                ValidateSubqueryExpressions(select);
                return;

            case QueryStatement query:
                ValidateSqlKataBackendCompatibility(query.Head);
                foreach (var operation in query.SetOperations)
                    ValidateSqlKataBackendCompatibility(operation.Query);
                return;

            default:
                throw new SqlCompilationException(
                    $"Unsupported statement for SqlKata backend: {statement.GetType().Name}");
        }
    }

    private static void ValidateSubqueryExpressions(SelectStatement select)
    {
        foreach (var item in select.Select) VisitExpression(item.Expression);
        if (select.Where is not null) VisitExpression(select.Where);
        foreach (var expression in select.GroupBy) VisitExpression(expression);
        if (select.Having is not null) VisitExpression(select.Having);
        foreach (var item in select.OrderBy) VisitExpression(item.Expression);
        foreach (var join in select.Joins)
            if (join.Predicate is not null) VisitExpression(join.Predicate);
    }

    private static void VisitExpression(SqlExpr expression)
    {
        switch (expression)
        {
            case SubqueryExpr subquery:
                ValidateSqlKataBackendCompatibility(subquery.Query);
                return;
            case ExistsExpr exists:
                ValidateSqlKataBackendCompatibility(exists.Query);
                return;
            case UnaryExpr unary:
                VisitExpression(unary.Operand);
                return;
            case BinaryExpr binary:
                VisitExpression(binary.Left);
                VisitExpression(binary.Right);
                return;
            case FunctionCallExpr function:
                foreach (var argument in function.Arguments) VisitExpression(argument);
                return;
            case FilterExpr filter:
                VisitExpression(filter.Expression);
                VisitExpression(filter.Predicate);
                return;
            case WindowedExpr windowed:
                VisitExpression(windowed.Expression);
                foreach (var partition in windowed.Window.PartitionBy) VisitExpression(partition);
                foreach (var item in windowed.Window.OrderBy) VisitExpression(item.Expression);
                return;
            case CastExpr cast:
                VisitExpression(cast.Expression);
                return;
            case CaseExpr @case:
                foreach (var branch in @case.Branches)
                {
                    VisitExpression(branch.Condition);
                    VisitExpression(branch.Value);
                }
                if (@case.ElseExpression is not null) VisitExpression(@case.ElseExpression);
                return;
            case InExpr @in:
                VisitExpression(@in.Value);
                foreach (var item in @in.Items) VisitExpression(item);
                return;
            case BetweenExpr between:
                VisitExpression(between.Value);
                VisitExpression(between.Lower);
                VisitExpression(between.Upper);
                return;
            case IsNullExpr isNull:
                VisitExpression(isNull.Value);
                return;
            case LiteralExpr or ColumnExpr or BoundColumnExpr or IntervalExpr:
                return;
            default:
                throw new SqlCompilationException(
                    $"Unsupported expression for SqlKata backend compatibility check: {expression.GetType().Name}");
        }
    }
}
