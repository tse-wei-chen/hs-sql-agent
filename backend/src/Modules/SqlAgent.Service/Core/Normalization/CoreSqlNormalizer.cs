using System.Collections.Immutable;
using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Core.Binding;
using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;
using SqlAgent.Service.SqlTranslation.Functions;

namespace SqlAgent.Service.Core.Normalization;

/// <summary>
/// Canonicalizes the bound Core AST with explicit source/target dialect context. No ambient state
/// is used. Function translations that still require the legacy template/specialized adapters fail
/// closed until their Core AST translators are implemented.
/// </summary>
public sealed class CoreSqlNormalizer(IFunctionRegistry functionRegistry) : ISqlNormalizer
{
    private readonly IFunctionRegistry _functionRegistry = functionRegistry;

    public static CoreSqlNormalizer CreateDefault() =>
        new(new FunctionRegistry(FunctionDefinitionLoader.LoadEmbedded()));

    public CanonicalStatement Normalize(BoundStatement statement, SqlAgentToolType targetProvider)
    {
        ArgumentNullException.ThrowIfNull(statement);
        var context = new NormalizationContext(statement.SourceDialect, targetProvider);
        var normalized = NormalizeStatement(statement.Statement, context);
        return new CanonicalStatement(
            normalized,
            statement.Facts,
            statement.SourceDialect,
            targetProvider);
    }

    private SqlStatement NormalizeStatement(SqlStatement statement, NormalizationContext context) =>
        statement switch
        {
            SelectStatement select => NormalizeSelect(select, context),
            QueryStatement query => query with
            {
                Head = NormalizeSelect(query.Head, context),
                SetOperations = query.SetOperations
                    .Select(operation => operation with
                    {
                        Query = NormalizeStatement(operation.Query, context)
                    })
                    .ToImmutableArray(),
                OrderBy = NormalizeOrderBy(query.OrderBy, context)
            },
            _ => throw new SqlCompilationException(
                $"Unsupported statement during normalization: {statement.GetType().Name}")
        };

    private SelectStatement NormalizeSelect(SelectStatement select, NormalizationContext context) =>
        select with
        {
            Ctes = select.Ctes
                .Select(cte => cte with
                {
                    Query = NormalizeStatement(cte.Query, context)
                })
                .ToImmutableArray(),
            Select = select.Select
                .Select(item => item with { Expression = NormalizeExpr(item.Expression, context) })
                .ToImmutableArray(),
            From = select.From is null ? null : NormalizeSource(select.From, context),
            Joins = select.Joins
                .Select(join => join with
                {
                    Kind = join.Kind.Trim().ToUpperInvariant(),
                    Source = NormalizeSource(join.Source, context),
                    Predicate = join.Predicate is null ? null : NormalizeExpr(join.Predicate, context)
                })
                .ToImmutableArray(),
            Where = select.Where is null ? null : NormalizeExpr(select.Where, context),
            GroupBy = select.GroupBy
                .Select(expression => NormalizeExpr(expression, context))
                .ToImmutableArray(),
            Having = select.Having is null ? null : NormalizeExpr(select.Having, context),
            OrderBy = NormalizeOrderBy(select.OrderBy, context)
        };

    private TableSource NormalizeSource(TableSource source, NormalizationContext context) =>
        source switch
        {
            NamedTableSource named => named,
            DerivedTableSource derived => derived with
            {
                Query = NormalizeStatement(derived.Query, context)
            },
            _ => throw new SqlCompilationException(
                $"Unsupported table source during normalization: {source.GetType().Name}")
        };

    private ImmutableArray<OrderByItem> NormalizeOrderBy(
        ImmutableArray<OrderByItem> orderBy,
        NormalizationContext context) =>
        orderBy.Select(item => item with
            {
                Expression = NormalizeExpr(item.Expression, context)
            })
            .ToImmutableArray();

    private SqlExpr NormalizeExpr(SqlExpr expression, NormalizationContext context)
    {
        return expression switch
        {
            LiteralExpr literal => literal,
            IntervalExpr interval => interval,
            BoundColumnExpr column => column,
            ColumnExpr column => column,
            UnaryExpr unary => unary with
            {
                Operator = NormalizeOperator(unary.Operator),
                Operand = NormalizeExpr(unary.Operand, context)
            },
            BinaryExpr binary => binary with
            {
                Left = NormalizeExpr(binary.Left, context),
                Operator = NormalizeOperator(binary.Operator),
                Right = NormalizeExpr(binary.Right, context)
            },
            FunctionCallExpr function => NormalizeFunction(function, context),
            CastExpr cast => cast with
            {
                Expression = NormalizeExpr(cast.Expression, context),
                TypeName = cast.TypeName.Trim()
            },
            CaseExpr @case => @case with
            {
                Branches = @case.Branches.Select(branch => new CaseBranch(
                        NormalizeExpr(branch.Condition, context),
                        NormalizeExpr(branch.Value, context)))
                    .ToImmutableArray(),
                ElseExpression = @case.ElseExpression is null
                    ? null
                    : NormalizeExpr(@case.ElseExpression, context)
            },
            InExpr @in => @in with
            {
                Value = NormalizeExpr(@in.Value, context),
                Items = @in.Items.Select(item => NormalizeExpr(item, context)).ToImmutableArray()
            },
            BetweenExpr between => between with
            {
                Value = NormalizeExpr(between.Value, context),
                Lower = NormalizeExpr(between.Lower, context),
                Upper = NormalizeExpr(between.Upper, context)
            },
            IsNullExpr isNull => isNull with
            {
                Value = NormalizeExpr(isNull.Value, context)
            },
            SubqueryExpr subquery => subquery with
            {
                Query = NormalizeStatement(subquery.Query, context)
            },
            ExistsExpr exists => exists with
            {
                Query = NormalizeStatement(exists.Query, context)
            },
            _ => throw new SqlCompilationException(
                $"Unsupported expression during normalization: {expression.GetType().Name}")
        };
    }

    private FunctionCallExpr NormalizeFunction(
        FunctionCallExpr function,
        NormalizationContext context)
    {
        var arguments = function.Arguments
            .Select(argument => NormalizeExpr(argument, context))
            .ToImmutableArray();
        var sourceName = IdentifierText(function.Name).Trim().ToUpperInvariant();

        if (context.SourceDialect == context.TargetProvider)
            return function with { Name = Identifier(sourceName), Arguments = arguments };

        var sourceDefinition = _functionRegistry.Find(
            context.SourceDialect,
            sourceName,
            arguments.Length);
        if (sourceDefinition is null)
        {
            throw new SqlCompilationException(
                $"Function '{sourceName}' is not registered for source dialect {context.SourceDialect}; " +
                "cross-dialect normalization was rejected.");
        }

        if (sourceDefinition.Semantic is null)
        {
            throw new SqlCompilationException(
                $"Function '{sourceName}' has no portable semantic mapping from {context.SourceDialect}.");
        }

        var targetDefinition = _functionRegistry.Find(
            context.TargetProvider,
            sourceDefinition.Semantic.Value,
            arguments.Length);
        if (targetDefinition is null)
        {
            throw new SqlCompilationException(
                $"Semantic function '{sourceDefinition.Semantic}' with {arguments.Length} argument(s) " +
                $"is not supported by {context.TargetProvider}.");
        }

        if (targetDefinition.TranslationKind is FunctionTranslationKind.Template
            or FunctionTranslationKind.Specialized)
        {
            throw new SqlCompilationException(
                $"Function '{sourceName}' requires Core {targetDefinition.TranslationKind} translation " +
                $"for target provider {context.TargetProvider}; no lossless Core translator is registered yet.");
        }

        return function with
        {
            Name = Identifier(targetDefinition.Name.Trim().ToUpperInvariant()),
            Arguments = arguments
        };
    }

    private static string NormalizeOperator(string value)
    {
        var normalized = string.Join(' ', value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToUpperInvariant();
        return normalized switch
        {
            "!=" => "<>",
            "NOTIN" => "NOT IN",
            "NOTBETWEEN" => "NOT BETWEEN",
            "NOTEXISTS" => "NOT EXISTS",
            _ => normalized
        };
    }

    private static SqlIdentifier Identifier(string name) =>
        new([new IdentifierPart(name, false, SourceSpan.Unknown)], SourceSpan.Unknown);

    private static string IdentifierText(SqlIdentifier identifier) =>
        string.Join('.', identifier.Parts.Select(part => part.Value));

    private sealed record NormalizationContext(
        SqlAgentToolType SourceDialect,
        SqlAgentToolType TargetProvider);
}
