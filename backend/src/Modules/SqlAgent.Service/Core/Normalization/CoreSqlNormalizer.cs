using System.Collections.Immutable;
using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Core.Binding;
using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;
using SqlAgent.Service.SqlTranslation.DateFormats;
using SqlAgent.Service.SqlTranslation.Functions;

namespace SqlAgent.Service.Core.Normalization;

/// <summary>
/// Canonicalizes the bound Core AST with explicit source/target dialect context. No ambient state
/// is used. Cross-dialect function families are reduced to a small set of canonical semantic
/// function names; provider syntax is selected only by the provider lowerer.
/// </summary>
public sealed class CoreSqlNormalizer(IFunctionRegistry functionRegistry) : ISqlNormalizer
{
    private static readonly DateFormatTranslator DateFormats = new();
    private static readonly HashSet<string> PortableFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "ABS", "AVG", "COUNT", "MAX", "MIN", "ROUND", "SUM",
        "LOWER", "UPPER", "TRIM", "LTRIM", "RTRIM", "COALESCE", "NULLIF"
    };

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
                SetOperations = query.SetOperations.Select(operation => operation with
                {
                    Query = NormalizeStatement(operation.Query, context)
                }).ToImmutableArray(),
                OrderBy = NormalizeOrderBy(query.OrderBy, context)
            },
            UpdateStatement update => update with
            {
                Assignments = update.Assignments.Select(assignment => assignment with
                {
                    Value = NormalizeExpr(assignment.Value, context)
                }).ToImmutableArray(),
                Predicate = update.Predicate is null ? null : NormalizeExpr(update.Predicate, context)
            },
            DeleteStatement delete => delete with
            {
                Predicate = delete.Predicate is null ? null : NormalizeExpr(delete.Predicate, context)
            },
            _ => throw new SqlCompilationException(
                $"Unsupported statement during normalization: {statement.GetType().Name}")
        };

    private SelectStatement NormalizeSelect(SelectStatement select, NormalizationContext context) =>
        select with
        {
            Ctes = select.Ctes.Select(cte => cte with
            {
                Query = NormalizeStatement(cte.Query, context)
            }).ToImmutableArray(),
            Select = select.Select.Select(item => item with
            {
                Expression = NormalizeExpr(item.Expression, context)
            }).ToImmutableArray(),
            From = select.From is null ? null : NormalizeSource(select.From, context),
            Joins = select.Joins.Select(join => join with
            {
                Kind = join.Kind.Trim().ToUpperInvariant(),
                Source = NormalizeSource(join.Source, context),
                Predicate = join.Predicate is null ? null : NormalizeExpr(join.Predicate, context)
            }).ToImmutableArray(),
            Where = select.Where is null ? null : NormalizeExpr(select.Where, context),
            GroupBy = select.GroupBy.Select(expression => NormalizeExpr(expression, context)).ToImmutableArray(),
            Having = select.Having is null ? null : NormalizeExpr(select.Having, context),
            OrderBy = NormalizeOrderBy(select.OrderBy, context)
        };

    private TableSource NormalizeSource(TableSource source, NormalizationContext context) =>
        source switch
        {
            NamedTableSource named => named,
            DerivedTableSource derived => derived with { Query = NormalizeStatement(derived.Query, context) },
            _ => throw new SqlCompilationException(
                $"Unsupported table source during normalization: {source.GetType().Name}")
        };

    private ImmutableArray<OrderByItem> NormalizeOrderBy(
        ImmutableArray<OrderByItem> orderBy,
        NormalizationContext context) =>
        orderBy.Select(item => item with
        {
            Expression = NormalizeExpr(item.Expression, context)
        }).ToImmutableArray();

    private WindowSpec NormalizeWindow(WindowSpec window, NormalizationContext context) =>
        window with
        {
            PartitionBy = window.PartitionBy.Select(expression => NormalizeExpr(expression, context)).ToImmutableArray(),
            OrderBy = NormalizeOrderBy(window.OrderBy, context)
        };

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
            FilterExpr filter => filter with
            {
                Expression = NormalizeExpr(filter.Expression, context),
                Predicate = NormalizeExpr(filter.Predicate, context)
            },
            WindowedExpr windowed => windowed with
            {
                Expression = NormalizeExpr(windowed.Expression, context),
                Window = NormalizeWindow(windowed.Window, context)
            },
            CastExpr cast => cast with
            {
                Expression = NormalizeExpr(cast.Expression, context),
                TypeName = cast.TypeName.Trim()
            },
            CaseExpr @case => @case with
            {
                Branches = @case.Branches.Select(branch => new CaseBranch(
                    NormalizeExpr(branch.Condition, context),
                    NormalizeExpr(branch.Value, context))).ToImmutableArray(),
                ElseExpression = @case.ElseExpression is null ? null : NormalizeExpr(@case.ElseExpression, context)
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
            IsNullExpr isNull => isNull with { Value = NormalizeExpr(isNull.Value, context) },
            SubqueryExpr subquery => subquery with { Query = NormalizeStatement(subquery.Query, context) },
            ExistsExpr exists => exists with { Query = NormalizeStatement(exists.Query, context) },
            _ => throw new SqlCompilationException(
                $"Unsupported expression during normalization: {expression.GetType().Name}")
        };
    }

    private SqlExpr NormalizeFunction(FunctionCallExpr function, NormalizationContext context)
    {
        var arguments = function.Arguments.Select(argument => NormalizeExpr(argument, context)).ToImmutableArray();
        var sourceName = IdentifierText(function.Name).Trim().ToUpperInvariant();

        var specialized = NormalizePortableFamily(function, sourceName, arguments, context);
        if (specialized is not null)
            return specialized;

        if (PortableFunctions.Contains(sourceName))
            return function with { Name = Identifier(sourceName), Arguments = arguments };

        var sourceDefinition = _functionRegistry.Find(context.SourceDialect, sourceName, arguments.Length);
        if (sourceDefinition is null)
        {
            throw new SqlCompilationException(
                $"Function '{sourceName}' is not registered for source dialect {context.SourceDialect}; normalization was rejected.");
        }
        if (sourceDefinition.Semantic is null)
            throw new SqlCompilationException($"Function '{sourceName}' has no portable semantic mapping from {context.SourceDialect}.");

        var targetDefinition = _functionRegistry.Find(context.TargetProvider, sourceDefinition.Semantic.Value, arguments.Length);
        if (targetDefinition is null)
        {
            throw new SqlCompilationException(
                $"Semantic function '{sourceDefinition.Semantic}' with {arguments.Length} argument(s) is not supported by {context.TargetProvider}.");
        }
        if (targetDefinition.TranslationKind is FunctionTranslationKind.Template or FunctionTranslationKind.Specialized)
        {
            throw new SqlCompilationException(
                $"Function '{sourceName}' requires Core {targetDefinition.TranslationKind} translation for target provider {context.TargetProvider}; no lossless Core translator is registered yet.");
        }

        return function with
        {
            Name = Identifier(targetDefinition.Name.Trim().ToUpperInvariant()),
            Arguments = arguments
        };
    }

    private static SqlExpr? NormalizePortableFamily(
        FunctionCallExpr original,
        string sourceName,
        ImmutableArray<SqlExpr> arguments,
        NormalizationContext context)
    {
        return sourceName switch
        {
            "DATEADD" => CanonicalDateAdd(original, arguments),
            "DATEDIFF" => CanonicalDateDiff(original, arguments),
            "YEAR" or "MONTH" or "DAY" => CanonicalDatePart(original, sourceName, arguments),
            "DATE_FORMAT" or "FORMAT" => CanonicalDateFormat(original, arguments, context),
            "TO_DATE" => CanonicalDateParse(original, arguments, context),
            "CHARINDEX" or "LOCATE" or "STRPOS" or "INSTR" => CanonicalPosition(original, sourceName, arguments),
            "JSON_EXTRACT" => CanonicalFunction(original, "CORE_JSON_EXTRACT", arguments),
            "JSON_SET" => CanonicalFunction(original, "CORE_JSON_SET", arguments),
            "REGEXP_LIKE" => CanonicalFunction(original, "CORE_REGEX_MATCH", arguments),
            "GETDATE" or "NOW" or "SYSDATE" or "CURRENT_TIMESTAMP" =>
                arguments.Length == 0
                    ? CanonicalFunction(original, "CORE_CURRENT_TIMESTAMP", arguments)
                    : throw new SqlCompilationException($"{sourceName} does not accept arguments."),
            "STRING_AGG" or "GROUP_CONCAT" or "LISTAGG" or "LIST" =>
                CanonicalStringAggregate(original, arguments),
            _ => null
        };
    }

    private static SqlExpr CanonicalDateAdd(FunctionCallExpr original, ImmutableArray<SqlExpr> arguments)
    {
        if (arguments.Length != 3)
            throw new SqlCompilationException("DATEADD requires exactly 3 arguments.");
        var unit = DatePartUnit(arguments[0]);
        return CanonicalFunction(original, "CORE_DATE_ADD",
            [new LiteralExpr(unit, original.Span), arguments[1], arguments[2]]);
    }

    private static SqlExpr CanonicalDateDiff(FunctionCallExpr original, ImmutableArray<SqlExpr> arguments)
    {
        if (arguments.Length == 2)
            return CanonicalFunction(original, "CORE_DATE_DIFF",
                [new LiteralExpr("DAY", original.Span), arguments[1], arguments[0]]);
        if (arguments.Length != 3)
            throw new SqlCompilationException("DATEDIFF requires 2 or 3 arguments.");
        var unit = DatePartUnit(arguments[0]);
        return CanonicalFunction(original, "CORE_DATE_DIFF",
            [new LiteralExpr(unit, original.Span), arguments[1], arguments[2]]);
    }

    private static SqlExpr CanonicalDatePart(
        FunctionCallExpr original,
        string part,
        ImmutableArray<SqlExpr> arguments)
    {
        if (arguments.Length != 1)
            throw new SqlCompilationException($"{part} requires exactly 1 argument.");
        return CanonicalFunction(original, "CORE_DATE_PART",
            [new LiteralExpr(part, original.Span), arguments[0]]);
    }

    private static SqlExpr CanonicalDateFormat(
        FunctionCallExpr original,
        ImmutableArray<SqlExpr> arguments,
        NormalizationContext context)
    {
        if (arguments.Length != 2)
            throw new SqlCompilationException("DATE_FORMAT/FORMAT requires exactly 2 arguments.");
        var sourceFormat = LiteralString(arguments[1], "DATE_FORMAT format");
        string translated;
        try
        {
            translated = DateFormats.Translate(sourceFormat, context.SourceDialect, context.TargetProvider);
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException)
        {
            throw new SqlCompilationException(
                $"portable date formatting from {context.SourceDialect} to {context.TargetProvider} is not supported: {ex.Message}", ex);
        }
        return CanonicalFunction(original, "CORE_DATE_FORMAT",
            [arguments[0], new LiteralExpr(translated, original.Span)]);
    }

    private static SqlExpr CanonicalDateParse(
        FunctionCallExpr original,
        ImmutableArray<SqlExpr> arguments,
        NormalizationContext context)
    {
        if (arguments.Length != 2)
            throw new SqlCompilationException("TO_DATE requires exactly 2 arguments.");
        var sourceFormat = LiteralString(arguments[1], "TO_DATE format");
        string translated;
        try
        {
            translated = DateFormats.Translate(sourceFormat, context.SourceDialect, context.TargetProvider);
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException)
        {
            throw new SqlCompilationException(
                $"formatted date parsing from {context.SourceDialect} to {context.TargetProvider} is not supported: {ex.Message}", ex);
        }
        return CanonicalFunction(original, "CORE_DATE_PARSE",
            [arguments[0], new LiteralExpr(translated, original.Span)]);
    }

    private static SqlExpr CanonicalPosition(
        FunctionCallExpr original,
        string sourceName,
        ImmutableArray<SqlExpr> arguments)
    {
        if (arguments.Length != 2)
            throw new SqlCompilationException($"{sourceName} requires exactly 2 arguments.");
        var haystackFirst = sourceName is "STRPOS" or "INSTR";
        return CanonicalFunction(original, "CORE_POSITION",
            haystackFirst
                ? [arguments[0], arguments[1]]
                : [arguments[1], arguments[0]]);
    }

    private static SqlExpr CanonicalStringAggregate(
        FunctionCallExpr original,
        ImmutableArray<SqlExpr> arguments)
    {
        if (arguments.Length is < 1 or > 2)
            throw new SqlCompilationException("String aggregate requires 1 or 2 arguments.");
        var normalized = arguments.Length == 1
            ? ImmutableArray.Create(arguments[0], (SqlExpr)new LiteralExpr(",", original.Span))
            : arguments;
        return CanonicalFunction(original, "CORE_STRING_AGG", normalized);
    }

    private static FunctionCallExpr CanonicalFunction(
        FunctionCallExpr original,
        string name,
        IEnumerable<SqlExpr> arguments) =>
        original with
        {
            Name = Identifier(name),
            Arguments = arguments.ToImmutableArray()
        };

    private static string DatePartUnit(SqlExpr expression)
    {
        var unit = expression switch
        {
            BoundColumnExpr column => IdentifierText(column.Name),
            ColumnExpr column => IdentifierText(column.Name),
            LiteralExpr { Value: string value } => value,
            _ => throw new SqlCompilationException(
                "DATEADD/DATEDIFF date-part unit must be an unquoted SQL keyword.")
        };
        return unit.Trim().ToUpperInvariant() switch
        {
            "DAY" or "DD" or "D" => "DAY",
            "WEEK" or "WK" or "WW" => "WEEK",
            "MONTH" or "MM" or "M" => "MONTH",
            "QUARTER" or "QQ" or "Q" => "QUARTER",
            "YEAR" or "YY" or "YYYY" => "YEAR",
            "HOUR" or "HH" => "HOUR",
            "MINUTE" or "MI" or "N" => "MINUTE",
            "SECOND" or "SS" or "S" => "SECOND",
            _ => throw new SqlCompilationException($"Unsupported DATEADD/DATEDIFF date-part unit '{unit}'.")
        };
    }

    private static string LiteralString(SqlExpr expression, string label) =>
        expression is LiteralExpr { Value: string value }
            ? value
            : throw new SqlCompilationException($"{label} must be a string literal.");

    private static string NormalizeOperator(string value)
    {
        var normalized = string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToUpperInvariant();
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

    private sealed record NormalizationContext(SqlAgentToolType SourceDialect, SqlAgentToolType TargetProvider);
}
