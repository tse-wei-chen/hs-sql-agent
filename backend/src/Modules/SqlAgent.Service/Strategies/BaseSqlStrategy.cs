using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Interfaces;
using SqlAgent.Service.Models;
using SqlKata;
using SqlKata.Compilers;
using SqlKata.Execution;

namespace SqlAgent.Service.Strategies;

public abstract class BaseSqlStrategy(IQueryValueParserService valueParser, IConfiguration configuration) : ISqlStrategy
{
    private static readonly Regex SafeFunctionNamePattern = new(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$", RegexOptions.Compiled);
    private readonly IQueryValueParserService _valueParser = valueParser;
    protected readonly IConfiguration _configuration = configuration;

    public abstract SqlAgentToolType DbType { get; }

    public abstract string BuildConnectionString(BuildDbConnectionModelBase model);

    public abstract DbConnection CreateConnection(string? connectionString);
    protected abstract Compiler CreateCompiler();

    #region Shared Query Builders

    private Query ApplySelectColumns(Query query, List<SelectCondition> cols)
    {
        if (cols == null || cols.Count == 0) return query.Select("*");

        foreach (var col in cols)
        {
            var hasAlias = !string.IsNullOrWhiteSpace(col.Alias);
            var hasAgg = !string.IsNullOrWhiteSpace(col.Aggregation);

            AbstractColumn columnExpr;

            if (col.Arithmetic != null)
            {
                columnExpr = MapArithmetic(col.Arithmetic);
            }
            else if (col.Function != null)
            {
                columnExpr = MapFunction(col.Function);
            }
            else if (col.CaseWhen?.Count > 0)
            {
                columnExpr = MapCaseWhen(col.CaseWhen, col.ElseValue);
            }
            else if (col.SubQuery != null)
            {
                columnExpr = new QueryColumn { Query = BuildQueryFromDefinition(col.SubQuery) };
            }
            else if (!string.IsNullOrWhiteSpace(col.Field))
            {
                columnExpr = new Column { Name = col.Field.Trim() };
            }
            else
            {
                continue;
            }

            if (hasAgg)
            {
                columnExpr = new AggregatedColumn
                {
                    Aggregate = col.Aggregation,
                    Column = columnExpr
                };
            }

            if (hasAlias)
            {
                columnExpr.Alias = col.Alias;
            }

            query = query.Select(columnExpr);
        }

        return query;
    }

    private CaseColumn MapCaseWhen(List<SqlAgent.Service.Models.CaseWhenClause> cases, object? elseValue)
    {
        var caseCol = new CaseColumn();
        foreach (var c in cases)
        {
            var whenQuery = new Query();
            ApplySingleWhere(whenQuery, c.Condition);
            caseCol.Cases.Add(new SqlKata.CaseWhenClause
            {
                ConditionQuery = whenQuery,
                Value = c.Value is JsonElement je ? _valueParser.UnwrapJsonElement(je) : c.Value
            });
        }
        caseCol.ElseValue = elseValue is JsonElement eje ? _valueParser.UnwrapJsonElement(eje) : elseValue;
        return caseCol;
    }

    private AbstractColumn MapArithmetic(SelectArithmeticCondition arithmetic)
    {
        if (arithmetic.Arithmetic != null)
        {
            return MapArithmetic(arithmetic.Arithmetic);
        }

        if (arithmetic.Operator != null)
        {
            if (!string.IsNullOrWhiteSpace(arithmetic.FieldName) || arithmetic.Constant != null || arithmetic.Function != null)
            {
                throw new InvalidOperationException("Arithmetic node cannot contain both 'operator' and leaf properties. Use 'left' and 'right' for operations, and 'fieldName'/'constant'/'function' for leaf nodes.");
            }

            var left = arithmetic.Left != null ? MapArithmetic(arithmetic.Left) : throw new InvalidOperationException("Arithmetic operation missing 'left' operand.");
            var right = arithmetic.Right != null ? MapArithmetic(arithmetic.Right) : throw new InvalidOperationException("Arithmetic operation missing 'right' operand.");

            return new ArithmeticColumn
            {
                Left = left,
                Right = right,
                Operator = arithmetic.Operator
            };
        }

        return MapArithmeticLeaf(arithmetic);
    }

    private AbstractColumn MapArithmeticLeaf(SelectArithmeticCondition arithmetic)
    {
        if (arithmetic.Function != null)
        {
            return MapFunction(arithmetic.Function);
        }

        if (arithmetic.Constant != null)
        {
            var value = arithmetic.Constant is JsonElement je ? _valueParser.UnwrapJsonElement(je) : arithmetic.Constant;

            // To prevent PostgreSQL "integer - real" type mismatch errors during parameterization,
            // promote common integer types to decimal.
            if (value is sbyte or byte or short or ushort or int or uint or long or ulong)
            {
                value = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            }

            return new NumberColumn { Value = value };
        }

        if (string.IsNullOrWhiteSpace(arithmetic.FieldName))
        {
            throw new InvalidOperationException("Arithmetic leaf node must contain either FieldName, Constant, or Function. Do not wrap properties in an extra 'arithmetic' object.");
        }

        return new Column { Name = arithmetic.FieldName.Trim() };
    }

    private AbstractColumn MapFunction(SqlFunctionCondition function)
    {
        var functionName = function.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(functionName) || !SafeFunctionNamePattern.IsMatch(functionName))
        {
            throw new InvalidOperationException($"Invalid function name: {function.Name}");
        }

        return new FunctionColumn
        {
            Name = functionName,
            Arguments = function.Arguments?.Select(MapFunctionArgument).ToList() ?? []
        };
    }

    private AbstractColumn MapFunctionArgument(SqlFunctionArgument argument)
    {
        if (argument.Arithmetic != null)
        {
            return MapArithmetic(argument.Arithmetic);
        }

        if (argument.Function != null)
        {
            return MapFunction(argument.Function);
        }

        if (argument.Constant != null)
        {
            var value = argument.Constant is JsonElement je ? _valueParser.UnwrapJsonElement(je) : argument.Constant;

            if (value is string stringValue)
            {
                var escaped = stringValue.Replace("'", "''");
                return new NumberColumn { Value = new UnsafeLiteral($"'{escaped}'", replaceQuotes: false) };
            }

            if (value is null)
            {
                return new NumberColumn { Value = new UnsafeLiteral("NULL", replaceQuotes: false) };
            }

            if (value is bool boolValue)
            {
                return new NumberColumn { Value = new UnsafeLiteral(boolValue ? "true" : "false", replaceQuotes: false) };
            }

            if (value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal)
            {
                return new NumberColumn
                {
                    Value = new UnsafeLiteral(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0", replaceQuotes: false)
                };
            }

            return new NumberColumn { Value = value };
        }

        if (!string.IsNullOrWhiteSpace(argument.FieldName))
        {
            return new Column { Name = argument.FieldName.Trim() };
        }

        throw new InvalidOperationException("Function arguments must provide FieldName, Constant, or nested Function.");
    }

    private Query ApplyJoins(Query query, IList<JoinCondition> joins)
    {
        foreach (var join in joins)
        {
            var joinType = join.Type.ToLowerInvariant().Replace(" ", "").Trim() ?? "inner";
            var type = joinType switch
            {
                "left" => "left join",
                "right" => "right join",
                "full" or "outer" or "fullouter" => "full outer join",
                "cross" => "cross join",
                _ => "inner join"
            };

            Join onCallback(Join j)
            {
                if (join.OnConditions?.Count > 0)
                {
                    ApplyWhereConditions(j, join.OnConditions);
                }
                else
                {
                    j.On(join.First, join.Second, join.Operator);
                }
                return j;
            }

            if (join.SubQuery != null)
            {
                var sub = BuildQueryFromDefinition(join.SubQuery);
                if (!string.IsNullOrWhiteSpace(join.Alias))
                {
                    sub.As(join.Alias);
                }
                query = query.Join(sub, onCallback, type);
            }
            else
            {
                var tableName = join.Table;
                if (!string.IsNullOrWhiteSpace(join.Alias))
                {
                    tableName += " AS " + join.Alias;
                }
                query = query.Join(tableName ?? string.Empty, onCallback, type);
            }
        }

        return query;
    }

    private Q ApplyWhereConditions<Q>(Q query, IList<WhereCondition> conds) where Q : BaseQuery<Q>
    {
        foreach (var c in conds)
        {
            ApplySingleWhere(query, c);
        }
        return query;
    }

    private Q ApplySingleWhere<Q>(Q query, WhereCondition c) where Q : BaseQuery<Q>
    {
        if (c.Groups?.Count > 0)
        {
            return c.IsOr
                ? query.OrWhere(q => ApplyWhereConditions(q, c.Groups))
                : query.Where(q => ApplyWhereConditions(q, c.Groups));
        }

        if (string.IsNullOrWhiteSpace(c.Field) && c.SubQuery == null) return query;

        var op = (c.Operator ?? "=").ToLowerInvariant().Replace(" ", "").Trim();
        var val = c.Value is JsonElement je ? _valueParser.UnwrapJsonElement(je) : c.Value;
        var vals = c.Values?.Select(v => v is JsonElement vje ? _valueParser.UnwrapJsonElement(vje) : v).ToList();

        // Subquery handling (EXISTS, IN)
        if (c.SubQuery != null)
        {
            var sub = BuildQueryFromDefinition(c.SubQuery);
            return op switch
            {
                "exists" => c.IsOr
                    ? (c.IsNot ? query.OrWhereNotExists(sub) : query.OrWhereExists(sub))
                    : (c.IsNot ? query.WhereNotExists(sub) : query.WhereExists(sub)),
                "notexists" => c.IsOr
                    ? (c.IsNot ? query.OrWhereExists(sub) : query.OrWhereNotExists(sub))
                    : (c.IsNot ? query.WhereExists(sub) : query.WhereNotExists(sub)),
                "in" => c.IsOr
                    ? (c.IsNot ? query.OrWhereNotIn(c.Field, sub) : query.OrWhereIn(c.Field, sub))
                    : (c.IsNot ? query.WhereNotIn(c.Field, sub) : query.WhereIn(c.Field, sub)),
                "notin" => c.IsOr
                    ? (c.IsNot ? query.OrWhereIn(c.Field, sub) : query.OrWhereNotIn(c.Field, sub))
                    : (c.IsNot ? query.WhereIn(c.Field, sub) : query.WhereNotIn(c.Field, sub)),
                _ => query
            };
        }

        // Date handling
        if (c.IsDate)
        {
            var dtPart = _valueParser.TryToDateTime(val, out var dt) ? (object)dt : val;
            return op switch
            {
                "is" or "isnull" => c.IsOr ? query.OrWhereDate(c.Field, dtPart) : query.WhereDate(c.Field, dtPart),
                "isnot" or "isnotnull" => c.IsOr ? query.OrWhereNotDate(c.Field, dtPart) : query.WhereNotDate(c.Field, dtPart),

                "in" or "notin" when _valueParser.TryGetInValues(val, out var ins) || (vals != null && _valueParser.TryGetInValues(vals, out ins))
                    => ApplyDateIn(query, c, op, ins),

                "between" or "notbetween" when _valueParser.TryGetRangeValues(val, out var low, out var high)
                    => ApplyDateBetween(query, c, op, low, high),

                _ => c.IsOr ? query.OrWhereDate(c.Field, op, dtPart) : query.WhereDate(c.Field, op, dtPart)
            };
        }

        // Standard handling
        void apply(Q q)
        {
            switch (op)
            {
                case "is":
                case "isnull":
                    q.Where(c.Field, val);
                    break;
                case "isnot":
                case "isnotnull":
                    q.WhereNot(c.Field, val);
                    break;
                case "in":
                case "notin":
                    if (_valueParser.TryGetInValues(val, out var ins))
                        _ = op == "in" ? q.WhereIn(c.Field, ins) : q.WhereNotIn(c.Field, ins);
                    else if (vals != null && vals.Count > 0)
                        _ = op == "in" ? q.WhereIn(c.Field, vals) : q.WhereNotIn(c.Field, vals);
                    break;
                case "between":
                case "notbetween":
                    if (_valueParser.TryGetRangeValues(val, out var low, out var high))
                        _ = op == "between" ? q.WhereBetween(c.Field, low, high) : q.WhereNotBetween(c.Field, low, high);
                    break;
                case "like":
                    q.WhereLike(c.Field, val);
                    break;
                case "notlike":
                    q.WhereNotLike(c.Field, val);
                    break;
                case "starts":
                    q.WhereStarts(c.Field, val);
                    break;
                case "ends":
                    q.WhereEnds(c.Field, val);
                    break;
                case "contains":
                    q.WhereContains(c.Field, val);
                    break;
                default:
                    q.Where(c.Field, op, val);
                    break;
            }
        }

        if (c.IsOr) return query.OrWhere(q => { apply(q); return q; });
        if (c.IsNot) return query.Not().Where(q => { apply(q); return q; });

        apply(query);
        return query;
    }

    private Q ApplyDateIn<Q>(Q query, WhereCondition c, string op, IEnumerable<object> ins) where Q : BaseQuery<Q>
    {
        var dtIns = ins.Select(i => _valueParser.TryToDateTime(i, out var d) ? (object)d : i).ToList();
        return op == "in"
            ? (c.IsOr ? query.OrWhereDateIn(c.Field, dtIns) : query.WhereDateIn(c.Field, dtIns))
            : (c.IsOr ? query.OrWhereDateNotIn(c.Field, dtIns) : query.WhereDateNotIn(c.Field, dtIns));
    }

    private Q ApplyDateBetween<Q>(Q query, WhereCondition c, string op, object? low, object? high) where Q : BaseQuery<Q>
    {
        var lowDt = _valueParser.TryToDateTime(low, out var d1) ? (object)d1 : low;
        var highDt = _valueParser.TryToDateTime(high, out var d2) ? (object)d2 : high;
        return op == "between"
            ? (c.IsOr ? query.OrWhereDateBetween(c.Field, lowDt, highDt) : query.WhereDateBetween(c.Field, lowDt, highDt))
            : (c.IsOr ? query.OrWhereDateNotBetween(c.Field, lowDt, highDt) : query.WhereDateNotBetween(c.Field, lowDt, highDt));
    }


    private Query ApplyGroupByConditions(Query query, IList<GroupByCondition> conds)
    {
        foreach (var gf in conds)
        {
            if (gf.Function != null)
            {
                query = query.GroupBy(MapFunction(gf.Function));
                continue;
            }

            var field = gf.Field.Trim();
            query = query.GroupBy(field);
        }

        return query;
    }

    private Query ApplyHavingConditions(Query query, IList<HavingCondition> conds)
    {
        foreach (var c in conds)
        {
            query = ApplySingleHaving(query, c);
        }
        return query;
    }

    private Query ApplySingleHaving(Query query, HavingCondition c)
    {
        if (c.Groups?.Count > 0)
        {
            return c.IsOr
                ? query.OrHaving(q => ApplyHavingConditions(q, c.Groups))
                : query.Having(q => ApplyHavingConditions(q, c.Groups));
        }

        if (string.IsNullOrWhiteSpace(c.Field)) return query;

        var op = (c.Operator ?? "=").ToLowerInvariant().Replace(" ", "").Trim();
        var val = c.Value is JsonElement je ? _valueParser.UnwrapJsonElement(je) : c.Value;
        var agg = c.Aggregation;
        var isAgg = !string.IsNullOrWhiteSpace(agg);
        var field = c.Field.Trim();

        if (c.IsDate)
        {
            var dtVal = _valueParser.TryToDateTime(val, out var dt) ? dt : val;
            return isAgg
                ? op switch
                {
                    "in" or "notin" when _valueParser.TryGetInValues(val, out var ins)
                        => ApplyDateInAggregate(query, c, agg, field, op, ins),

                    "between" or "notbetween" when _valueParser.TryGetRangeValues(val, out var low, out var high)
                        => ApplyDateBetweenAggregate(query, c, agg, field, op, low, high),

                    _ => c.IsOr ? query.OrHavingDateAggregate(agg, field, op, dtVal) : query.HavingDateAggregate(agg, field, op, dtVal)
                }
                : op switch
                {
                    "in" or "notin" when _valueParser.TryGetInValues(val, out var ins)
                        => ApplyDateInHaving(query, c, field, op, ins),

                    "between" or "notbetween" when _valueParser.TryGetRangeValues(val, out var low, out var high)
                        => ApplyDateBetweenHaving(query, c, field, op, low, high),

                    _ => c.IsOr ? query.OrHavingDate(field, op, dtVal) : query.HavingDate(field, op, dtVal)
                };
        }

        Query apply(Query q)
        {
            return op switch
            {
                "between" or "notbetween" when _valueParser.TryGetRangeValues(val, out var low, out var high) =>
                    isAgg
                        ? (op == "between" ? q.HavingBetweenAggregate(agg, field, low, high) : q.HavingNotBetweenAggregate(agg, field, low, high))
                        : (op == "between" ? q.HavingBetween(field, low, high) : q.HavingNotBetween(field, low, high)),

                "is" or "isnull" => isAgg ? q.HavingAggregate(agg, field, "=", null) : q.HavingNull(field),
                "isnot" or "isnotnull" => isAgg ? q.Not().HavingAggregate(agg, field, "=", null) : q.HavingNotNull(field),

                "in" when _valueParser.TryGetInValues(val, out var ins)
                    => isAgg ? q.HavingInAggregate(agg, field, ins) : q.HavingIn(field, ins),
                "notin" when _valueParser.TryGetInValues(val, out var nins)
                    => isAgg ? q.HavingNotInAggregate(agg, field, nins) : q.HavingNotIn(field, nins),

                "like" => isAgg ? q.HavingLikeAggregate(agg, field, val) : q.HavingLike(field, val),
                "starts" => isAgg ? q.HavingStartsAggregate(agg, field, val) : q.HavingStarts(field, val),
                "ends" => isAgg ? q.HavingEndsAggregate(agg, field, val) : q.HavingEnds(field, val),
                "contains" => isAgg ? q.HavingContainsAggregate(agg, field, val) : q.HavingContains(field, val),

                _ => isAgg ? q.HavingAggregate(agg, field, op, val) : q.Having(field, op, val)
            };
        }

        if (c.IsOr) return query.OrHaving(q => apply(q));
        if (c.IsNot) return query.Not().Having(q => apply(q));
        return query.Having(q => apply(q));
    }

    private Query ApplyDateInAggregate(Query query, HavingCondition c, string agg, string field, string op, IEnumerable<object> ins)
    {
        var dtIns = ins.Select(i => _valueParser.TryToDateTime(i, out var d) ? (object)d : i).ToList();
        return op == "in"
            ? (c.IsOr ? query.Or().HavingDateInAggregate(agg, field, dtIns) : query.HavingDateInAggregate(agg, field, dtIns))
            : (c.IsOr ? query.Or().Not().HavingDateInAggregate(agg, field, dtIns) : query.Not().HavingDateInAggregate(agg, field, dtIns));
    }

    private Query ApplyDateBetweenAggregate(Query query, HavingCondition c, string agg, string field, string op, object? low, object? high)
    {
        var lowDt = _valueParser.TryToDateTime(low, out var d1) ? (object)d1 : low;
        var highDt = _valueParser.TryToDateTime(high, out var d2) ? (object)d2 : high;
        return op == "between"
            ? (c.IsOr ? query.Or().HavingDateBetweenAggregate(agg, field, lowDt, highDt) : query.HavingDateBetweenAggregate(agg, field, lowDt, highDt))
            : (c.IsOr ? query.Or().Not().HavingDateBetweenAggregate(agg, field, lowDt, highDt) : query.Not().HavingDateBetweenAggregate(agg, field, lowDt, highDt));
    }

    private Query ApplyDateInHaving(Query query, HavingCondition c, string field, string op, IEnumerable<object> ins)
    {
        var dtIns = ins.Select(i => _valueParser.TryToDateTime(i, out var d) ? (object)d : i).ToList();
        return op == "in"
            ? (c.IsOr ? query.Or().HavingDateIn(field, dtIns) : query.HavingDateIn(field, dtIns))
            : (c.IsOr ? query.Or().Not().HavingDateIn(field, dtIns) : query.Not().HavingDateIn(field, dtIns));
    }

    private Query ApplyDateBetweenHaving(Query query, HavingCondition c, string field, string op, object? low, object? high)
    {
        var lowDt = _valueParser.TryToDateTime(low, out var d1) ? (object)d1 : low;
        var highDt = _valueParser.TryToDateTime(high, out var d2) ? (object)d2 : high;
        return op == "between"
            ? (c.IsOr ? query.Or().HavingDateBetween(field, lowDt, highDt) : query.HavingDateBetween(field, lowDt, highDt))
            : (c.IsOr ? query.Or().Not().HavingDateBetween(field, lowDt, highDt) : query.Not().HavingDateBetween(field, lowDt, highDt));
    }


    private Query ApplyOrderByColumns(Query query, IList<OrderByCondition> cols)
    {
        if (cols == null || !cols.Any()) return query;

        return cols.Where(c => c.Function != null || !string.IsNullOrWhiteSpace(c.Field))
            .Aggregate(query, (q, c) =>
            {
                var field = c.Field?.Trim() ?? string.Empty;
                var dir = c.Direction?.ToLowerInvariant().Trim() ?? "asc";
                var functionExpr = c.Function != null ? MapFunction(c.Function) : null;

                return dir switch
                {
                    "random" => q.OrderByRandom(field),
                    "desc" when functionExpr != null => q.OrderByDesc(functionExpr),
                    "desc" => q.OrderByDesc(field),
                    "asc" when functionExpr != null => q.OrderBy(functionExpr),
                    "asc" => q.OrderBy(field),
                    _ when functionExpr != null => q.OrderBy(functionExpr),
                    _ => q.OrderBy(field)
                };
            });
    }
    #endregion
    #region ExecuteQueryAsync
    public async Task<string> ExecuteQueryAsync(
        string? connectionString = null,
        string? tableName = null,
        List<SelectCondition>? selectColumns = null,
        List<WhereCondition>? whereConditions = null,
        List<OrderByCondition>? orderByColumns = null,
        List<GroupByCondition>? groupByConditions = null,
        List<HavingCondition>? havingConditions = null,
        List<CombineCondition>? combineConditions = null,
        List<CteCondition>? cteConditions = null,
        int? limit = null,
        int? offset = null,
        List<JoinCondition>? joins = null,
        QueryDefinition? fromQuery = null,
        string? alias = null,
        bool distinct = false,
        CancellationToken cancellationToken = default)
    {
        var definition = new QueryDefinition
        {
            TableName = tableName ?? string.Empty,
            FromQuery = fromQuery,
            Alias = alias,
            Distinct = distinct,
            SelectColumns = selectColumns,
            WhereColumnsAndValues = whereConditions,
            OrderByColumns = orderByColumns,
            GroupByConditions = groupByConditions,
            HavingConditions = havingConditions,
            Joins = joins,
            Limit = limit,
            Offset = offset,
            CombineConditions = combineConditions,
            CteConditions = cteConditions
        };

        using var connection = CreateConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var compiler = CreateCompiler();
        var db = new QueryFactory(connection, compiler);

        try
        {
            var query = BuildQueryFromDefinition(definition);
            var result = await db.GetAsync(query, cancellationToken: cancellationToken);
            return SerializeQueryResult(result);
        }
        catch (Exception ex)
        {
            var message = BuildExecutionErrorMessage(ex, "Query");
            throw new Exception(message, ex);
        }
    }

    public async Task<string> ExecuteDmlAsync(
        string? connectionString = null,
        DmlDefinition? dml = null,
        CancellationToken cancellationToken = default)
    {
        if (dml == null) return "No DML definition provided.";

        using var connection = CreateConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var compiler = CreateCompiler();
        var db = new QueryFactory(connection, compiler);

        using var transaction = connection.BeginTransaction();

        try
        {
            var query = new Query(dml.TableName);

            // Apply where for update/delete
            if (dml.WhereConditions?.Count > 0)
            {
                query = ApplyWhereConditions(query, dml.WhereConditions);
            }

            // Build the action
            Query terminalQuery;
            switch (dml.Operation.ToLowerInvariant())
            {
                case "insert":
                    if (dml.FromQuery != null) terminalQuery = query.AsInsert(dml.Columns ?? [], BuildQueryFromDefinition(dml.FromQuery));
                    else if (dml.MultiValues?.Count > 0) terminalQuery = query.AsInsert(dml.Columns ?? [], dml.MultiValues);
                    else if (dml.Values?.Count > 0)
                    {
                        var data = dml.Values.ToDictionary(v => v.Name, v => v.Value is System.Text.Json.JsonElement je ? _valueParser.UnwrapJsonElement(je) : v.Value);
                        terminalQuery = query.AsInsert(data);
                    }
                    else return "Insert operation requires Values or MultiValues or FromQuery.";
                    break;
                case "update":
                    if (dml.Values?.Count > 0)
                    {
                        var data = dml.Values.ToDictionary(v => v.Name, v => v.Value is System.Text.Json.JsonElement je ? _valueParser.UnwrapJsonElement(je) : v.Value);
                        terminalQuery = query.AsUpdate(data);
                    }
                    else return "Update operation requires Values.";
                    break;
                case "delete":
                    terminalQuery = query.AsDelete();
                    break;
                default:
                    return $"Unsupported DML operation: {dml.Operation}";
            }

            // Execution
            int affected = await db.ExecuteAsync(terminalQuery, transaction, cancellationToken: cancellationToken);

            // Decide whether to commit or rollback based on token
            var expectedToken = GenerateConfirmToken(dml.Operation, dml.TableName, affected);

            if (dml.ConfirmToken == expectedToken)
            {
                transaction.Commit();
                return $"Success | affectedRows={affected} | Operation Committed.";
            }
            else
            {
                transaction.Rollback();
                return $"Dry Run Result | affectedRows={affected} | TokenRequired={expectedToken} | " +
                       "Security Note: This operation HAS NOT been committed. To proceed, call me again with the provided Token.";
            }
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            var message = BuildExecutionErrorMessage(ex, "DML");
            throw new Exception(message, ex);
        }
    }

    private string GenerateConfirmToken(string operation, string table, int affectedRows)
    {
        var secret = _configuration["McpKeySettings:HmacSecretKey"] ?? "AgentSafetyFallbackSecret";
        var input = $"{operation.ToLowerInvariant()}|{table.ToLowerInvariant()}|{affectedRows}|{secret}";
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToBase64String(bytes)[..12];
    }
    #endregion

    #region BuildQueryFromDefinition
    private Query BuildQueryFromDefinition(QueryDefinition definition)
    {
        // 1. Determine Source (Table or Subquery)
        var tableName = definition.TableName;
        if (!string.IsNullOrEmpty(definition.Alias) && definition.FromQuery == null && !tableName.Contains(" as ", StringComparison.InvariantCultureIgnoreCase))
        {
            tableName += " AS " + definition.Alias;
        }

        var query = definition.FromQuery != null
            ? new Query().From(BuildQueryFromDefinition(definition.FromQuery), definition.Alias)
            : new Query(tableName);

        // 2. Apply CTEs
        if (definition.CteConditions?.Count > 0)
        {
            foreach (var cte in definition.CteConditions)
            {
                if (string.IsNullOrWhiteSpace(cte.Name)) continue;
                query = query.With(cte.Name, BuildQueryFromDefinition(cte.Query));
            }
        }

        // 3. Apply Base Components
        if (definition.Distinct) query = query.Distinct();
        query = ApplySelectColumns(query, definition.SelectColumns ?? []);
        if (definition.Joins?.Count > 0) query = ApplyJoins(query, definition.Joins);
        if (definition.WhereColumnsAndValues?.Count > 0) query = ApplyWhereConditions(query, definition.WhereColumnsAndValues);
        if (definition.GroupByConditions?.Count > 0) query = ApplyGroupByConditions(query, definition.GroupByConditions);
        if (definition.HavingConditions?.Count > 0) query = ApplyHavingConditions(query, definition.HavingConditions);

        // 4. Handle Combines (UNION, INTERSECT, EXCEPT)
        if (definition.CombineConditions?.Count > 0)
        {
            foreach (var combine in definition.CombineConditions)
            {
                var sub = BuildQueryFromDefinition(combine.Query);
                var type = combine.Type?.ToLowerInvariant().Replace("_", "").Trim() ?? "union";
                query = type switch
                {
                    "unionall" => query.Union(sub, all: true),
                    "intersect" => query.Intersect(sub),
                    "except" => query.Except(sub),
                    _ => query.Union(sub)
                };
            }

            // 5. Wrapping for Post-Combine Operations
            // If we have ORDER BY or LIMIT/OFFSET after a combine, we MUST wrap it in a subquery
            // to ensure the operations apply to the combined set, not just the last branch.
            if (definition.OrderByColumns?.Count > 0 || (definition.Limit ?? 0) > 0 || (definition.Offset ?? 0) > 0)
            {
                var wrapper = new Query().From(query.As("combined_set"));
                if (definition.OrderByColumns?.Count > 0)
                    wrapper = ApplyOrderByColumns(wrapper, definition.OrderByColumns);
                if ((definition.Limit ?? 0) > 0)
                    wrapper = wrapper.Limit(definition.Limit!.Value);
                if ((definition.Offset ?? 0) > 0)
                    wrapper = wrapper.Offset(definition.Offset!.Value);

                return wrapper;
            }
        }
        else
        {
            // Standard No-Combine Operations
            if (definition.OrderByColumns?.Count > 0)
                query = ApplyOrderByColumns(query, definition.OrderByColumns);
            if ((definition.Limit ?? 0) > 0)
                query = query.Limit(definition.Limit!.Value);
            if ((definition.Offset ?? 0) > 0)
                query = query.Offset(definition.Offset!.Value);
        }

        return query;
    }
    #endregion

    #region Error Helpers

    protected virtual string SerializeQueryResult(IEnumerable<dynamic> result)
    {
        var resultList = result.Select(r => (IDictionary<string, object>)r).ToList();
        return JsonSerializer.Serialize(resultList);
    }

    protected virtual string BuildExecutionErrorMessage(Exception ex, string type)
    {
        return $"Error executing query | {ex}";
    }

    protected virtual string BuildHint(string? code, string message)
    {
        return "Use the SQL and bindings to adjust fields/operators/types, then retry.";
    }

    #endregion

    #region Abstract Members

    public abstract Task<List<string>> GetSchemasAsync(string connectionString, CancellationToken cancellationToken = default);
    public abstract Task<List<string>> GetTablesAsync(string connectionString, string schemaName, CancellationToken cancellationToken = default);
    public abstract Task<List<ColumnInfo>> GetColumnsAsync(string connectionString, string schemaName, string tableName, CancellationToken cancellationToken = default);

    #endregion
}
