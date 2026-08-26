using System.Collections.Immutable;
using HsSqlAgent.SqlCore.Core.Ast;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Models;

namespace HsSqlAgent.SqlCore.Core.Mapping;

/// <summary>
/// Strangler adapter from the public QueryDefinition contract into the independent Core AST.
/// It intentionally fails closed for legacy shapes that the Core AST cannot yet preserve.
/// Mapping is pure: legacy-equivalent spellings are normalized while constructing Core nodes and
/// the supplied transport DTO is never rewritten in place.
/// </summary>
public static class QueryDefinitionCoreMapper
{
    private static readonly SourceSpan Unknown = SourceSpan.Unknown;

    public static SqlStatement Map(QueryDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var select = MapSelectStatement(definition, includeTail: definition.CombineConditions is null or { Count: 0 });
        if (definition.CombineConditions is not { Count: > 0 })
            return select;

        var operations = definition.CombineConditions
            .Select(c => new SetOperation(
                MapSetOperation(c.Type),
                Map(c.Query),
                Unknown))
            .ToImmutableArray();

        return new QueryStatement(
            select,
            operations,
            MapOrderBy(definition.OrderByColumns),
            definition.Limit,
            definition.Offset,
            Unknown);
    }

    private static SelectStatement MapSelectStatement(QueryDefinition definition, bool includeTail)
    {
        var ctes = definition.CteConditions?.Select(c => new CteDefinition(
                Identifier(c.CteAliasName),
                ImmutableArray<SqlIdentifier>.Empty,
                Map(c.Query),
                Unknown))
            .ToImmutableArray() ?? ImmutableArray<CteDefinition>.Empty;

        var from = MapSource(definition);
        var joins = definition.Joins?.Select(MapJoin).ToImmutableArray()
            ?? ImmutableArray<JoinSource>.Empty;

        return new SelectStatement(
            ctes,
            definition.Distinct,
            definition.SelectColumns?.Select(MapSelectItem).ToImmutableArray()
                ?? ImmutableArray<SelectItem>.Empty,
            from,
            joins,
            MapWhereList(definition.WhereColumnsAndValues),
            definition.GroupByConditions?.Select(MapGroupBy).ToImmutableArray()
                ?? ImmutableArray<SqlExpr>.Empty,
            MapHavingList(definition.HavingConditions),
            includeTail ? MapOrderBy(definition.OrderByColumns) : ImmutableArray<OrderByItem>.Empty,
            includeTail ? definition.Limit : null,
            includeTail ? definition.Offset : null,
            Unknown);
    }

    private static TableSource? MapSource(QueryDefinition definition)
    {
        if (definition.FromQuery is not null)
        {
            var alias = definition.Alias ?? definition.FromQuery.Alias;
            if (string.IsNullOrWhiteSpace(alias))
                throw new InvalidOperationException("A derived table must have an explicit alias in the Core AST.");
            return new DerivedTableSource(Map(definition.FromQuery), alias.Trim(), Unknown);
        }

        if (string.IsNullOrWhiteSpace(definition.TableName))
            return null;

        return new NamedTableSource(Identifier(definition.TableName), NormalizeAlias(definition.Alias), Unknown);
    }

    private static JoinSource MapJoin(JoinCondition join)
    {
        TableSource source;
        if (join.SubQuery is not null)
        {
            if (string.IsNullOrWhiteSpace(join.Alias))
                throw new InvalidOperationException("A joined derived table must have an explicit alias in the Core AST.");
            source = new DerivedTableSource(Map(join.SubQuery), join.Alias.Trim(), Unknown);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(join.Table))
                throw new InvalidOperationException("JOIN must specify either a table or a subquery.");
            source = new NamedTableSource(Identifier(join.Table), NormalizeAlias(join.Alias), Unknown);
        }

        var predicate = MapWhereList(join.OnConditions);
        if (join.Type != JoinType.Cross && predicate is null)
            throw new InvalidOperationException($"{join.Type} JOIN requires an ON predicate.");
        if (join.Type == JoinType.Cross && predicate is not null)
            throw new InvalidOperationException("CROSS JOIN must not carry an ON predicate.");

        return new JoinSource(join.Type.ToString().ToUpperInvariant(), source, predicate, Unknown);
    }

    private static SelectItem MapSelectItem(SelectCondition condition) =>
        new(MapExpr(condition), NormalizeAlias(condition.Alias), Unknown);

    private static SqlExpr MapExpr(SelectCondition condition)
    {
        return condition switch
        {
            FieldSelectCondition field => new ColumnExpr(Identifier(field.FieldName), Unknown),
            ConstantSelectCondition constant => new LiteralExpr(constant.Constant, Unknown),
            OperationSelectCondition operation => new BinaryExpr(
                MapExpr(operation.Left),
                MapOperator(operation.Operator),
                MapExpr(operation.Right),
                Unknown),
            FunctionSelectCondition function => MapFunction(
                function.FunctionName,
                function.Arguments,
                function.IsDistinct,
                function.FilterWhereConditions,
                function.Window),
            CastSelectCondition cast => new CastExpr(MapExpr(cast.Expression), cast.TypeName, Unknown),
            IntervalSelectCondition interval => new IntervalExpr(interval.Literal, Unknown),
            CaseWhenSelectCondition @case => new CaseExpr(
                @case.CaseWhen.Select(c => new CaseBranch(
                        MapWhere(c.Condition),
                        new LiteralExpr(c.Value, Unknown)))
                    .ToImmutableArray(),
                @case.ElseValue is null ? null : new LiteralExpr(@case.ElseValue, Unknown),
                Unknown),
            SubQuerySelectCondition subquery => new SubqueryExpr(Map(ToDefinition(subquery)), Unknown),
            TemplateSqlTokenSelectCondition token => MapTemplateToken(token),
            _ => throw new InvalidOperationException(
                $"Unsupported SELECT expression for Core AST mapping: {condition.GetType().Name}")
        };
    }

    private static SqlExpr MapTemplateToken(TemplateSqlTokenSelectCondition token)
    {
        var value = token.Token.Replace("_", string.Empty, StringComparison.Ordinal).Trim().ToUpperInvariant();
        return value switch
        {
            "CURRENTDATE" => MapFunction("CURRENT_DATE", null, false, null, null),
            "CURRENTTIME" => MapFunction("CURRENT_TIME", null, false, null, null),
            "CURRENTTIMESTAMP" => MapFunction("CURRENT_TIMESTAMP", null, false, null, null),
            "SYSDATE" => MapFunction("SYSDATE", null, false, null, null),
            "DAY" or "WEEK" or "MONTH" or "QUARTER" or "YEAR" or "HOUR" or "MINUTE" or "SECOND" =>
                new ColumnExpr(Identifier(value), Unknown),
            _ => throw new SqlCompilationException($"Unsupported SQL template token '{token.Token}'.")
        };
    }

    private static SqlExpr MapFunction(
        string name,
        IEnumerable<SelectCondition>? arguments,
        bool distinct,
        IReadOnlyCollection<WhereCondition>? filter,
        WindowDefinition? window)
    {
        SqlExpr result = new FunctionCallExpr(
            Identifier(name),
            arguments?.Select(MapExpr).ToImmutableArray() ?? ImmutableArray<SqlExpr>.Empty,
            distinct,
            Unknown);

        if (filter is { Count: > 0 })
        {
            var predicate = MapWhereList(filter.ToList())
                ?? throw new InvalidOperationException($"Function FILTER for '{name}' cannot be empty.");
            result = new FilterExpr(result, predicate, Unknown);
        }

        if (window is not null)
            result = new WindowedExpr(result, MapWindow(window), Unknown);

        return result;
    }

    private static WindowSpec MapWindow(WindowDefinition window) =>
        new(
            window.PartitionBy?.Select(MapGroupBy).ToImmutableArray()
                ?? ImmutableArray<SqlExpr>.Empty,
            MapOrderBy(window.OrderBy),
            window.Frame is null ? null : MapWindowFrame(window.Frame),
            Unknown);

    private static WindowFrame MapWindowFrame(WindowFrameDefinition frame) =>
        new(
            frame.Unit switch
            {
                WindowFrameUnit.Rows => WindowFrameUnitKind.Rows,
                WindowFrameUnit.Range => WindowFrameUnitKind.Range,
                _ => throw new ArgumentOutOfRangeException(nameof(frame.Unit))
            },
            MapWindowBound(frame.Start),
            frame.End is null ? null : MapWindowBound(frame.End),
            Unknown);

    private static WindowFrameBoundCore MapWindowBound(WindowFrameBound bound)
    {
        var kind = bound.Kind switch
        {
            WindowFrameBoundKind.UnboundedPreceding => WindowFrameBoundKindCore.UnboundedPreceding,
            WindowFrameBoundKind.Preceding => WindowFrameBoundKindCore.Preceding,
            WindowFrameBoundKind.CurrentRow => WindowFrameBoundKindCore.CurrentRow,
            WindowFrameBoundKind.Following => WindowFrameBoundKindCore.Following,
            WindowFrameBoundKind.UnboundedFollowing => WindowFrameBoundKindCore.UnboundedFollowing,
            _ => throw new ArgumentOutOfRangeException(nameof(bound.Kind))
        };
        if (kind is WindowFrameBoundKindCore.Preceding or WindowFrameBoundKindCore.Following
            && bound.Offset is null or < 0)
            throw new InvalidOperationException($"Window frame bound '{bound.Kind}' requires a non-negative offset.");
        if (kind is not (WindowFrameBoundKindCore.Preceding or WindowFrameBoundKindCore.Following)
            && bound.Offset is not null)
            throw new InvalidOperationException($"Window frame bound '{bound.Kind}' must not carry an offset.");
        return new WindowFrameBoundCore(kind, bound.Offset, Unknown);
    }

    private static SqlExpr? MapWhereList(IReadOnlyList<WhereCondition>? conditions)
    {
        if (conditions is not { Count: > 0 }) return null;
        SqlExpr? result = null;
        foreach (var condition in conditions)
        {
            var current = MapWhere(condition);
            result = result is null
                ? current
                : new BinaryExpr(result, condition.IsOr ? "OR" : "AND", current, Unknown);
        }
        return result;
    }

    private static SqlExpr MapWhere(WhereCondition condition)
    {
        SqlExpr result = condition switch
        {
            BasicWhereCondition basic => MapBasicWhere(basic),
            ColumnCompareWhereCondition compare => new BinaryExpr(
                new ColumnExpr(Identifier(compare.LeftFieldName), Unknown),
                NormalizeComparisonOperator(compare.Operator),
                new ColumnExpr(Identifier(compare.RightFieldName), Unknown),
                Unknown),
            ExpressionWhereCondition expression => MapExpressionPredicate(
                expression.LeftExpression,
                expression.Operator,
                expression.RightExpression),
            GroupWhereCondition group => MapWhereList(group.Groups)
                ?? throw new InvalidOperationException("Empty WHERE groups are not valid Core predicates."),
            SubQueryWhereCondition subquery => MapSubQueryWhere(subquery),
            _ => throw new InvalidOperationException(
                $"Unsupported WHERE node for Core AST mapping: {condition.GetType().Name}")
        };

        return condition.IsNot ? new UnaryExpr("NOT", result, Unknown) : result;
    }

    private static SqlExpr MapExpressionPredicate(
        SelectCondition left,
        string opText,
        SelectCondition? right)
    {
        var op = NormalizeComparisonOperator(opText);
        var leftExpr = MapExpr(left);
        if (right is null)
        {
            if (op is "IS" or "IS NOT")
                return new IsNullExpr(leftExpr, op == "IS NOT", Unknown);
            throw new InvalidOperationException(
                $"Predicate operator '{op}' requires a right-hand expression.");
        }
        return new BinaryExpr(leftExpr, op, MapExpr(right), Unknown);
    }

    private static SqlExpr MapBasicWhere(BasicWhereCondition basic)
    {
        if (string.IsNullOrWhiteSpace(basic.FieldName))
            throw new InvalidOperationException("WHERE field name cannot be empty.");

        var field = new ColumnExpr(Identifier(basic.FieldName), Unknown);
        var op = NormalizeComparisonOperator(basic.Operator);

        if (op is "IN" or "NOT IN")
        {
            if (basic.Values.Count == 0)
                throw new InvalidOperationException($"{op} requires at least one value.");
            return new InExpr(
                field,
                basic.Values.Select(v => (SqlExpr)new LiteralExpr(v, Unknown)).ToImmutableArray(),
                op == "NOT IN",
                Unknown);
        }

        if (op is "BETWEEN" or "NOT BETWEEN")
        {
            if (basic.Value is not IEnumerable<object> values)
                throw new InvalidOperationException($"{op} requires exactly two values.");
            var pair = values.Take(3).ToArray();
            if (pair.Length != 2)
                throw new InvalidOperationException($"{op} requires exactly two values.");
            return new BetweenExpr(
                field,
                new LiteralExpr(pair[0], Unknown),
                new LiteralExpr(pair[1], Unknown),
                op == "NOT BETWEEN",
                Unknown);
        }

        if ((op is "IS" or "IS NOT") && basic.Value is null)
            return new IsNullExpr(field, op == "IS NOT", Unknown);

        if (op is "IS" or "IS NOT")
            throw new InvalidOperationException($"{op} currently supports NULL only in the Core AST.");

        return new BinaryExpr(field, op, new LiteralExpr(basic.Value, Unknown), Unknown);
    }

    private static SqlExpr MapSubQueryWhere(SubQueryWhereCondition subquery)
    {
        var op = NormalizeComparisonOperator(subquery.Operator);
        var mapped = Map(subquery.SubQuery);
        if (op is "EXISTS" or "NOT EXISTS")
            return new ExistsExpr(mapped, op == "NOT EXISTS", Unknown);

        if (op is not ("IN" or "NOT IN"))
            throw new InvalidOperationException($"Unsupported subquery predicate operator '{subquery.Operator}'.");
        if (string.IsNullOrWhiteSpace(subquery.FieldName))
            throw new InvalidOperationException($"{op} subquery predicate requires a field name.");

        return new BinaryExpr(
            new ColumnExpr(Identifier(subquery.FieldName), Unknown),
            op,
            new SubqueryExpr(mapped, Unknown),
            Unknown);
    }

    private static SqlExpr MapGroupBy(GroupByCondition condition) => condition switch
    {
        FieldGroupByCondition field => new ColumnExpr(Identifier(field.FieldName), Unknown),
        FunctionGroupByCondition function => MapFunction(
            function.FunctionName,
            function.Arguments,
            function.IsDistinct,
            function.FilterWhereConditions,
            null),
        _ => throw new InvalidOperationException(
            $"Unsupported GROUP BY node for Core AST mapping: {condition.GetType().Name}")
    };

    private static SqlExpr? MapHavingList(IReadOnlyList<HavingCondition>? conditions)
    {
        if (conditions is not { Count: > 0 }) return null;
        SqlExpr? result = null;
        foreach (var condition in conditions)
        {
            var current = MapHaving(condition);
            result = result is null
                ? current
                : new BinaryExpr(result, condition.IsOr ? "OR" : "AND", current, Unknown);
        }
        return result;
    }

    private static SqlExpr MapHaving(HavingCondition condition)
    {
        SqlExpr result = condition switch
        {
            BasicHavingCondition basic => MapHavingBasic(basic),
            FunctionHavingCondition function => MapHavingFunction(function),
            ExpressionHavingCondition expression => MapExpressionPredicate(
                expression.LeftExpression,
                expression.Operator,
                expression.RightExpression),
            GroupHavingCondition group => MapHavingList(group.Groups)
                ?? throw new InvalidOperationException("Empty HAVING groups are not valid Core predicates."),
            _ => throw new InvalidOperationException(
                $"Unsupported HAVING node for Core AST mapping: {condition.GetType().Name}")
        };

        return condition.IsNot ? new UnaryExpr("NOT", result, Unknown) : result;
    }

    private static SqlExpr MapHavingBasic(BasicHavingCondition basic)
    {
        var left = new ColumnExpr(Identifier(basic.FieldName), Unknown);
        var op = NormalizeComparisonOperator(basic.Operator);
        if ((op is "IS" or "IS NOT") && basic.Value is null)
            return new IsNullExpr(left, op == "IS NOT", Unknown);
        if (op is "IS" or "IS NOT")
            throw new InvalidOperationException($"{op} currently supports NULL only in the Core AST.");
        return new BinaryExpr(left, op, new LiteralExpr(basic.Value, Unknown), Unknown);
    }

    private static SqlExpr MapHavingFunction(FunctionHavingCondition function)
    {
        var left = MapFunction(
            function.LeftFunction.FunctionName,
            function.LeftFunction.Arguments,
            function.LeftFunction.IsDistinct,
            function.LeftFunction.FilterWhereConditions,
            function.LeftFunction.Window);
        var op = NormalizeComparisonOperator(function.Operator);
        if ((op is "IS" or "IS NOT") && function.Value is null)
            return new IsNullExpr(left, op == "IS NOT", Unknown);
        if (op is "IS" or "IS NOT")
            throw new InvalidOperationException($"{op} currently supports NULL only in the Core AST.");
        return new BinaryExpr(left, op, new LiteralExpr(function.Value, Unknown), Unknown);
    }

    private static ImmutableArray<OrderByItem> MapOrderBy(IEnumerable<OrderByCondition>? conditions) =>
        conditions?.Select(condition =>
        {
            var expression = condition switch
            {
                FieldOrderByCondition field => (SqlExpr)new ColumnExpr(Identifier(field.FieldName), Unknown),
                FunctionOrderByCondition function => MapFunction(
                    function.FunctionName,
                    function.Arguments,
                    function.IsDistinct,
                    function.FilterWhereConditions,
                    null),
                _ => throw new InvalidOperationException(
                    $"Unsupported ORDER BY node for Core AST mapping: {condition.GetType().Name}")
            };
            return new OrderByItem(
                expression,
                condition.Direction == SortDirection.Desc,
                condition.NullOrdering switch
                {
                    NullOrdering.Default => NullOrderingKind.Default,
                    NullOrdering.First => NullOrderingKind.First,
                    NullOrdering.Last => NullOrderingKind.Last,
                    _ => throw new ArgumentOutOfRangeException(nameof(condition.NullOrdering))
                },
                Unknown);
        }).ToImmutableArray() ?? ImmutableArray<OrderByItem>.Empty;

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

    private static SqlIdentifier Identifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("SQL identifier cannot be empty.");
        var parts = value.Split('.', StringSplitOptions.TrimEntries);
        if (parts.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException($"Invalid SQL identifier '{value}'.");
        return new SqlIdentifier(
            parts.Select(part => new IdentifierPart(part, false, Unknown)).ToImmutableArray(),
            Unknown);
    }

    private static string? NormalizeAlias(string? alias) =>
        string.IsNullOrWhiteSpace(alias) ? null : alias.Trim();

    private static string MapOperator(ArithmeticOperator op) => op switch
    {
        ArithmeticOperator.Add => "+",
        ArithmeticOperator.Subtract => "-",
        ArithmeticOperator.Multiply => "*",
        ArithmeticOperator.Divide => "/",
        ArithmeticOperator.Modulo => "%",
        ArithmeticOperator.Concat => "||",
        ArithmeticOperator.Equal => "=",
        ArithmeticOperator.NotEqual => "<>",
        ArithmeticOperator.GreaterThan => ">",
        ArithmeticOperator.LessThan => "<",
        ArithmeticOperator.GreaterThanOrEqual => ">=",
        ArithmeticOperator.LessThanOrEqual => "<=",
        ArithmeticOperator.And => "AND",
        ArithmeticOperator.Or => "OR",
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Unknown expression operator.")
    };

    private static string NormalizeComparisonOperator(string? op)
    {
        var normalized = string.Join(' ', (op ?? "=")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToUpperInvariant();
        return normalized switch
        {
            "=" or "<>" or "!=" or ">" or "<" or ">=" or "<=" or
            "LIKE" or "ILIKE" or "IN" or "NOT IN" or "BETWEEN" or "NOT BETWEEN" or
            "IS" or "IS NOT" or "EXISTS" or "NOT EXISTS" => normalized,
            "ISNULL" => "IS",
            "ISNOTNULL" => "IS NOT",
            "NOTIN" => "NOT IN",
            "NOTBETWEEN" => "NOT BETWEEN",
            "NOTEXISTS" => "NOT EXISTS",
            _ => throw new InvalidOperationException($"Unsupported comparison operator '{op}'.")
        };
    }

    private static SetOperationKind MapSetOperation(CombineType type) => type switch
    {
        CombineType.Union => SetOperationKind.Union,
        CombineType.UnionAll => SetOperationKind.UnionAll,
        CombineType.Intersect => SetOperationKind.Intersect,
        CombineType.Except => SetOperationKind.Except,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown set operation.")
    };
}
