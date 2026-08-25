using System.Collections.Immutable;
using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Core.Binding;
using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;

namespace SqlAgent.Service.Core.Analysis;

/// <summary>
/// Resolves source-session-dependent syntax into an internal canonical marker before normalization,
/// then restores the canonical Core operator after source-specific normalization has completed.
/// Undeclared session semantics remain fail-closed in the ordinary normalizer.
/// </summary>
internal static class CoreSourceProfileRewriter
{
    private const string MySqlPipesConcatMarker = "__CORE_MYSQL_PIPES_AS_CONCAT__";

    public static void ValidateProfile(
        SqlAgentToolType sourceDialect,
        SqlProviderCapabilityProfile? sourceProfile)
    {
        if (sourceProfile is null) return;
        if (sourceProfile.Provider != sourceDialect)
        {
            throw new SqlCompilationException(
                $"Source capability profile declares provider {sourceProfile.Provider}, " +
                $"but parsed SQL declares source dialect {sourceDialect}.");
        }
        if (sourceProfile.CompatibilityLevel is < 0)
            throw new SqlCompilationException("Provider compatibility level must be non-negative.");
    }

    public static bool SupportsMySqlPipesAsConcat(
        SqlAgentToolType sourceDialect,
        SqlProviderCapabilityProfile? sourceProfile) =>
        sourceDialect == SqlAgentToolType.MySQL
        && sourceProfile is { Provider: SqlAgentToolType.MySQL }
        && (sourceProfile.HasSessionMode("PIPES_AS_CONCAT")
            || sourceProfile.HasSessionMode("ANSI"));

    public static SqlStatement Prepare(
        SqlStatement statement,
        SqlAgentToolType sourceDialect,
        SqlProviderCapabilityProfile? sourceProfile)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ValidateProfile(sourceDialect, sourceProfile);

        if (!SupportsMySqlPipesAsConcat(sourceDialect, sourceProfile))
            return statement;

        return RewriteStatement(
            statement,
            static op => op == "||" ? MySqlPipesConcatMarker : op);
    }

    public static SqlStatement Restore(SqlStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return RewriteStatement(
            statement,
            static op => op.Equals(MySqlPipesConcatMarker, StringComparison.OrdinalIgnoreCase)
                ? "||"
                : op);
    }

    private static SqlStatement RewriteStatement(
        SqlStatement statement,
        Func<string, string> rewriteOperator) => statement switch
    {
        SelectStatement select => RewriteSelect(select, rewriteOperator),
        QueryStatement query => query with
        {
            Head = RewriteSelect(query.Head, rewriteOperator),
            SetOperations = query.SetOperations
                .Select(operation => operation with
                {
                    Query = RewriteStatement(operation.Query, rewriteOperator)
                })
                .ToImmutableArray(),
            OrderBy = RewriteOrderBy(query.OrderBy, rewriteOperator)
        },
        InsertStatement insert => insert with
        {
            Source = insert.Source switch
            {
                InsertValuesSource values => values with
                {
                    Rows = values.Rows
                        .Select(row => row
                            .Select(value => RewriteExpression(value, rewriteOperator))
                            .ToImmutableArray())
                        .ToImmutableArray()
                },
                InsertQuerySource querySource => querySource with
                {
                    Query = RewriteStatement(querySource.Query, rewriteOperator)
                },
                _ => throw new SqlCompilationException(
                    $"Unsupported INSERT source during source-profile rewrite: {insert.Source.GetType().Name}")
            }
        },
        UpdateStatement update => update with
        {
            Assignments = update.Assignments
                .Select(assignment => assignment with
                {
                    Value = RewriteExpression(assignment.Value, rewriteOperator)
                })
                .ToImmutableArray(),
            Predicate = update.Predicate is null
                ? null
                : RewriteExpression(update.Predicate, rewriteOperator)
        },
        DeleteStatement delete => delete with
        {
            Predicate = delete.Predicate is null
                ? null
                : RewriteExpression(delete.Predicate, rewriteOperator)
        },
        _ => throw new SqlCompilationException(
            $"Unsupported statement during source-profile rewrite: {statement.GetType().Name}")
    };

    private static SelectStatement RewriteSelect(
        SelectStatement select,
        Func<string, string> rewriteOperator) => select with
    {
        Ctes = select.Ctes
            .Select(cte => cte with
            {
                Query = RewriteStatement(cte.Query, rewriteOperator)
            })
            .ToImmutableArray(),
        Select = select.Select
            .Select(item => item with
            {
                Expression = RewriteExpression(item.Expression, rewriteOperator)
            })
            .ToImmutableArray(),
        From = select.From is null
            ? null
            : RewriteSource(select.From, rewriteOperator),
        Joins = select.Joins
            .Select(join => join with
            {
                Source = RewriteSource(join.Source, rewriteOperator),
                Predicate = join.Predicate is null
                    ? null
                    : RewriteExpression(join.Predicate, rewriteOperator)
            })
            .ToImmutableArray(),
        Where = select.Where is null
            ? null
            : RewriteExpression(select.Where, rewriteOperator),
        GroupBy = select.GroupBy
            .Select(expression => RewriteExpression(expression, rewriteOperator))
            .ToImmutableArray(),
        Having = select.Having is null
            ? null
            : RewriteExpression(select.Having, rewriteOperator),
        OrderBy = RewriteOrderBy(select.OrderBy, rewriteOperator)
    };

    private static TableSource RewriteSource(
        TableSource source,
        Func<string, string> rewriteOperator) => source switch
    {
        NamedTableSource => source,
        DerivedTableSource derived => derived with
        {
            Query = RewriteStatement(derived.Query, rewriteOperator)
        },
        _ => throw new SqlCompilationException(
            $"Unsupported table source during source-profile rewrite: {source.GetType().Name}")
    };

    private static ImmutableArray<OrderByItem> RewriteOrderBy(
        ImmutableArray<OrderByItem> orderBy,
        Func<string, string> rewriteOperator) => orderBy
        .Select(item => item with
        {
            Expression = RewriteExpression(item.Expression, rewriteOperator)
        })
        .ToImmutableArray();

    private static SqlExpr RewriteExpression(
        SqlExpr expression,
        Func<string, string> rewriteOperator) => expression switch
    {
        LiteralExpr or ColumnExpr or BoundColumnExpr or IntervalExpr => expression,
        UnaryExpr unary => unary with
        {
            Operand = RewriteExpression(unary.Operand, rewriteOperator)
        },
        BinaryExpr binary => binary with
        {
            Left = RewriteExpression(binary.Left, rewriteOperator),
            Operator = rewriteOperator(binary.Operator),
            Right = RewriteExpression(binary.Right, rewriteOperator)
        },
        FunctionCallExpr function => function with
        {
            Arguments = function.Arguments
                .Select(argument => RewriteExpression(argument, rewriteOperator))
                .ToImmutableArray()
        },
        FilterExpr filter => filter with
        {
            Expression = RewriteExpression(filter.Expression, rewriteOperator),
            Predicate = RewriteExpression(filter.Predicate, rewriteOperator)
        },
        WindowedExpr windowed => windowed with
        {
            Expression = RewriteExpression(windowed.Expression, rewriteOperator),
            Window = windowed.Window with
            {
                PartitionBy = windowed.Window.PartitionBy
                    .Select(item => RewriteExpression(item, rewriteOperator))
                    .ToImmutableArray(),
                OrderBy = RewriteOrderBy(windowed.Window.OrderBy, rewriteOperator)
            }
        },
        CastExpr cast => cast with
        {
            Expression = RewriteExpression(cast.Expression, rewriteOperator)
        },
        SimpleCaseExpr simpleCase => new SimpleCaseExpr(
            RewriteBranches(simpleCase.Branches, rewriteOperator),
            simpleCase.ElseExpression is null
                ? null
                : RewriteExpression(simpleCase.ElseExpression, rewriteOperator),
            simpleCase.Span),
        CaseExpr @case => @case with
        {
            Branches = RewriteBranches(@case.Branches, rewriteOperator),
            ElseExpression = @case.ElseExpression is null
                ? null
                : RewriteExpression(@case.ElseExpression, rewriteOperator)
        },
        InExpr @in => @in with
        {
            Value = RewriteExpression(@in.Value, rewriteOperator),
            Items = @in.Items
                .Select(item => RewriteExpression(item, rewriteOperator))
                .ToImmutableArray()
        },
        BetweenExpr between => between with
        {
            Value = RewriteExpression(between.Value, rewriteOperator),
            Lower = RewriteExpression(between.Lower, rewriteOperator),
            Upper = RewriteExpression(between.Upper, rewriteOperator)
        },
        IsNullExpr isNull => isNull with
        {
            Value = RewriteExpression(isNull.Value, rewriteOperator)
        },
        SubqueryExpr subquery => subquery with
        {
            Query = RewriteStatement(subquery.Query, rewriteOperator)
        },
        ExistsExpr exists => exists with
        {
            Query = RewriteStatement(exists.Query, rewriteOperator)
        },
        _ => throw new SqlCompilationException(
            $"Unsupported expression during source-profile rewrite: {expression.GetType().Name}")
    };

    private static ImmutableArray<CaseBranch> RewriteBranches(
        ImmutableArray<CaseBranch> branches,
        Func<string, string> rewriteOperator) => branches
        .Select(branch => branch with
        {
            Condition = RewriteExpression(branch.Condition, rewriteOperator),
            Value = RewriteExpression(branch.Value, rewriteOperator)
        })
        .ToImmutableArray();
}
