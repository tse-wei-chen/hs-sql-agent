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

        NormalizeLegacyDtoSurface(definition);
        var parsed = new ParsedStatement(QueryDefinitionCoreMapper.Map(definition), sourceDialect);
        var bound = _binder.Bind(parsed);
        var canonical = _normalizer.Normalize(bound, targetProvider);
        var validated = _validator.Validate(canonical, validationContext);
        var executable = _policyRewriter.Rewrite(validated, executionPolicy);
        ValidateSqlKataBackendCompatibility(executable.Statement);
        return new SqlKataProviderLowerer(targetProvider).Lower(executable);
    }

    /// <summary>
    /// Temporary strangler adapter for public DTO spellings that predate the canonical AST. It
    /// changes only equivalent spellings and internal template tokens; it performs no dialect
    /// inference and must stay ahead of the parser-independent mapper boundary.
    /// </summary>
    private static void NormalizeLegacyDtoSurface(QueryDefinition definition)
    {
        NormalizeWhereList(definition.WhereColumnsAndValues);
        if (definition.Joins is not null)
            foreach (var join in definition.Joins)
            {
                NormalizeWhereList(join.OnConditions);
                if (join.SubQuery is not null) NormalizeLegacyDtoSurface(join.SubQuery);
            }
        if (definition.SelectColumns is not null)
            NormalizeSelectList(definition.SelectColumns);
        if (definition.FromQuery is not null) NormalizeLegacyDtoSurface(definition.FromQuery);
        if (definition.CombineConditions is not null)
            foreach (var combine in definition.CombineConditions) NormalizeLegacyDtoSurface(combine.Query);
        if (definition.CteConditions is not null)
            foreach (var cte in definition.CteConditions) NormalizeLegacyDtoSurface(cte.Query);
    }

    private static void NormalizeWhereList(IReadOnlyList<WhereCondition>? conditions)
    {
        if (conditions is null) return;
        foreach (var condition in conditions)
        {
            switch (condition)
            {
                case BasicWhereCondition basic:
                    basic.Operator = NormalizePredicateAlias(basic.Operator);
                    break;
                case ColumnCompareWhereCondition compare:
                    compare.Operator = NormalizePredicateAlias(compare.Operator);
                    break;
                case ExpressionWhereCondition expression:
                    expression.Operator = NormalizePredicateAlias(expression.Operator);
                    NormalizeSelect(expression.LeftExpression);
                    if (expression.RightExpression is not null) NormalizeSelect(expression.RightExpression);
                    break;
                case GroupWhereCondition group:
                    NormalizeWhereList(group.Groups);
                    break;
                case SubQueryWhereCondition subquery:
                    subquery.Operator = NormalizePredicateAlias(subquery.Operator);
                    NormalizeLegacyDtoSurface(subquery.SubQuery);
                    break;
            }
        }
    }

    private static string NormalizePredicateAlias(string value)
    {
        var normalized = string.Join(' ', (value ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToUpperInvariant();
        return normalized switch
        {
            "ISNULL" => "IS",
            "ISNOTNULL" => "IS NOT",
            _ => value
        };
    }

    private static void NormalizeSelectList(IList<SelectCondition> conditions)
    {
        for (var i = 0; i < conditions.Count; i++)
            conditions[i] = NormalizeSelect(conditions[i]);
    }

    private static SelectCondition NormalizeSelect(SelectCondition condition)
    {
        switch (condition)
        {
            case FunctionSelectCondition function when function.Arguments is not null:
                NormalizeSelectList(function.Arguments);
                NormalizeWhereList(function.FilterWhereConditions);
                return function;
            case OperationSelectCondition operation:
                NormalizeSelect(operation.Left);
                NormalizeSelect(operation.Right);
                return operation;
            case CastSelectCondition cast:
                NormalizeSelect(cast.Expression);
                return cast;
            case SubQuerySelectCondition subquery:
                NormalizeLegacyDtoSurface(ToDefinition(subquery));
                return subquery;
            case TemplateSqlTokenSelectCondition token:
                return NormalizeTemplateToken(token);
            default:
                return condition;
        }
    }

    private static SelectCondition NormalizeTemplateToken(TemplateSqlTokenSelectCondition token)
    {
        var value = token.Token.Replace("_", string.Empty, StringComparison.Ordinal).Trim().ToUpperInvariant();
        return value switch
        {
            "CURRENTDATE" => new FunctionSelectCondition { FunctionName = "CURRENT_DATE", Alias = token.Alias },
            "CURRENTTIME" => new FunctionSelectCondition { FunctionName = "CURRENT_TIME", Alias = token.Alias },
            "CURRENTTIMESTAMP" => new FunctionSelectCondition { FunctionName = "CURRENT_TIMESTAMP", Alias = token.Alias },
            "SYSDATE" => new FunctionSelectCondition { FunctionName = "SYSDATE", Alias = token.Alias },
            "DAY" or "WEEK" or "MONTH" or "QUARTER" or "YEAR" or "HOUR" or "MINUTE" or "SECOND" =>
                new FieldSelectCondition { FieldName = value, Alias = token.Alias },
            _ => throw new SqlCompilationException($"Unsupported SQL template token '{token.Token}'.")
        };
    }

    private static QueryDefinition ToDefinition(SubQuerySelectCondition source) => new()
    {
        TableName = source.TableName,
        FromQuery = source.FromQuery,
        Alias = source.Alias,
        Distinct = source.Distinct,
        SelectColumns = source.SelectColumns,
        WhereColumnsAndValues = source.WhereColumnsAndValues,
        OrderByColumns = source.OrderByColumns,
        GroupByConditions = source.GroupByConditions,
        HavingConditions = source.HavingConditions,
        Joins = source.Joins,
        CombineConditions = source.CombineConditions,
        CteConditions = source.CteConditions,
        Limit = source.Limit,
        Offset = source.Offset
    };

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
