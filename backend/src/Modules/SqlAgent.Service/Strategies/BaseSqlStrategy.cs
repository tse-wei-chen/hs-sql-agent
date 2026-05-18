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

public abstract class BaseSqlStrategy(
    IQueryValueParserService valueParser,
    IConfiguration configuration) : ISqlStrategy
{
    private static readonly Regex SafeFunctionNamePattern =
        new(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$", RegexOptions.Compiled);

    private readonly IQueryValueParserService _valueParser = valueParser;

    private static SqlFunctionCondition ToFunc(
        string name, List<SqlFunctionArgument>? args, bool distinct, List<WhereCondition>? filter) =>
        new() { FunctionName = name, Arguments = args, IsDistinct = distinct, FilterWhereConditions = filter };
    protected readonly IConfiguration _configuration = configuration;

    public abstract SqlAgentToolType DbType { get; }
    public abstract string BuildConnectionString(BuildDbConnectionModelBase model);
    public abstract DbConnection CreateConnection(string? connectionString);
    protected abstract Compiler CreateCompiler();

    // =====================================================================
    // Public API
    // =====================================================================

    public async Task<string> ExecuteQueryAsync(
        QueryDefinition definition,
        string? connectionString = null,
        CancellationToken cancellationToken = default)
    {
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
            throw new Exception(BuildExecutionErrorMessage(ex, "Query"), ex);
        }
    }

    public async Task<string> ExecuteDmlAsync(
        string? connectionString = null,
        DmlDefinition? dml = null,
        CancellationToken cancellationToken = default)
    {
        if (dml == null)
            return "No DML definition provided.";

        using var connection = CreateConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var compiler = CreateCompiler();
        var db = new QueryFactory(connection, compiler);

        using var transaction = connection.BeginTransaction();

        try
        {
            var query = new Query(dml.TableName);

            if (dml.WhereConditions?.Count > 0)
                query = ApplyWhereConditions(query, dml.WhereConditions);

            var terminalQuery = BuildDmlTerminalQuery(query, dml);
            if (terminalQuery == null)
                return $"Unsupported DML operation: {dml.Operation}";

            int affected = await db.ExecuteAsync(terminalQuery, transaction, cancellationToken: cancellationToken);

            var expectedToken = GenerateConfirmToken(dml.Operation, dml.TableName, affected);

            if (dml.ConfirmToken == expectedToken)
            {
                transaction.Commit();
                return $"Success | affectedRows={affected} | Operation Committed.";
            }

            transaction.Rollback();
            return $"Dry Run Result | affectedRows={affected} | TokenRequired={expectedToken} | " +
                   "Security Note: This operation HAS NOT been committed. To proceed, call me again with the provided Token.";
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            throw new Exception(BuildExecutionErrorMessage(ex, "DML"), ex);
        }
    }

    // =====================================================================
    // QueryDefinition → SqlKata Query
    // =====================================================================

    private Query BuildQueryFromDefinition(QueryDefinition definition)
    {
        var query = ResolveSource(definition);

        if (definition.CteConditions?.Count > 0)
            query = ApplyCtes(query, definition.CteConditions);

        if (definition.Distinct)
            query = query.Distinct();

        query = ApplySelectColumns(query, definition.SelectColumns ?? []);

        if (definition.Joins?.Count > 0)
            query = ApplyJoins(query, definition.Joins);

        if (definition.WhereColumnsAndValues?.Count > 0)
            query = ApplyWhereConditions(query, definition.WhereColumnsAndValues);

        if (definition.GroupByConditions?.Count > 0)
            query = ApplyGroupByConditions(query, definition.GroupByConditions);

        if (definition.HavingConditions?.Count > 0)
            query = ApplyHavingConditions(query, definition.HavingConditions);

        if (definition.CombineConditions?.Count > 0)
            return ApplyCombines(query, definition);

        if (definition.OrderByColumns?.Count > 0)
            query = ApplyOrderByColumns(query, definition.OrderByColumns);

        if ((definition.Limit ?? 0) > 0)
            query = query.Limit(definition.Limit!.Value);

        if ((definition.Offset ?? 0) > 0)
            query = query.Offset(definition.Offset!.Value);

        return query;
    }

    private Query ResolveSource(QueryDefinition definition)
    {
        if (definition.FromQuery != null)
        {
            // Use the inner FromQuery's alias as the subquery wrapper alias,
            // so column references (e.g. "sub.customer_id") stay valid.
            // Fall back to the outer definition's Alias.
            var alias = definition.FromQuery.Alias;
            if (string.IsNullOrWhiteSpace(alias))
                alias = definition.Alias ?? "_sub";
            return new Query().From(BuildQueryFromDefinition(definition.FromQuery), alias);
        }

        var tableName = definition.TableName;
        if (!string.IsNullOrEmpty(definition.Alias)
            && !tableName.Contains(" as ", StringComparison.InvariantCultureIgnoreCase))
        {
            tableName += " AS " + definition.Alias;
        }

        return new Query(tableName);
    }

    private Query ApplyCtes(Query query, List<CteCondition> ctes)
    {
        foreach (var cte in ctes)
        {
            if (string.IsNullOrWhiteSpace(cte.CteAliasName))
                continue;

            query = query.With(cte.CteAliasName, BuildQueryFromDefinition(cte.Query));
        }

        return query;
    }

    private Query ApplyCombines(Query query, QueryDefinition definition)
    {
        foreach (var combine in definition.CombineConditions!)
        {
            var sub = BuildQueryFromDefinition(combine.Query);

            query = combine.Type switch
            {
                CombineType.UnionAll => query.Union(sub, all: true),
                CombineType.Intersect => query.Intersect(sub),
                CombineType.Except => query.Except(sub),
                _ => query.Union(sub)
            };
        }

        // ORDER BY / LIMIT / OFFSET after combine → must wrap in a subquery
        if (definition.OrderByColumns?.Count > 0
            || (definition.Limit ?? 0) > 0
            || (definition.Offset ?? 0) > 0)
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

        return query;
    }

    // =====================================================================
    // SELECT columns
    // =====================================================================

    private Query ApplySelectColumns(Query query, List<SelectCondition>? cols)
    {
        if (cols == null || cols.Count == 0)
            return query.Select("*");

        foreach (var col in cols)
        {
            if (col == null)
                continue;

            var hasAlias = !string.IsNullOrWhiteSpace(col.Alias);

            var columnExpr = col switch
            {
                OperationSelectCondition opCol => MapArithmetic(opCol),
                ConstantSelectCondition constCol => MapArithmetic(
                    new ConstantArithmeticCondition { Constant = constCol.Constant }),
                FunctionSelectCondition f => MapFunction(ToFunc(f.FunctionName, f.Arguments, f.IsDistinct, f.FilterWhereConditions)),
                CaseWhenSelectCondition caseWhenCol => MapCaseWhen(caseWhenCol.CaseWhen, caseWhenCol.ElseValue),
                SubQuerySelectCondition subQueryCol => MapSubQueryColumn(subQueryCol),
                FieldSelectCondition fieldCol when !string.IsNullOrWhiteSpace(fieldCol.FieldName)
                    => new Column { Name = fieldCol.FieldName.Trim() },
                _ => null
            };

            if (columnExpr == null)
                continue;

            if (hasAlias)
                columnExpr.Alias = col.Alias!.Trim();

            query = query.Select(columnExpr);
        }

        return query;
    }

    private QueryColumn MapSubQueryColumn(SubQuerySelectCondition sq)
    {
        var qd = new QueryDefinition
        {
            TableName = sq.TableName,
            FromQuery = sq.FromQuery,
            Distinct = sq.Distinct,
            SelectColumns = sq.SelectColumns,
            WhereColumnsAndValues = sq.WhereColumnsAndValues,
            OrderByColumns = sq.OrderByColumns,
            GroupByConditions = sq.GroupByConditions,
            HavingConditions = sq.HavingConditions,
            Joins = sq.Joins,
            CombineConditions = sq.CombineConditions,
            CteConditions = sq.CteConditions,
            Limit = sq.Limit,
            Offset = sq.Offset
        };

        var result = new QueryColumn { Query = BuildQueryFromDefinition(qd) };
        if (!string.IsNullOrWhiteSpace(sq.Alias))
            result.Alias = sq.Alias.Trim();
        return result;
    }

    // =====================================================================
    // CASE WHEN
    // =====================================================================

    private CaseColumn MapCaseWhen(List<global::SqlAgent.Service.Models.CaseWhenClause> cases, object? elseValue)
    {
        var caseCol = new CaseColumn();

        foreach (var c in cases)
        {
            var whenQuery = new Query();
            ApplySingleWhere(whenQuery, c.Condition);

            caseCol.Cases.Add(new global::SqlKata.CaseWhenClause
            {
                ConditionQuery = whenQuery,
                Value = c.Value is JsonElement je
                    ? _valueParser.UnwrapJsonElement(je)
                    : c.Value
            });
        }

        caseCol.ElseValue = elseValue is JsonElement eje
            ? _valueParser.UnwrapJsonElement(eje)
            : elseValue;

        return caseCol;
    }

    // =====================================================================
    // Arithmetic expressions
    // =====================================================================

    private AbstractColumn MapArithmetic(SelectArithmeticCondition arithmetic)
    {
        return arithmetic switch
        {
            OperationArithmeticCondition op => new ArithmeticColumn
            {
                Left = MapArithmetic(op.Left),
                Right = MapArithmetic(op.Right),
                Operator = GetOperatorString(op.Operator)
            },

            FunctionArithmeticCondition f => MapFunction(ToFunc(f.FunctionName, f.Arguments, f.IsDistinct, f.FilterWhereConditions)),

            ConstantArithmeticCondition cst => MapConstantValue(cst.Constant),

            FieldArithmeticCondition field => new Column { Name = field.FieldName.Trim() },

            CaseWhenArithmeticCondition cs => MapCaseWhen(cs.CaseWhen, cs.ElseValue),

            _ => throw new InvalidOperationException(
                $"Unknown arithmetic condition type: {arithmetic?.GetType().Name}")
        };
    }

    private AbstractColumn MapArithmetic(OperationSelectCondition opCol)
    {
        var internalNode = new OperationArithmeticCondition
        {
            Left = opCol.Left,
            Operator = opCol.Operator,
            Right = opCol.Right
        };

        return MapArithmetic(internalNode);
    }

    private NumberColumn MapConstantValue(object? rawConstant)
    {
        var value = rawConstant is JsonElement je
            ? _valueParser.UnwrapJsonElement(je)
            : rawConstant;

        if (value is sbyte or byte or short or ushort or int or uint or long or ulong)
        {
            value = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        }

        return value switch
        {
            null => new NumberColumn
            {
                Value = new UnsafeLiteral("NULL", replaceQuotes: false)
            },

            string s => new NumberColumn
            {
                Value = new UnsafeLiteral($"'{s.Replace("'", "''")}'", replaceQuotes: false)
            },

            bool b => new NumberColumn
            {
                Value = new UnsafeLiteral(b ? "true" : "false", replaceQuotes: false)
            },

            float or double or decimal or sbyte or byte or short or ushort
                or int or uint or long or ulong => new NumberColumn
            {
                Value = new UnsafeLiteral(
                    Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0",
                    replaceQuotes: false)
            },

            _ => new NumberColumn { Value = value }
        };
    }

    private static string GetOperatorString(ArithmeticOperator op)
    {
        return op switch
        {
            ArithmeticOperator.Add => "+",
            ArithmeticOperator.Subtract => "-",
            ArithmeticOperator.Multiply => "*",
            ArithmeticOperator.Divide => "/",
            _ => throw new ArgumentOutOfRangeException(nameof(op), $"Unknown operator: {op}")
        };
    }

    // =====================================================================
    // SQL Functions
    // =====================================================================

    private FunctionColumn MapFunction(SqlFunctionCondition function)
    {
        var functionName = function.FunctionName?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(functionName) || !SafeFunctionNamePattern.IsMatch(functionName))
            throw new InvalidOperationException($"Invalid function name: {function.FunctionName}");

        var args = function.Arguments?.Select(MapFunctionArgument).ToList() ?? [];

        var result = new FunctionColumn
        {
            Name = functionName,
            Arguments = args,
            IsDistinct = function.IsDistinct
        };

        if (function.FilterWhereConditions?.Count > 0)
        {
            var filterQuery = new Query();
            ApplyWhereConditions(filterQuery, function.FilterWhereConditions);
            result.FilterQuery = filterQuery;
        }

        if (function.Window != null)
        {
            if (function.Window.PartitionBy?.Count > 0)
            {
                result.OverPartitionBy = function.Window.PartitionBy
                    .Select(p => p switch
                    {
                        FieldGroupByCondition f => (AbstractColumn)new Column { Name = f.FieldName.Trim() },
                        FunctionGroupByCondition f => MapFunction(ToFunc(f.FunctionName, f.Arguments, f.IsDistinct, f.FilterWhereConditions)),
                        _ => null
                    })
                    .Where(x => x != null)
                    .ToList()!;
            }

            if (function.Window.OrderBy?.Count > 0)
            {
                result.OverOrderBy = function.Window.OrderBy
                    .Select(o =>
                    {
                        var col = o switch
                        {
                            FieldOrderByCondition f => (AbstractColumn)new Column { Name = f.FieldName.Trim() },
                            FunctionOrderByCondition f => MapFunction(ToFunc(f.FunctionName, f.Arguments, f.IsDistinct, f.FilterWhereConditions)),
                            _ => null
                        };
                        if (col == null) return ((AbstractColumn?)null, string.Empty);
                        return (col, o.Direction == SortDirection.Desc ? "desc" : "asc");
                    })
                    .Where(x => x.Item1 != null)
                    .Select(x => (x.Item1!, x.Item2))
                    .ToList();
            }
        }

        return result;
    }

    private AbstractColumn MapFunctionArgument(SqlFunctionArgument argument)
    {
        return argument switch
        {
            ArithmeticFunctionArgument a => MapArithmetic(new OperationArithmeticCondition
            {
                Left = a.Left,
                Operator = a.Operator,
                Right = a.Right
            }),
            NestedFunctionArgument nf => MapFunction(ToFunc(nf.FunctionName, nf.Arguments, nf.IsDistinct, nf.FilterWhereConditions)),
            ConstantFunctionArgument constantArg => MapConstantValue(constantArg.Constant),
            FieldFunctionArgument fieldArg when !string.IsNullOrWhiteSpace(fieldArg.FieldName)
                => new Column { Name = fieldArg.FieldName.Trim() },
            _ => throw new InvalidOperationException(
                $"Unsupported or invalid function argument type: {argument?.GetType().Name ?? "null"}")
        };
    }

    // =====================================================================
    // WHERE conditions
    // =====================================================================

    private Q ApplyWhereConditions<Q>(Q query, IList<WhereCondition>? conds)
        where Q : BaseQuery<Q>
    {
        if (conds == null) return query;

        foreach (var c in conds)
            ApplySingleWhere(query, c);

        return query;
    }

    private Q ApplySingleWhere<Q>(Q query, WhereCondition condition)
        where Q : BaseQuery<Q>
    {
        return condition switch
        {
            GroupWhereCondition g => ApplyGroupWhere(query, g),
            SubQueryWhereCondition s => ApplySubQueryWhere(query, s),
            InWhereCondition i => ApplyInWhere(query, i),
            BasicWhereCondition b => ApplyBasicWhere(query, b),
            ColumnCompareWhereCondition c => ApplyColumnCompareWhere(query, c),
            _ => query
        };
    }

    private Q ApplyGroupWhere<Q>(Q query, GroupWhereCondition g)
        where Q : BaseQuery<Q>
    {
        if (g.Groups?.Count == 0)
            return query;

        return g.IsOr
            ? query.OrWhere(q => ApplyWhereConditions(q, g.Groups))
            : query.Where(q => ApplyWhereConditions(q, g.Groups));
    }

    private Q ApplySubQueryWhere<Q>(Q query, SubQueryWhereCondition s)
        where Q : BaseQuery<Q>
    {
        if (s.SubQuery == null)
            return query;

        var op = (s.Operator ?? "in").ToLowerInvariant().Replace(" ", "").Trim();
        var sub = BuildQueryFromDefinition(s.SubQuery);
        var field = s.FieldName;

        return op switch
        {
            "exists" => s.IsOr
                ? (s.IsNot ? query.OrWhereNotExists(sub) : query.OrWhereExists(sub))
                : (s.IsNot ? query.WhereNotExists(sub) : query.WhereExists(sub)),

            "notexists" => s.IsOr
                ? (s.IsNot ? query.OrWhereExists(sub) : query.OrWhereNotExists(sub))
                : (s.IsNot ? query.WhereExists(sub) : query.WhereNotExists(sub)),

            "in" when field != null => s.IsOr
                ? (s.IsNot ? query.OrWhereNotIn(field, sub) : query.OrWhereIn(field, sub))
                : (s.IsNot ? query.WhereNotIn(field, sub) : query.WhereIn(field, sub)),

            "notin" when field != null => s.IsOr
                ? (s.IsNot ? query.OrWhereIn(field, sub) : query.OrWhereNotIn(field, sub))
                : (s.IsNot ? query.WhereIn(field, sub) : query.WhereNotIn(field, sub)),

            _ => query
        };
    }

    private Q ApplyInWhere<Q>(Q query, InWhereCondition c)
        where Q : BaseQuery<Q>
    {
        if (string.IsNullOrWhiteSpace(c.FieldName) || c.Values == null || c.Values.Count == 0)
            return query;

        var op = (c.Operator ?? "in").ToLowerInvariant().Replace(" ", "").Trim();
        var vals = c.Values.Select(v => v is JsonElement je ? _valueParser.UnwrapJsonElement(je) : v).ToList();

        if (c.IsDate)
            return ApplyDateIn(query, c.FieldName, op, vals, c.IsOr);

        return op == "in"
            ? ApplyInLogic(query, c.FieldName, vals, c.IsOr, c.IsNot, negate: false)
            : ApplyInLogic(query, c.FieldName, vals, c.IsOr, c.IsNot, negate: true);
    }

    private Q ApplyBasicWhere<Q>(Q query, BasicWhereCondition c)
        where Q : BaseQuery<Q>
    {
        if (string.IsNullOrWhiteSpace(c.FieldName))
            return query;

        var op = (c.Operator ?? "=").ToLowerInvariant().Replace(" ", "").Trim();
        var val = c.Value is JsonElement je ? _valueParser.UnwrapJsonElement(je) : c.Value;

        if (c.IsDate)
            return ApplyDateWhere(query, c.FieldName, op, val, c.IsOr);

        if (c.IsOr)
            return query.OrWhere(q => { ApplySimpleWhere(q, c.FieldName, op, val); return q; });

        if (c.IsNot)
            return query.Not().Where(q => { ApplySimpleWhere(q, c.FieldName, op, val); return q; });

        ApplySimpleWhere(query, c.FieldName, op, val);
        return query;
    }

    private Q ApplyColumnCompareWhere<Q>(Q query, ColumnCompareWhereCondition c)
        where Q : BaseQuery<Q>
    {
        if (string.IsNullOrWhiteSpace(c.LeftFieldName) || string.IsNullOrWhiteSpace(c.RightFieldName))
            return query;

        var op = (c.Operator ?? "=").ToLowerInvariant().Replace(" ", "").Trim();

        if (c.IsOr)
            return query.OrWhereColumns(c.LeftFieldName, op, c.RightFieldName);

        if (c.IsNot)
            return query.Not().WhereColumns(c.LeftFieldName, op, c.RightFieldName);

        return query.WhereColumns(c.LeftFieldName, op, c.RightFieldName);
    }

    private void ApplySimpleWhere<Q>(Q query, string field, string op, object? val)
        where Q : BaseQuery<Q>
    {
        switch (op)
        {
            case "is":
            case "isnull":
                query.Where(field, val);
                break;

            case "isnot":
            case "isnotnull":
                query.WhereNot(field, val);
                break;

            case "between":
                if (_valueParser.TryGetRangeValues(val, out var low, out var high))
                    query.WhereBetween(field, low, high);
                break;

            case "notbetween":
                if (_valueParser.TryGetRangeValues(val, out low, out high))
                    query.WhereNotBetween(field, low, high);
                break;

            case "like":
                query.WhereLike(field, val);
                break;

            case "notlike":
                query.WhereNotLike(field, val);
                break;

            case "starts":
                query.WhereStarts(field, val);
                break;

            case "ends":
                query.WhereEnds(field, val);
                break;

            case "contains":
                query.WhereContains(field, val);
                break;

            default:
                query.Where(field, op, val);
                break;
        }
    }

    // =====================================================================
    // Date-specific WHERE helpers
    // =====================================================================

    private Q ApplyDateWhere<Q>(Q query, string field, string op, object? val, bool isOr)
        where Q : BaseQuery<Q>
    {
        if (op is "between" or "notbetween")
        {
            if (_valueParser.TryGetRangeValues(val, out var low, out var high))
                return ApplyDateBetween(query, field, op, low, high, isOr);
            return query;
        }

        var dtPart = _valueParser.TryToDateTime(val, out var dt) ? (object)dt : val;

        return op switch
        {
            "is" or "isnull" => isOr
                ? query.OrWhereDate(field, dtPart)
                : query.WhereDate(field, dtPart),

            "isnot" or "isnotnull" => isOr
                ? query.OrWhereNotDate(field, dtPart)
                : query.WhereNotDate(field, dtPart),

            _ => isOr
                ? query.OrWhereDate(field, op, dtPart)
                : query.WhereDate(field, op, dtPart)
        };
    }

    private Q ApplyDateIn<Q>(Q query, string field, string op, IEnumerable<object> values, bool isOr)
        where Q : BaseQuery<Q>
    {
        var dtValues = values
            .Select(v => _valueParser.TryToDateTime(v, out var d) ? (object)d : v)
            .ToList();

        return op == "in"
            ? (isOr ? query.OrWhereDateIn(field, dtValues) : query.WhereDateIn(field, dtValues))
            : (isOr ? query.OrWhereDateNotIn(field, dtValues) : query.WhereDateNotIn(field, dtValues));
    }

    private Q ApplyDateBetween<Q>(
        Q query, string field, string op, object? low, object? high, bool isOr)
        where Q : BaseQuery<Q>
    {
        var lowDt = _valueParser.TryToDateTime(low, out var d1) ? (object)d1 : low;
        var highDt = _valueParser.TryToDateTime(high, out var d2) ? (object)d2 : high;

        return op == "between"
            ? (isOr
                ? query.OrWhereDateBetween(field, lowDt, highDt)
                : query.WhereDateBetween(field, lowDt, highDt))
            : (isOr
                ? query.OrWhereDateNotBetween(field, lowDt, highDt)
                : query.WhereDateNotBetween(field, lowDt, highDt));
    }

    // =====================================================================
    // IN helper
    // =====================================================================

    private static Q ApplyInLogic<Q>(
        Q query, string field, List<object> values, bool isOr, bool isNot, bool negate)
        where Q : BaseQuery<Q>
    {
        if (negate)
        {
            return isOr
                ? (isNot ? query.OrWhereIn(field, values) : query.OrWhereNotIn(field, values))
                : (isNot ? query.WhereIn(field, values) : query.WhereNotIn(field, values));
        }

        return isOr
            ? (isNot ? query.OrWhereNotIn(field, values) : query.OrWhereIn(field, values))
            : (isNot ? query.WhereNotIn(field, values) : query.WhereIn(field, values));
    }

    // =====================================================================
    // JOINs
    // =====================================================================

    private Query ApplyJoins(Query query, IList<JoinCondition> joins)
    {
        foreach (var join in joins)
        {
            var sqlJoinType = MapJoinType(join.Type);

            if (join.SubQuery != null)
            {
                var sub = BuildQueryFromDefinition(join.SubQuery);
                var alias = !string.IsNullOrWhiteSpace(join.Alias)
                    ? join.Alias
                    : $"sub_{Guid.NewGuid().ToString("N")[..4]}";

                query = query.Join(
                    sub.As(alias),
                    j => { ApplyOnConditions(j, join); return j; },
                    sqlJoinType
                );
            }
            else
            {
                var tableName = join.Table ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(join.Alias))
                {
                    query = query.Join(
                        $"{tableName} AS {join.Alias}",
                        j => { ApplyOnConditions(j, join); return j; },
                        sqlJoinType
                    );
                }
                else
                {
                    query = query.Join(
                        tableName,
                        j => { ApplyOnConditions(j, join); return j; },
                        sqlJoinType
                    );
                }
            }
        }

        return query;
    }

    private static string MapJoinType(JoinType type)
    {
        return type switch
        {
            JoinType.Left => "left join",
            JoinType.Right => "right join",
            JoinType.Full => "full outer join",
            JoinType.Cross => "cross join",
            JoinType.Inner => "inner join",
            _ => "inner join"
        };
    }

    private void ApplyOnConditions(Join j, JoinCondition join)
    {
        if (join.OnConditions != null && join.OnConditions.Count > 0)
        {
            ApplyWhereConditions(j, join.OnConditions);
        }
        else
        {
            throw new InvalidOperationException(
                $"Join on '{join.Alias ?? join.Table}' must have at least one OnCondition.");
        }
    }

    // =====================================================================
    // GROUP BY
    // =====================================================================

    private Query ApplyGroupByConditions(Query query, IList<GroupByCondition> conds)
    {
        foreach (var cond in conds)
        {
            switch (cond)
            {
                case FieldGroupByCondition f:
                    query = query.GroupBy(f.FieldName.Trim());
                    break;

                case FunctionGroupByCondition f:
                    query = query.GroupBy(MapFunction(ToFunc(f.FunctionName, f.Arguments, f.IsDistinct, f.FilterWhereConditions)));
                    break;
            }
        }

        return query;
    }

    // =====================================================================
    // HAVING
    // =====================================================================

    private Query ApplyHavingConditions(Query query, IList<HavingCondition>? conds)
    {
        if (conds == null) return query;

        foreach (var c in conds)
            query = ApplySingleHaving(query, c);

        return query;
    }

    private Query ApplySingleHaving(Query query, HavingCondition condition)
    {
        return condition switch
        {
            GroupHavingCondition g => ApplyGroupHaving(query, g),
            BasicHavingCondition b => ApplyBasicHaving(query, b),
            FunctionHavingCondition f => ApplyFunctionHaving(query, f),
            _ => query
        };
    }

    private Query ApplyGroupHaving(Query query, GroupHavingCondition g)
    {
        if (g.Groups?.Count == 0)
            return query;

        return g.IsOr
            ? query.OrHaving(q => ApplyHavingConditions(q, g.Groups))
            : query.Having(q => ApplyHavingConditions(q, g.Groups));
    }

    private Query ApplyBasicHaving(Query query, BasicHavingCondition c)
    {
        if (string.IsNullOrWhiteSpace(c.FieldName))
            return query;

        var op = (c.Operator ?? "=").ToLowerInvariant().Replace(" ", "").Trim();
        var val = c.Value is JsonElement je ? _valueParser.UnwrapJsonElement(je) : c.Value;
        var field = c.FieldName.Trim();

        if (c.IsDate)
            return ApplyDateHaving(query, field, op, val, c.IsOr);

        if (c.IsOr)
            return query.OrHaving(q => ApplySimpleHaving(q, field, op, val));

        if (c.IsNot)
            return query.Not().Having(q => ApplySimpleHaving(q, field, op, val));

        return ApplySimpleHaving(query, field, op, val);
    }

    private Query ApplyFunctionHaving(Query query, FunctionHavingCondition c)
    {
        var func = c.LeftFunction;
        if (string.IsNullOrWhiteSpace(func.FunctionName))
            return query;

        var op = (c.Operator ?? "=").ToLowerInvariant().Replace(" ", "").Trim();
        var val = c.Value is JsonElement je ? _valueParser.UnwrapJsonElement(je) : c.Value;

        // Build raw HAVING expression with parameter placeholders
        var bindings = new List<object>();
        var expr = BuildHavingFuncExpr(func, bindings);

        var baseQuery = c.IsOr ? query.Or() : query;
        baseQuery = c.IsNot ? baseQuery.Not() : baseQuery;

        var sqlOp = op switch
        {
            "is" or "isnull" => "IS NULL",
            "isnot" or "isnotnull" => "IS NOT NULL",
            _ => op
        };

        if (sqlOp is "IS NULL" or "IS NOT NULL")
        {
            return baseQuery.HavingRaw($"{expr} {sqlOp}");
        }

        bindings.Add(val!);
        return baseQuery.HavingRaw($"{expr} {sqlOp} ?", [.. bindings]);
    }

    private string BuildHavingFuncExpr(SqlFunctionCondition func, List<object> bindings)
    {
        var argParts = func.Arguments?.Select(a => BuildHavingArgPart(a, bindings)).ToList() ?? [];
        return $"{func.FunctionName}({string.Join(", ", argParts)})";
    }

    private string BuildHavingArgPart(SqlFunctionArgument arg, List<object> bindings)
    {
        return arg switch
        {
            FieldFunctionArgument f => f.FieldName,
            ConstantFunctionArgument c => BuildHavingConstPart(c.Constant, bindings),
            NestedFunctionArgument n => BuildHavingFuncExpr(
                new SqlFunctionCondition
                {
                    FunctionName = n.FunctionName,
                    Arguments = n.Arguments,
                    IsDistinct = n.IsDistinct,
                    FilterWhereConditions = n.FilterWhereConditions
                },
                bindings),
            ArithmeticFunctionArgument a when a.Left != null => BuildHavingArithPart(
                new OperationArithmeticCondition { Left = a.Left, Operator = a.Operator, Right = a.Right },
                bindings),
            _ => "?"
        };
    }

    private string BuildHavingConstPart(object constant, List<object> bindings)
    {
        var val = constant is JsonElement je ? _valueParser.UnwrapJsonElement(je) : constant;
        bindings.Add(val);
        return "?";
    }

    private string BuildHavingArithPart(SelectArithmeticCondition condition, List<object> bindings)
    {
        return condition switch
        {
            FieldArithmeticCondition f => f.FieldName,
            ConstantArithmeticCondition c => BuildHavingConstPart(c.Constant, bindings),
            OperationArithmeticCondition op => $"({BuildHavingArithPart(op.Left, bindings)} {HavingOpToSql(op.Operator)} {BuildHavingArithPart(op.Right, bindings)})",
            _ => "?"
        };
    }

    private static string HavingOpToSql(ArithmeticOperator op)
    {
        return op switch
        {
            ArithmeticOperator.Add => "+",
            ArithmeticOperator.Subtract => "-",
            ArithmeticOperator.Multiply => "*",
            ArithmeticOperator.Divide => "/",
            _ => "+"
        };
    }

    private Query ApplySimpleHaving(Query query, string field, string op, object? val)
    {
        switch (op)
        {
            case "between":
                if (_valueParser.TryGetRangeValues(val, out var low, out var high))
                    return query.HavingBetween(field, low, high);
                break;

            case "notbetween":
                if (_valueParser.TryGetRangeValues(val, out low, out high))
                    return query.HavingNotBetween(field, low, high);
                break;

            case "is" or "isnull":
                return query.HavingNull(field);

            case "isnot" or "isnotnull":
                return query.HavingNotNull(field);

            case "in":
                if (_valueParser.TryGetInValues(val, out var ins))
                    return query.HavingIn(field, ins);
                break;

            case "notin":
                if (_valueParser.TryGetInValues(val, out var nins))
                    return query.HavingNotIn(field, nins);
                break;

            case "like":
                return query.HavingLike(field, val);

            case "starts":
                return query.HavingStarts(field, val);

            case "ends":
                return query.HavingEnds(field, val);

            case "contains":
                return query.HavingContains(field, val);

            default:
                return query.Having(field, op, val);
        }

        return query;
    }

    // =====================================================================
    // Date-specific HAVING helpers
    // =====================================================================

    private Query ApplyDateHaving(Query query, string field, string op, object? val, bool isOr)
    {
        switch (op)
        {
            case "in":
                if (_valueParser.TryGetInValues(val, out var ins))
                    return ApplyDateInHaving(query, field, op, ins, isOr);
                break;

            case "notin":
                if (_valueParser.TryGetInValues(val, out ins))
                    return ApplyDateInHaving(query, field, op, ins, isOr);
                break;

            case "between":
                if (_valueParser.TryGetRangeValues(val, out var low, out var high))
                    return ApplyDateBetweenHaving(query, field, op, low, high, isOr);
                break;

            case "notbetween":
                if (_valueParser.TryGetRangeValues(val, out low, out high))
                    return ApplyDateBetweenHaving(query, field, op, low, high, isOr);
                break;
        }

        var dtVal = _valueParser.TryToDateTime(val, out var dt) ? dt : val;
        return isOr
            ? query.OrHavingDate(field, op, dtVal)
            : query.HavingDate(field, op, dtVal);
    }

    private Query ApplyDateInHaving(
        Query query, string field, string op, IEnumerable<object> values, bool isOr)
    {
        var dtValues = values
            .Select(v => _valueParser.TryToDateTime(v, out var d) ? (object)d : v)
            .ToList();

        return op == "in"
            ? (isOr ? query.Or().HavingDateIn(field, dtValues) : query.HavingDateIn(field, dtValues))
            : (isOr ? query.Or().Not().HavingDateIn(field, dtValues) : query.Not().HavingDateIn(field, dtValues));
    }

    private Query ApplyDateBetweenHaving(
        Query query, string field, string op, object? low, object? high, bool isOr)
    {
        var lowDt = _valueParser.TryToDateTime(low, out var d1) ? (object)d1 : low;
        var highDt = _valueParser.TryToDateTime(high, out var d2) ? (object)d2 : high;

        return op == "between"
            ? (isOr
                ? query.Or().HavingDateBetween(field, lowDt, highDt)
                : query.HavingDateBetween(field, lowDt, highDt))
            : (isOr
                ? query.Or().Not().HavingDateBetween(field, lowDt, highDt)
                : query.Not().HavingDateBetween(field, lowDt, highDt));
    }

    // =====================================================================
    // ORDER BY
    // =====================================================================

    private Query ApplyOrderByColumns(Query query, IList<OrderByCondition> cols)
    {
        if (cols == null || cols.Count == 0)
            return query;

        foreach (var col in cols)
        {
            switch (col)
            {
                case FieldOrderByCondition f:
                    query = ApplyOrderByField(query, f.FieldName?.Trim() ?? string.Empty, f.Direction);
                    break;

                case FunctionOrderByCondition f:
                    query = ApplyOrderByFunction(query, MapFunction(ToFunc(f.FunctionName, f.Arguments, f.IsDistinct, f.FilterWhereConditions)), f.Direction);
                    break;
            }
        }

        return query;
    }

    private static Query ApplyOrderByField(Query query, string field, SortDirection direction)
    {
        if (string.IsNullOrWhiteSpace(field))
            return query;

        return direction switch
        {
            SortDirection.Random => query.OrderByRandom(field),
            SortDirection.Desc => query.OrderByDesc(field),
            _ => query.OrderBy(field)
        };
    }

    private static Query ApplyOrderByFunction(Query query, FunctionColumn function, SortDirection direction)
    {
        return direction switch
        {
            SortDirection.Desc => query.OrderByDesc(function),
            _ => query.OrderBy(function)
        };
    }

    // =====================================================================
    // DML helpers
    // =====================================================================

    private Query? BuildDmlTerminalQuery(Query query, DmlDefinition dml)
    {
        switch (dml.Operation)
        {
            case DmlOperation.Insert:
                if (dml.FromQuery != null)
                    return query.AsInsert(dml.Columns ?? [], BuildQueryFromDefinition(dml.FromQuery));

                if (dml.MultiValues?.Count > 0)
                    return query.AsInsert(dml.Columns ?? [], dml.MultiValues);

                if (dml.Values?.Count > 0)
                {
                    var data = dml.Values.ToDictionary(
                        v => v.FieldName,
                        v => v.Value is JsonElement je
                            ? _valueParser.UnwrapJsonElement(je)
                            : v.Value);
                    return query.AsInsert(data);
                }

                return null;

            case DmlOperation.Update:
                if (dml.Values?.Count > 0)
                {
                    var data = dml.Values.ToDictionary(
                        v => v.FieldName,
                        v => v.Value is JsonElement je
                            ? _valueParser.UnwrapJsonElement(je)
                            : v.Value);
                    return query.AsUpdate(data);
                }

                return null;

            case DmlOperation.Delete:
                return query.AsDelete();

            default:
                return null;
        }
    }

    private string GenerateConfirmToken(DmlOperation operation, string table, int affectedRows)
    {
        var secret = _configuration["McpKeySettings:HmacSecretKey"]
                     ?? "AgentSafetyFallbackSecret";

        var input = $"{operation.ToString().ToLowerInvariant()}|{table.ToLowerInvariant()}|{affectedRows}|{secret}";
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(input));

        return Convert.ToBase64String(bytes)[..12];
    }

    // =====================================================================
    // Error / Serialization helpers
    // =====================================================================

    protected virtual string SerializeQueryResult(IEnumerable<dynamic> result)
    {
        var list = result.Select(r => (IDictionary<string, object>)r).ToList();
        return JsonSerializer.Serialize(list);
    }

    protected virtual string BuildExecutionErrorMessage(Exception ex, string type)
    {
        return $"Error executing query | {ex}";
    }

    protected virtual string BuildHint(string? code, string message)
    {
        return "Use the SQL and bindings to adjust fields/operators/types, then retry.";
    }

    // =====================================================================
    // Schema introspection (abstract)
    // =====================================================================

    public abstract Task<List<string>> GetSchemasAsync(
        string connectionString, CancellationToken cancellationToken = default);

    public abstract Task<List<string>> GetTablesAsync(
        string connectionString, string schemaName, CancellationToken cancellationToken = default);

    public abstract Task<List<ColumnInfo>> GetColumnsAsync(
        string connectionString, string schemaName, string tableName,
        CancellationToken cancellationToken = default);
}
