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
/// Single entry point for the strangler compiler pipeline. Callers receive a CompiledSqlCommand
/// only after mapping, binding, normalization, authorization/capability validation and policy
/// rewriting have all succeeded.
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

    public CompiledSqlCommand Compile(
        QueryDefinition definition,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetProvider,
        SqlPlanValidationContext validationContext,
        SqlExecutionPlanPolicy executionPolicy)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(validationContext);
        ArgumentNullException.ThrowIfNull(executionPolicy);

        var parsed = new ParsedStatement(
            QueryDefinitionCoreMapper.Map(definition),
            sourceDialect);
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
                // This SqlKata fork compiles ORDER/LIMIT before UNION/INTERSECT/EXCEPT. Applying
                // a query-level tail here would therefore change semantics. Reject until the
                // backend exposes a compound-query wrapper that can preserve the AST shape.
                if (!query.OrderBy.IsDefaultOrEmpty || query.Limit is > 0 || query.Offset is > 0)
                {
                    throw new SqlCompilationException(
                        "Query-level ORDER BY/LIMIT/OFFSET after a set operation is not supported " +
                        "by the current SqlKata backend; the plan was rejected to preserve semantics.");
                }
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
        foreach (var item in select.Select)
            VisitExpression(item.Expression);
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
