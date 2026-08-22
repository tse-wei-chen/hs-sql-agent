using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FirebirdSql.Data.Types;
using Microsoft.Extensions.Configuration;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Interfaces;
using SqlAgent.Service.Models;
using SqlAgent.Service.Services;
using SqlAgent.Service.SqlParsing;
using SqlAgent.Service.SqlTranslation.Context;
using SqlAgent.Service.SqlTranslation.Ast.Semantic;
using SqlAgent.Service.SqlTranslation.Functions.Translators;
using SqlAgent.Service.SqlTranslation.Functions;
using SqlAgent.Service.SqlTranslation.Normalization;
using SqlAgent.Service.SqlTranslation.Diagnostics;
using SqlAgent.Service.SqlTranslation.DateFormats;
using SqlKata;
using SqlKata.Compilers;
using SqlKata.Execution;

namespace SqlAgent.Service.Strategies;

public abstract partial class BaseSqlStrategy(
    IQueryValueParserService valueParser,
    IConfiguration configuration) : ISqlStrategy
{
    static BaseSqlStrategy()
    {
        DapperTemporalTypeHandlerRegistry.EnsureRegistered();
    }

    private enum TemplateSqlToken
    {
        Day,
        Week,
        Month,
        Quarter,
        Year,
        Hour,
        Minute,
        Second,
        CurrentDate,
        CurrentTime,
        CurrentTimestamp,
        Sysdate,
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$")]
    private static partial Regex SafeFunctionNamePattern();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.]*(?:\s+(?:PRECISION|VARYING|WITH|WITHOUT|TIME|ZONE|SIGNED|UNSIGNED))*(?:\([0-9]+(?:,[0-9]+)?\))?$", RegexOptions.IgnoreCase)]
    private static partial Regex SafeCastTypePattern();

    private readonly IQueryValueParserService _valueParser = valueParser;
    private static readonly SpecializedFunctionTranslatorRegistry SpecializedFunctionTranslators = new(
    [
        new TemporalFunctionTranslator(),
        new JsonFunctionTranslator(),
        new RegexFunctionTranslator(),
        new PortableFunctionTranslator()
    ]);
    private static readonly FunctionRegistry SemanticFunctionRegistry =
        new(FunctionDefinitionLoader.LoadEmbedded());
    private static readonly SqlSemanticNormalizer SemanticFunctionNormalizer =
        new(SemanticFunctionRegistry);
    private static readonly DateFormatTranslator DateFormatTranslator = new();
    private static readonly HashSet<string> PortableIdentityFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "COUNT", "SUM", "AVG", "MIN", "MAX", "ROUND", "NULLIF", "ABS", "LOWER", "UPPER",
        "ROW_NUMBER", "RANK", "DENSE_RANK", "LAG", "LEAD", "FIRST_VALUE", "LAST_VALUE", "GROUPING"
    };
    // Temporary adapter around the legacy recursive SqlKata builder. It keeps one explicit
    // session consistent across a compile without changing process-global state and restores
    // nested scopes on dispose. New lowering code must receive TranslationContext explicitly;
    // this ambient bridge should disappear when BuildQueryFromDefinition is extracted into a
    // ProviderLowerer with Lower(node, context).
    private static readonly AsyncLocal<TranslationSession?> CurrentTranslation = new();
    private sealed record TranslationSession(
        TranslationContext Context,
        List<TranslationDiagnostic> Diagnostics);

    private static SqlFunctionCondition ToFunc(
        string name, List<SelectCondition>? args, bool distinct, List<WhereCondition>? filter, WindowDefinition? window = null) =>
        new() { FunctionName = name, Arguments = args, IsDistinct = distinct, FilterWhereConditions = filter, Window = window };
    protected readonly IConfiguration _configuration = configuration;

    public abstract SqlAgentToolType DbType { get; }
    public abstract string BuildConnectionString(BuildDbConnectionModelBase model);
    public abstract DbConnection CreateConnection(string? connectionString);
    protected abstract Compiler CreateCompiler();

    // =====================================================================
    // Public API
    // =====================================================================

    public string CompileQuerySql(QueryDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        // Null is a declaration of target-native input, never a request to infer a dialect.
        using var scope = BeginTranslation(new TranslationContext(
            definition.SourceDialect ?? DbType, DbType, UnknownFunctionPolicy.Throw));
        var compiler = CreateCompiler();
        return compiler.Compile(BuildQueryFromDefinition(definition)).RawSql;
    }

    public SqlTranslationResult CompileQueryTranslation(
        QueryDefinition definition,
        SqlAgentToolType sourceDialect,
        UnknownFunctionPolicy unknownFunctionPolicy = UnknownFunctionPolicy.Throw)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var session = new TranslationSession(
            new TranslationContext(sourceDialect, DbType, unknownFunctionPolicy), []);
        using var scope = BeginTranslation(session);
        var sql = CreateCompiler().Compile(BuildQueryFromDefinition(definition)).RawSql;
        return new SqlTranslationResult(sql, session.Diagnostics);
    }

    private static IDisposable BeginTranslation(TranslationContext context) =>
        BeginTranslation(new TranslationSession(context, []));

    private static IDisposable BeginTranslation(TranslationSession session)
    {
        var previous = CurrentTranslation.Value;
        CurrentTranslation.Value = session;
        return new TranslationScope(previous);
    }

    private sealed class TranslationScope(TranslationSession? previous) : IDisposable
    {
        public void Dispose() => CurrentTranslation.Value = previous;
    }

    public async Task<string> ExecuteQueryAsync(
        QueryDefinition definition,
        string? connectionString = null,
        CancellationToken cancellationToken = default)
        => await ExecuteQueryAsync(
            definition,
            connectionString,
            new SqlExecutionPolicy { QueryTimeoutSeconds = 30 },
            cancellationToken);

    public async Task<string> ExecuteQueryAsync(
        QueryDefinition definition,
        string? connectionString,
        SqlExecutionPolicy policy,
        CancellationToken cancellationToken = default)
    {
        using var connection = CreateConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var compiler = CreateCompiler();
        var db = new QueryFactory(connection, compiler)
        {
            QueryTimeout = NormalizeTimeout(policy.QueryTimeoutSeconds)
        };

        try
        {
            using var translationScope = BeginTranslation(new TranslationContext(
                definition.SourceDialect ?? DbType, DbType, UnknownFunctionPolicy.Throw));
            var query = BuildQueryFromDefinition(definition);
            var requestedLimit = definition.Limit.GetValueOrDefault();
            if (policy.QueryMaxRows > 0
                && (requestedLimit <= 0 || requestedLimit > policy.QueryMaxRows))
                query = query.Limit(policy.QueryMaxRows);
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
        => await ExecuteDmlAsync(
            connectionString,
            dml,
            new SqlExecutionPolicy { QueryTimeoutSeconds = 30 },
            cancellationToken);

    public async Task<string> ExecuteDmlAsync(
        string? connectionString,
        DmlDefinition? dml,
        SqlExecutionPolicy policy,
        CancellationToken cancellationToken = default)
    {
        if (dml == null)
            return "No DML definition provided.";

        var policyViolation = ValidateDmlPolicy(dml, policy);
        if (policyViolation is not null)
            return $"Security policy denied DML: {policyViolation}";

        using var connection = CreateConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var compiler = CreateCompiler();
        var db = new QueryFactory(connection, compiler)
        {
            QueryTimeout = NormalizeTimeout(policy.QueryTimeoutSeconds)
        };

        try
        {
            var (affected, preview) = await PreviewDmlAsync(db, dml, cancellationToken);

            if (policy.DmlMaxAffectedRows > 0 && affected > policy.DmlMaxAffectedRows)
                return $"Security policy denied DML: affectedRows={affected} exceeds maximum {policy.DmlMaxAffectedRows}.";

            var expectedToken = GenerateConfirmToken(dml, affected);
            if (dml.ConfirmToken != expectedToken)
                return $"Dry Run Result | affectedRows={affected} | TokenRequired={expectedToken} | " +
                       $"Preview={preview} | Security Note: This read-only preview did not execute the DML statement.";

            using var transaction = connection.BeginTransaction();
            try
            {
                var query = BuildDmlSourceQuery(dml);
                var terminalQuery = BuildDmlTerminalQuery(query, dml);
                if (terminalQuery == null)
                    return $"Unsupported DML operation: {dml.Operation}";

                var committedAffected = await db.ExecuteAsync(
                    terminalQuery,
                    transaction,
                    cancellationToken: cancellationToken);
                if (committedAffected != affected)
                {
                    transaction.Rollback();
                    return $"DML execution cancelled: the affected row count changed after approval " +
                           $"(approved={affected}, current={committedAffected}).";
                }

                transaction.Commit();
                return $"Success | affectedRows={committedAffected} | Operation Committed.";
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
        catch (Exception ex)
        {
            throw new Exception(BuildExecutionErrorMessage(ex, "DML"), ex);
        }
    }

    private static int NormalizeTimeout(int timeoutSeconds)
        => timeoutSeconds > 0 ? timeoutSeconds : 30;

    private static string? ValidateDmlPolicy(DmlDefinition dml, SqlExecutionPolicy policy)
    {
        var hasWhere = dml.WhereConditions is { Count: > 0 };

        if (dml.Operation == DmlOperation.Update &&
            !hasWhere &&
            (policy.RequireWhereForUpdate || !policy.AllowFullTableUpdate))
        {
            return "UPDATE without WHERE is not allowed.";
        }

        if (dml.Operation == DmlOperation.Delete &&
            !hasWhere &&
            (policy.RequireWhereForDelete || !policy.AllowFullTableDelete))
        {
            return "DELETE without WHERE is not allowed.";
        }

        return null;
    }

    // =====================================================================
    // QueryDefinition → SqlKata Query
    // =====================================================================

    private Query BuildQueryFromDefinition(QueryDefinition definition)
    {
        var query = ResolveSource(definition);

        if (definition.CteConditions?.Count > 0)
            query = ApplyCtes(query, definition.CteConditions);

        if (definition.FromQuery != null)
            ValidateOuterScopeAgainstSubquery(definition.FromQuery, definition);

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
            // Use the outer definition's Alias as the subquery wrapper alias,
            // so the outer SELECT can reference columns using the outer alias.
            // Fall back to the inner FromQuery's Alias, then "_sub".
            var alias = definition.Alias;
            if (string.IsNullOrWhiteSpace(alias))
                alias = definition.FromQuery.Alias ?? "_sub";
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

    private void ValidateOuterScopeAgainstSubquery(QueryDefinition subQuery, QueryDefinition outerDef)
    {
        if (outerDef.SelectColumns == null || outerDef.SelectColumns.Count == 0) return;
        // Must match ResolveSource: outerDef.Alias → subQuery.Alias → "_sub"
        var subAlias = outerDef.Alias ?? subQuery.Alias ?? "_sub";

        var innerAliases = CollectInnerJoinAliases(subQuery);

        foreach (var col in outerDef.SelectColumns)
        {
            CheckOuterSelectColumn(subQuery, col, innerAliases, subAlias);
        }
    }

    private static HashSet<string> CollectInnerJoinAliases(QueryDefinition subQuery)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(subQuery.Alias))
        {
            aliases.Add(subQuery.Alias);
        }

        if (subQuery.Joins == null) return aliases;
        foreach (var j in subQuery.Joins)
        {
            if (!string.IsNullOrWhiteSpace(j.Alias))
                aliases.Add(j.Alias);

            if (j.SubQuery?.Joins != null)
            {
                foreach (var sj in j.SubQuery.Joins)
                {
                    if (!string.IsNullOrWhiteSpace(sj.Alias))
                        aliases.Add(sj.Alias);
                }
            }
        }
        return aliases;
    }

    private void CheckOuterSelectColumn(QueryDefinition subQuery, SelectCondition col, HashSet<string> innerAliases, string subAlias)
    {
        switch (col)
        {
            case FieldSelectCondition f:
                CheckFieldNameLeak(f.FieldName, "outer SELECT", innerAliases, subAlias);
                break;

            case FunctionSelectCondition fn when fn.Arguments != null:
                foreach (var arg in fn.Arguments)
                    CheckFunctionArgLeak(arg, innerAliases, subAlias);
                break;

            case OperationSelectCondition op:
                CheckExprLeak(op.Left, innerAliases, subAlias);
                CheckExprLeak(op.Right, innerAliases, subAlias);
                break;

            case CastSelectCondition cast:
                CheckExprLeak(cast.Expression, innerAliases, subAlias);
                break;

            case CaseWhenSelectCondition cw when cw.CaseWhen != null:
                foreach (var clause in cw.CaseWhen)
                    CheckWhereLeak(clause.Condition, innerAliases, subAlias);
                break;
        }
    }

    private static void CheckFieldNameLeak(string? fieldName, string context, HashSet<string> innerAliases, string subAlias)
    {
        if (string.IsNullOrWhiteSpace(fieldName)) return;

        var trimmed = fieldName.AsSpan().Trim();
        if (trimmed.Equals("*", StringComparison.OrdinalIgnoreCase)) return;

        int firstDot = trimmed.IndexOf('.');
        if (firstDot == -1) return;

        int lastDot = trimmed.LastIndexOf('.');
        if (firstDot != lastDot) return;

        var prefix = trimmed[..firstDot];
        var columnName = trimmed[(firstDot + 1)..];

        if (prefix.Equals(subAlias.AsSpan(), StringComparison.OrdinalIgnoreCase)) return;
        string prefixStr = prefix.ToString();
        if (innerAliases.Contains(prefixStr))
        {
            throw new InvalidOperationException(
                $"Field '{fieldName.Trim()}' in {context} references table alias '{prefixStr}', " +
                $"which is defined only inside a subquery (FromQuery). " +
                $"The outer query can only see the subquery's output columns, " +
                $"not its internal tables. Use '{subAlias}.{columnName}' instead " +
                $"or reference the column via its alias from the subquery.");
        }
    }

    private void CheckFunctionArgLeak(SelectCondition arg, HashSet<string> innerAliases, string subAlias)
    {
        switch (arg)
        {
            case FieldSelectCondition f:
                CheckFieldNameLeak(f.FieldName, "outer SELECT function argument", innerAliases, subAlias);
                break;

            case FunctionSelectCondition n when n.Arguments != null:
                foreach (var a in n.Arguments)
                    CheckFunctionArgLeak(a, innerAliases, subAlias);
                break;

            case OperationSelectCondition a:
                CheckExprLeak(a.Left, innerAliases, subAlias);
                CheckExprLeak(a.Right, innerAliases, subAlias);
                break;

            case CastSelectCondition cast:
                CheckExprLeak(cast.Expression, innerAliases, subAlias);
                break;

            case CaseWhenSelectCondition cw when cw.CaseWhen != null:
                foreach (var clause in cw.CaseWhen)
                    CheckWhereLeak(clause.Condition, innerAliases, subAlias);
                break;
        }
    }

    private void CheckExprLeak(SelectCondition? cond, HashSet<string> innerAliases, string subAlias)
    {
        switch (cond)
        {
            case FieldSelectCondition f:
                CheckFieldNameLeak(f.FieldName, "outer SELECT expression", innerAliases, subAlias);
                break;

            case FunctionSelectCondition fn when fn.Arguments != null:
                foreach (var arg in fn.Arguments)
                    CheckFunctionArgLeak(arg, innerAliases, subAlias);
                break;

            case OperationSelectCondition op:
                CheckExprLeak(op.Left, innerAliases, subAlias);
                CheckExprLeak(op.Right, innerAliases, subAlias);
                break;

            case CastSelectCondition cast:
                CheckExprLeak(cast.Expression, innerAliases, subAlias);
                break;

            case CaseWhenSelectCondition cw when cw.CaseWhen != null:
                foreach (var clause in cw.CaseWhen)
                    CheckWhereLeak(clause.Condition, innerAliases, subAlias);
                break;
        }
    }

    private void CheckWhereLeak(WhereCondition? w, HashSet<string> innerAliases, string subAlias)
    {
        switch (w)
        {
            case BasicWhereCondition b:
                CheckFieldNameLeak(b.FieldName, "outer WHERE", innerAliases, subAlias);
                break;

            case SubQueryWhereCondition sq when sq.SubQuery != null:
                if (sq.SubQuery.SelectColumns != null)
                {
                    foreach (var c in sq.SubQuery.SelectColumns)
                        CheckOuterSelectColumn(sq.SubQuery, c, innerAliases, subAlias);
                }
                if (sq.SubQuery.WhereColumnsAndValues != null)
                {
                    foreach (var wc in sq.SubQuery.WhereColumnsAndValues)
                        CheckWhereLeak(wc, innerAliases, subAlias);
                }
                break;

            case GroupWhereCondition g when g.Groups != null:
                foreach (var gg in g.Groups)
                    CheckWhereLeak(gg, innerAliases, subAlias);
                break;
        }
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
                ConstantSelectCondition constCol => MapArithmetic(constCol),
                CastSelectCondition castCol => MapArithmetic(castCol),
                IntervalSelectCondition intervalCol => MapArithmetic(intervalCol),
                DateAddExpression dateAdd => MapArithmetic(dateAdd),
                DateDiffExpression dateDiff => MapArithmetic(dateDiff),
                JsonExtractExpression jsonExtract => MapArithmetic(jsonExtract),
                JsonSetExpression jsonSet => MapArithmetic(jsonSet),
                RegexMatchExpression regexMatch => MapArithmetic(regexMatch),
                DateFormatExpression dateFormat => MapArithmetic(dateFormat),
                FormattedDateParseExpression dateParse => MapArithmetic(dateParse),
                PositionExpression position => MapArithmetic(position),
                DatePartExpression datePart => MapArithmetic(datePart),
                TemplateSqlTokenSelectCondition tokenCol => MapArithmetic(tokenCol),
                FunctionSelectCondition f => MapFunction(ToFunc(f.FunctionName, f.Arguments, f.IsDistinct, f.FilterWhereConditions, f.Window)),
                CaseWhenSelectCondition caseWhenCol => MapCaseWhen(caseWhenCol.CaseWhen, caseWhenCol.ElseValue),
                SubQuerySelectCondition subQueryCol => MapSubQueryColumn(subQueryCol),
                FieldSelectCondition fieldCol when !string.IsNullOrWhiteSpace(fieldCol.FieldName)
                    => ValidateFieldColumn(fieldCol.FieldName.Trim()),
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

    private static Column ValidateFieldColumn(string fieldName)
    {
        if (fieldName.Contains('(') || fieldName.Contains(')'))
            throw new InvalidOperationException(
                $"Field name '{fieldName}' contains parentheses. " +
                "type: 'field' only allows pure column references. " +
                "Use type: 'function' for SQL functions like COUNT, SUM, AVG.");
        return new Column { Name = fieldName };
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
                Value = MapCaseWhenValue(c.Value)
            });
        }

        caseCol.ElseValue = MapCaseWhenValue(elseValue);

        return caseCol;
    }

    private object? MapCaseWhenValue(object? val)
    {
        if (val is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Object && je.TryGetProperty("type", out var typeProp)
                && typeProp.GetString() == "constant"
                && je.TryGetProperty("constant", out var constProp))
            {
                return UnwrapNumericConstant(constProp);
            }
            return _valueParser.UnwrapJsonElement(je);
        }
        if (val is ConstantSelectCondition cs)
        {
            var raw = cs.Constant is JsonElement csJe
                ? _valueParser.UnwrapJsonElement(csJe)
                : cs.Constant;
            return raw is sbyte or byte or short or ushort or int or uint or long or ulong
                ? Convert.ToDecimal(raw, CultureInfo.InvariantCulture)
                : raw;
        }
        if (val is SelectCondition sc)
            return MapArithmetic(sc);
        return val;
    }

    private static object? UnwrapNumericConstant(JsonElement constProp)
    {
        if (constProp.ValueKind == JsonValueKind.Number)
        {
            if (constProp.TryGetInt64(out var l)) return l is >= int.MinValue and <= int.MaxValue ? (int)l : l;
            return constProp.GetDouble();
        }
        if (constProp.ValueKind == JsonValueKind.String)
            return constProp.GetString()!;
        if (constProp.ValueKind == JsonValueKind.True) return true;
        if (constProp.ValueKind == JsonValueKind.False) return false;
        return constProp.ToString();
    }

    // =====================================================================
    // Arithmetic expressions
    // =====================================================================

    protected AbstractColumn MapArithmetic(SelectCondition? expr)
    {
        return expr switch
        {
            OperationSelectCondition op => MapOperation(op),

            FunctionSelectCondition f => MapFunction(ToFunc(f.FunctionName, f.Arguments, f.IsDistinct, f.FilterWhereConditions, f.Window)),

            CastSelectCondition cast => MapCast(cast),

            IntervalSelectCondition interval => MapInterval(interval),

            DateAddExpression dateAdd => MapDateAdd(dateAdd),

            DateDiffExpression dateDiff => MapDateDiff(dateDiff),

            JsonExtractExpression jsonExtract => MapJsonExtract(jsonExtract),

            JsonSetExpression jsonSet => MapJsonSet(jsonSet),

            RegexMatchExpression regexMatch => MapRegexMatch(regexMatch),

            DateFormatExpression dateFormat => MapDateFormat(dateFormat),

            FormattedDateParseExpression dateParse => MapFormattedDateParse(dateParse),

            PositionExpression position => MapPosition(position),

            DatePartExpression datePart => MapDatePart(datePart),

            ConstantSelectCondition cst => MapConstantValue(cst.Constant),

            TemplateSqlTokenSelectCondition token => MapTemplateSqlToken(token),

            TemplateExtractSelectCondition extract => MapExtract(extract),

            TemplateCaseSelectCondition caseExpression => MapTemplateCase(caseExpression),

            FieldSelectCondition field => new Column { Name = field.FieldName.Trim() },

            CaseWhenSelectCondition cs => MapCaseWhen(cs.CaseWhen, cs.ElseValue),

            SubQuerySelectCondition subQuery => MapSubQueryColumn(subQuery),

            null => throw new InvalidOperationException("Expression is null."),

            _ => throw new InvalidOperationException(
                $"Unsupported expression type in arithmetic context: {expr?.GetType().Name}")
        };
    }

    private AbstractColumn MapOperation(OperationSelectCondition op)
    {
        var left = MapArithmetic(op.Left);
        var right = MapArithmetic(op.Right);

        if (op.Operator == ArithmeticOperator.Modulo
            && DbType is SqlAgentToolType.Oracle or SqlAgentToolType.Firebird)
            return new FunctionColumn { Name = "MOD", Arguments = [left, right] };

        if (op.Operator == ArithmeticOperator.Concat)
        {
            if (DbType == SqlAgentToolType.MySQL)
                return new FunctionColumn { Name = "CONCAT", Arguments = [left, right] };
            if (DbType == SqlAgentToolType.MsSqlServer)
                return new ArithmeticColumn { Left = left, Right = right, Operator = "+" };
        }

        if (op.Operator is ArithmeticOperator.Equal or ArithmeticOperator.NotEqual
            or ArithmeticOperator.GreaterThan or ArithmeticOperator.LessThan
            or ArithmeticOperator.GreaterThanOrEqual or ArithmeticOperator.LessThanOrEqual
            or ArithmeticOperator.And or ArithmeticOperator.Or)
        {
            if (DbType is SqlAgentToolType.Oracle or SqlAgentToolType.MsSqlServer)
                throw CapabilityError("boolean/comparison expressions in SELECT");
        }

        return new ArithmeticColumn
        {
            Left = left,
            Right = right,
            Operator = GetOperatorString(op.Operator)
        };
    }

    private CastColumn MapCast(CastSelectCondition cast)
    {
        var typeName = cast.TypeName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(typeName) || !SafeCastTypePattern().IsMatch(typeName))
            throw new InvalidOperationException($"Unsupported or unsafe CAST type '{cast.TypeName}'.");
        return new CastColumn
        {
            Expression = MapArithmetic(cast.Expression),
            TypeName = typeName.ToUpperInvariant()
        };
    }

    private AbstractColumn MapInterval(IntervalSelectCondition interval)
    {
        if (DbType != SqlAgentToolType.Postgres)
            throw CapabilityError("INTERVAL expressions");
        if (string.IsNullOrWhiteSpace(interval.Literal))
            throw new InvalidOperationException("INTERVAL literal must not be empty.");
        var escaped = interval.Literal.Replace("'", "''", StringComparison.Ordinal);
        return new RawColumn { Expression = $"INTERVAL '{escaped}'", Bindings = [] };
    }

    private InvalidOperationException CapabilityError(string capability) =>
        new($"Unsupported SQL capability '{capability}' for provider {DbType}; the statement was rejected before execution.");

    private AbstractColumn MapConstantValue(object? rawConstant)
    {
        var value = rawConstant is JsonElement je
            ? _valueParser.UnwrapJsonElement(je)
            : rawConstant;

        if (value is sbyte or byte or short or ushort or int or uint or long or ulong)
        {
            value = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        }

        // Normalize legacy CLR temporal values before provider-specific handling,
        // so they follow exactly the same binding and type-annotation path as
        // temporal literals parsed from SQL.
        value = value switch
        {
            DateOnly date => new SqlDateValue(date),
            TimeOnly time => new SqlTimeValue(time),
            DateTime dt => dt.Kind == DateTimeKind.Utc
                ? new SqlOffsetDateTimeValue(new DateTimeOffset(dt))
                : new SqlLocalDateTimeValue(dt),
            DateTimeOffset dateTimeOffset => new SqlOffsetDateTimeValue(dateTimeOffset),
            _ => value
        };

        // Firebird cannot infer the data type of a parameter used as a bare
        // SELECT expression. Keep the value parameterized and add only the
        // provider type annotation required by the SQL compiler.
        if (DbType == SqlAgentToolType.Firebird && value is SqlTemporalValue)
        {
            var firebirdType = value switch
            {
                SqlDateValue => "DATE",
                SqlTimeValue => "TIME",
                SqlLocalDateTimeValue => "TIMESTAMP",
                SqlOffsetDateTimeValue => "TIMESTAMP WITH TIME ZONE",
                _ => throw new InvalidOperationException($"Unsupported Firebird temporal value {value.GetType().Name}.")
            };
            return new CastColumn
            {
                Expression = new NumberColumn { Value = value },
                TypeName = firebirdType
            };
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

            SqlTemporalValue temporal => new NumberColumn { Value = temporal },

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
            _ => throw new ArgumentOutOfRangeException(nameof(op), $"Unknown operator: {op}")
        };
    }

    // =====================================================================
    // SQL Functions — dialect normalization
    // =====================================================================

    protected AbstractColumn MapFunction(SqlFunctionCondition function)
    {
        var session = CurrentTranslation.Value;
        var context = session?.Context
            ?? new TranslationContext(DbType, DbType, UnknownFunctionPolicy.Passthrough);
        var expression = new FunctionSelectCondition
        {
            FunctionName = function.FunctionName,
            Arguments = function.Arguments,
            IsDistinct = function.IsDistinct,
            FilterWhereConditions = function.FilterWhereConditions,
            Window = function.Window
        };
        var specialized = SpecializedFunctionTranslators.Normalize(expression, context);
        if (specialized != null)
        {
            if (context.SourceDialect != context.TargetDialect)
            {
                session?.Diagnostics.Add(new TranslationDiagnostic(
                    "SQLFUNC002",
                    DiagnosticSeverity.Info,
                    $"Function '{function.FunctionName}' uses specialized translation from {context.SourceDialect} to {context.TargetDialect}.",
                    function.FunctionName is "DATEADD" or "DATEDIFF"
                        && DbType is SqlAgentToolType.Postgres or SqlAgentToolType.Oracle or SqlAgentToolType.Sqlite
                        ? FunctionPortability.Emulated : FunctionPortability.Equivalent));
            }
            return MapArithmetic(specialized);
        }

        if (PortableIdentityFunctions.Contains(function.FunctionName))
            return MapFunctionCore(function);

        var normalized = SemanticFunctionNormalizer.Normalize(expression, context);
        session?.Diagnostics.AddRange(normalized.Diagnostics);
        if (context.SourceDialect != context.TargetDialect
            && !ReferenceEquals(normalized.Expression, expression)
            && normalized.Expression is FunctionSelectCondition translated
            && !translated.FunctionName.Equals(function.FunctionName, StringComparison.OrdinalIgnoreCase))
        {
            session?.Diagnostics.Add(new TranslationDiagnostic(
                "SQLFUNC002", DiagnosticSeverity.Info,
                $"Function '{function.FunctionName}' was translated from {context.SourceDialect} to {context.TargetDialect}.",
                FunctionPortability.Equivalent));
        }
        if (normalized.Expression is FunctionSelectCondition semanticFunction)
            return MapFunctionCore(ToFunc(
                semanticFunction.FunctionName,
                semanticFunction.Arguments,
                semanticFunction.IsDistinct,
                semanticFunction.FilterWhereConditions,
                semanticFunction.Window));
        return MapArithmetic(normalized.Expression);
    }

    /// <summary>
    /// Builds a FunctionColumn directly from a SqlFunctionCondition,
    /// WITHOUT going through semantic or specialized normalization.
    /// Used after a registry translation has already selected its final target function.
    /// </summary>
    private FunctionColumn MapFunctionCore(SqlFunctionCondition function)
    {
        var functionName = function.FunctionName?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(functionName) || !SafeFunctionNamePattern().IsMatch(functionName))
            throw new InvalidOperationException($"Invalid function name: {function.FunctionName}");

        var args = function.Arguments?.Select(MapFunctionArgument).ToList() ?? [];

        if (args.Count == 0 && functionName == "COUNT")
        {
            args = [new RawColumn { Expression = "*", Bindings = [] }];
        }

        var result = new FunctionColumn
        {
            Name = functionName,
            Arguments = args,
            IsDistinct = function.IsDistinct
        };

        ApplyWindowAndFilter(function, result);
        return result;
    }

    /// <summary>
    /// Like <see cref="MapArithmetic"/>, but routes FunctionSelectCondition through
    /// <see cref="MapFunctionCore"/> (skipping template lookup) to break the cycle
    /// that arises when a template expands into the same function it was matched on.
    /// All other expression types fall through to the normal MapArithmetic path.
    /// </summary>
    private AbstractColumn MapArithmeticFromTemplate(SelectCondition? expr)
    {
        if (expr is FunctionSelectCondition f)
            return MapFunctionCore(ToFunc(f.FunctionName, f.Arguments, f.IsDistinct, f.FilterWhereConditions, f.Window));
        return MapArithmetic(expr);
    }

    private static string NormalizeDatePartUnit(SelectCondition argument)
    {
        return TemporalFunctionTranslator.ParseUnit(argument).ToString().ToUpperInvariant();
    }


    private void ApplyWindowAndFilter(SqlFunctionCondition function, FunctionColumn result)
    {
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
                EnsureNullOrderingSupported(function.Window.OrderBy);
                result.OverOrderBy = [.. function.Window.OrderBy
                    .Select(o =>
                    {
                        var col = o switch
                        {
                            FieldOrderByCondition f => (AbstractColumn)new Column { Name = f.FieldName.Trim() },
                            FunctionOrderByCondition f => MapFunction(ToFunc(f.FunctionName, f.Arguments, f.IsDistinct, f.FilterWhereConditions)),
                            _ => null
                        };
                        if (col == null) return ((AbstractColumn?)null, string.Empty, string.Empty);
                        return (col,
                            o.Direction == SortDirection.Desc ? "desc" : "asc",
                            NullOrderingSql(o.NullOrdering));
                    })
                    .Where(x => x.Item1 != null)
                    .Select(x => (x.Item1!, x.Item2, x.Item3))];
            }

            if (function.Window.Frame != null)
                result.OverFrame = CompileWindowFrame(function.Window.Frame);

            // Empty OVER () → force SqlKata to emit OVER (PARTITION BY 1)
            if (result.OverPartitionBy == null && result.OverOrderBy == null
                && string.IsNullOrWhiteSpace(result.OverFrame))
            {
                result.OverPartitionBy = [new RawColumn { Expression = "1", Bindings = [] }];
            }
        }

    }

    private AbstractColumn MapFunctionArgument(SelectCondition argument)
    {
        return argument switch
        {
            OperationSelectCondition a => MapArithmetic(a),
            FunctionSelectCondition nf => MapFunction(ToFunc(nf.FunctionName, nf.Arguments, nf.IsDistinct, nf.FilterWhereConditions, nf.Window)),
            ConstantSelectCondition constantArg => MapConstantValue(constantArg.Constant),
            TemplateSqlTokenSelectCondition tokenArg => MapTemplateSqlToken(tokenArg),
            TemplateExtractSelectCondition extractArg => MapExtract(extractArg),
            TemplateCaseSelectCondition caseArg => MapTemplateCase(caseArg),
            CaseWhenSelectCondition caseWhenArg => MapCaseWhen(caseWhenArg.CaseWhen, caseWhenArg.ElseValue),
            CastSelectCondition castArg => MapCast(castArg),
            IntervalSelectCondition intervalArg => MapInterval(intervalArg),
            FieldSelectCondition fieldArg when !string.IsNullOrWhiteSpace(fieldArg.FieldName)
                => fieldArg.FieldName.Trim() == "*"
                    ? new RawColumn { Expression = "*", Bindings = [] }
                    : new Column { Name = fieldArg.FieldName.Trim() },
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
            BasicWhereCondition b => ApplyBasicWhere(query, b),
            ColumnCompareWhereCondition c => ApplyColumnCompareWhere(query, c),
            ExpressionWhereCondition e => ApplyExpressionWhere(query, e),
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

    private Q ApplyBasicWhere<Q>(Q query, BasicWhereCondition c)
        where Q : BaseQuery<Q>
    {
        if (string.IsNullOrWhiteSpace(c.FieldName))
            return query;

        var op = (c.Operator ?? "=").ToLowerInvariant().Replace(" ", "").Trim();

        // IN / NOT IN with Values array
        if ((op is "in" or "notin") && c.Values.Count > 0)
        {
            var vals = c.Values.Select(v => v is JsonElement je ? _valueParser.UnwrapJsonElement(je) : v).ToList();

            if (c.IsDate)
                return ApplyDateIn(query, c.FieldName, op, vals, c.IsOr);

            var negate = op == "notin";
            if (negate)
            {
                return c.IsOr
                    ? (c.IsNot ? query.OrWhereIn(c.FieldName, vals) : query.OrWhereNotIn(c.FieldName, vals))
                    : (c.IsNot ? query.WhereIn(c.FieldName, vals) : query.WhereNotIn(c.FieldName, vals));
            }

            return c.IsOr
                ? (c.IsNot ? query.OrWhereNotIn(c.FieldName, vals) : query.OrWhereIn(c.FieldName, vals))
                : (c.IsNot ? query.WhereNotIn(c.FieldName, vals) : query.WhereIn(c.FieldName, vals));
        }

        var val = c.Value is JsonElement jeV ? _valueParser.UnwrapJsonElement(jeV) : c.Value;

        if (c.IsDate)
            return ApplyDateWhere(query, c.FieldName, op, val, c.IsOr);

        if (c.IsOr)
            return query.OrWhere(q => { ApplySimpleWhere(q, c.FieldName, op, val); return q; });

        if (c.IsNot)
            return query.Not().Where(q => { ApplySimpleWhere(q, c.FieldName, op, val); return q; });

        ApplySimpleWhere(query, c.FieldName, op, val);
        return query;
    }

    private static Q ApplyColumnCompareWhere<Q>(Q query, ColumnCompareWhereCondition c)
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

    private Q ApplyExpressionWhere<Q>(Q query, ExpressionWhereCondition c)
        where Q : BaseQuery<Q>
    {
        if (c.LeftExpression == null)
            return query;

        var bindings = new List<object>();
        var leftSql = BuildHavingArgPart(c.LeftExpression, bindings);
        var op = (c.Operator ?? "=").ToLowerInvariant().Replace(" ", "").Trim();
        var rightSql = c.RightExpression != null
            ? BuildHavingArgPart(c.RightExpression, bindings)
            : "?";

        var sql = $"({leftSql}) {op} ({rightSql})";

        if (c.IsOr)
            return query.OrWhereRaw(sql, [.. bindings]);
        if (c.IsNot)
            return query.Not().WhereRaw(sql, [.. bindings]);
        return query.WhereRaw(sql, [.. bindings]);
    }

    private void ApplySimpleWhere<Q>(Q query, string field, string op, object? val)
        where Q : BaseQuery<Q>
    {
        // Auto-detect date-like strings and use WhereDate for proper type casting
        if (val is string strVal && _valueParser.TryToDateTime(strVal, out _))
        {
            ApplyDateWhere(query, field, op, strVal, false);
            return;
        }

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
                    j => ApplyOnConditions(j, join),
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
                        j => ApplyOnConditions(j, join),
                        sqlJoinType
                    );
                }
                else
                {
                    query = query.Join(
                        tableName,
                        j => ApplyOnConditions(j, join),
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

    private Join ApplyOnConditions(Join j, JoinCondition join)
    {
        if (join.Type == JoinType.Cross && (join.OnConditions == null || join.OnConditions.Count == 0))
        {
            return j;
        }

        if (join.OnConditions != null && join.OnConditions.Count > 0)
        {
            ApplyWhereConditions(j, join.OnConditions);
            return j;
        }

        throw new InvalidOperationException(
            $"Join on '{join.Alias ?? join.Table}' must have at least one OnCondition.");
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
            ExpressionHavingCondition e => ApplyExpressionHaving(query, e),
            _ => query
        };
    }

    private RawColumn MapTemplateSqlToken(TemplateSqlTokenSelectCondition token)
    {
        var value = token.Token.Replace("_", string.Empty).Trim();
        if (!Enum.TryParse<TemplateSqlToken>(value, ignoreCase: true, out var parsed))
            throw new InvalidOperationException($"Unsupported SQL token in function template: {token.Token}");

        var expression = parsed switch
        {
            TemplateSqlToken.Day => "DAY",
            TemplateSqlToken.Week => "WEEK",
            TemplateSqlToken.Month => "MONTH",
            TemplateSqlToken.Quarter => "QUARTER",
            TemplateSqlToken.Year => "YEAR",
            TemplateSqlToken.Hour => "HOUR",
            TemplateSqlToken.Minute => "MINUTE",
            TemplateSqlToken.Second => "SECOND",
            TemplateSqlToken.CurrentDate => DbType == SqlAgentToolType.MsSqlServer
                ? "CAST(CURRENT_TIMESTAMP AS date)"
                : "CURRENT_DATE",
            TemplateSqlToken.CurrentTime => DbType switch
            {
                SqlAgentToolType.MsSqlServer => "CAST(CURRENT_TIMESTAMP AS time)",
                SqlAgentToolType.Oracle => throw CapabilityError("CURRENT_TIME"),
                _ => "CURRENT_TIME"
            },
            TemplateSqlToken.CurrentTimestamp => "CURRENT_TIMESTAMP",
            TemplateSqlToken.Sysdate => "SYSDATE",
            _ => throw new InvalidOperationException($"Unsupported SQL token in function template: {token.Token}")
        };

        return new RawColumn { Expression = expression, Bindings = [] };
    }

    private ExtractColumn MapExtract(TemplateExtractSelectCondition extract) => new()
    {
        Part = NormalizeDatePartUnit(extract.Unit),
        Expression = MapArithmeticFromTemplate(extract.Expression)
    };

    private AbstractColumn MapDateDiff(DateDiffExpression expression)
    {
        var unit = expression.Unit.ToString().ToUpperInvariant();
        if (expression.Unit != SqlDatePart.Day
            && DbType is SqlAgentToolType.Postgres or SqlAgentToolType.Oracle or SqlAgentToolType.Sqlite)
            throw CapabilityError($"DATEDIFF unit {unit}");

        var start = MapArithmetic(expression.Start);
        var end = MapArithmetic(expression.End);
        var unitColumn = new RawColumn { Expression = unit, Bindings = [] };

        return DbType switch
        {
            SqlAgentToolType.MsSqlServer => new FunctionColumn
            {
                Name = "DATEDIFF", Arguments = [unitColumn, start, end]
            },
            SqlAgentToolType.MySQL => new FunctionColumn
            {
                Name = "TIMESTAMPDIFF", Arguments = [unitColumn, start, end]
            },
            SqlAgentToolType.Postgres or SqlAgentToolType.Oracle => new ArithmeticColumn
            {
                Left = end, Operator = "-", Right = start
            },
            SqlAgentToolType.Sqlite => new ArithmeticColumn
            {
                Left = new FunctionColumn { Name = "JULIANDAY", Arguments = [end] },
                Operator = "-",
                Right = new FunctionColumn { Name = "JULIANDAY", Arguments = [start] }
            },
            SqlAgentToolType.Firebird => new FirebirdDateDiffColumn
            {
                Unit = unit, Start = start, End = end
            },
            _ => throw new ArgumentOutOfRangeException(nameof(DbType))
        };
    }

    private AbstractColumn MapDateAdd(DateAddExpression expression)
    {
        var unit = expression.Unit.ToString().ToUpperInvariant();
        if (expression.Unit != SqlDatePart.Day
            && DbType is SqlAgentToolType.Postgres or SqlAgentToolType.Oracle or SqlAgentToolType.Sqlite)
            throw CapabilityError($"DATEADD unit {unit}");

        var amount = MapArithmetic(expression.Amount);
        var value = MapArithmetic(expression.Value);
        var unitColumn = new RawColumn { Expression = unit, Bindings = [] };

        return DbType switch
        {
            SqlAgentToolType.MsSqlServer => new FunctionColumn
            {
                Name = "DATEADD", Arguments = [unitColumn, amount, value]
            },
            SqlAgentToolType.MySQL => new FunctionColumn
            {
                Name = "TIMESTAMPADD", Arguments = [unitColumn, amount, value]
            },
            SqlAgentToolType.Postgres => new ArithmeticColumn
            {
                Left = value,
                Operator = "+",
                Right = new ArithmeticColumn
                {
                    Left = amount,
                    Operator = "*",
                    Right = new RawColumn { Expression = "INTERVAL '1 day'", Bindings = [] }
                }
            },
            SqlAgentToolType.Oracle => new ArithmeticColumn
            {
                Left = value, Operator = "+", Right = amount
            },
            SqlAgentToolType.Sqlite => new FunctionColumn
            {
                Name = "DATETIME",
                Arguments =
                [
                    value,
                    new FunctionColumn
                    {
                        Name = "PRINTF",
                        Arguments = [new RawColumn { Expression = "'%+d day'", Bindings = [] }, amount]
                    }
                ]
            },
            SqlAgentToolType.Firebird => new FirebirdDateAddColumn
            {
                Unit = unit, Amount = amount, Value = value
            },
            _ => throw new ArgumentOutOfRangeException(nameof(DbType))
        };
    }

    private AbstractColumn MapJsonExtract(JsonExtractExpression expression)
    {
        var value = MapArithmetic(expression.Value);
        var dollarPath = MapConstantValue(expression.Path.RenderDollarPath());
        return DbType switch
        {
            SqlAgentToolType.MsSqlServer or SqlAgentToolType.Oracle =>
                throw CapabilityError("ambiguous JSON_EXTRACT result type; use JSON_VALUE or JSON_QUERY"),
            SqlAgentToolType.MySQL or SqlAgentToolType.Sqlite =>
                new FunctionColumn { Name = "JSON_EXTRACT", Arguments = [value, dollarPath] },
            SqlAgentToolType.Postgres => new FunctionColumn
            {
                Name = "JSONB_EXTRACT_PATH",
                Arguments =
                [
                    new CastColumn { Expression = value, TypeName = "jsonb" },
                    .. expression.Path.RenderSegments().Select(segment => MapConstantValue(segment))
                ]
            },
            SqlAgentToolType.Firebird => throw CapabilityError("JSON_EXTRACT"),
            _ => throw new ArgumentOutOfRangeException(nameof(DbType))
        };
    }

    private AbstractColumn MapJsonSet(JsonSetExpression expression)
    {
        var value = MapArithmetic(expression.Value);
        var newValue = MapArithmetic(expression.NewValue);
        var dollarPath = MapConstantValue(expression.Path.RenderDollarPath());
        return DbType switch
        {
            SqlAgentToolType.MsSqlServer =>
                new FunctionColumn { Name = "JSON_MODIFY", Arguments = [value, dollarPath, newValue] },
            SqlAgentToolType.MySQL or SqlAgentToolType.Sqlite =>
                new FunctionColumn { Name = "JSON_SET", Arguments = [value, dollarPath, newValue] },
            SqlAgentToolType.Postgres => new FunctionColumn
            {
                Name = "JSONB_SET",
                Arguments =
                [
                    new CastColumn { Expression = value, TypeName = "jsonb" },
                    new CastColumn
                    {
                        Expression = MapConstantValue(expression.Path.RenderPostgresPath()),
                        TypeName = "text[]"
                    },
                    new FunctionColumn { Name = "TO_JSONB", Arguments = [newValue] }
                ]
            },
            SqlAgentToolType.Oracle or SqlAgentToolType.Firebird => throw CapabilityError("JSON_SET"),
            _ => throw new ArgumentOutOfRangeException(nameof(DbType))
        };
    }

    private AbstractColumn MapRegexMatch(RegexMatchExpression expression)
    {
        if (DbType is SqlAgentToolType.MsSqlServer or SqlAgentToolType.Sqlite or SqlAgentToolType.Firebird)
            throw CapabilityError("REGEXP_LIKE");
        return new FunctionColumn
        {
            Name = "REGEXP_LIKE",
            Arguments = [MapArithmetic(expression.Value), MapArithmetic(expression.Pattern)]
        };
    }

    private AbstractColumn MapDateFormat(DateFormatExpression expression)
    {
        if (DbType == SqlAgentToolType.Firebird) throw CapabilityError("portable date formatting");
        var value = MapArithmetic(expression.Value);
        var format = MapConstantValue(DateFormatTranslator.Render(expression.Format, DbType));
        return DbType switch
        {
            SqlAgentToolType.MsSqlServer => new FunctionColumn { Name = "FORMAT", Arguments = [value, format] },
            SqlAgentToolType.Postgres or SqlAgentToolType.Oracle =>
                new FunctionColumn { Name = "TO_CHAR", Arguments = [value, format] },
            SqlAgentToolType.MySQL => new FunctionColumn { Name = "DATE_FORMAT", Arguments = [value, format] },
            SqlAgentToolType.Sqlite => new FunctionColumn { Name = "STRFTIME", Arguments = [format, value] },
            _ => throw new ArgumentOutOfRangeException(nameof(DbType))
        };
    }

    private AbstractColumn MapFormattedDateParse(FormattedDateParseExpression expression)
    {
        if (DbType is SqlAgentToolType.Sqlite or SqlAgentToolType.MsSqlServer or SqlAgentToolType.Firebird)
            throw CapabilityError("formatted date parsing");
        var value = MapArithmetic(expression.Value);
        var format = MapConstantValue(DateFormatTranslator.Render(expression.Format, DbType));
        if (DbType == SqlAgentToolType.MySQL)
            return new FunctionColumn
            {
                Name = "DATE",
                Arguments = [new FunctionColumn { Name = "STR_TO_DATE", Arguments = [value, format] }]
            };
        return new FunctionColumn { Name = "TO_DATE", Arguments = [value, format] };
    }

    private AbstractColumn MapPosition(PositionExpression expression)
    {
        var haystack = MapArithmetic(expression.Haystack);
        var needle = MapArithmetic(expression.Needle);
        return DbType switch
        {
            SqlAgentToolType.MsSqlServer => new FunctionColumn { Name = "CHARINDEX", Arguments = [needle, haystack] },
            SqlAgentToolType.Postgres => new FunctionColumn { Name = "STRPOS", Arguments = [haystack, needle] },
            SqlAgentToolType.MySQL => new FunctionColumn { Name = "LOCATE", Arguments = [needle, haystack] },
            SqlAgentToolType.Sqlite or SqlAgentToolType.Oracle =>
                new FunctionColumn { Name = "INSTR", Arguments = [haystack, needle] },
            SqlAgentToolType.Firebird => new FunctionColumn { Name = "POSITION", Arguments = [needle, haystack] },
            _ => throw new ArgumentOutOfRangeException(nameof(DbType))
        };
    }

    private AbstractColumn MapDatePart(DatePartExpression expression)
    {
        var part = expression.Part.ToString().ToUpperInvariant();
        var value = MapArithmetic(expression.Value);
        return DbType switch
        {
            SqlAgentToolType.MsSqlServer or SqlAgentToolType.MySQL =>
                new FunctionColumn { Name = part, Arguments = [value] },
            SqlAgentToolType.Postgres or SqlAgentToolType.Oracle or SqlAgentToolType.Firebird =>
                new ExtractColumn { Part = part, Expression = value },
            SqlAgentToolType.Sqlite => new CastColumn
            {
                Expression = new FunctionColumn
                {
                    Name = "STRFTIME",
                    Arguments =
                    [
                        MapConstantValue(expression.Part switch
                        {
                            SqlDatePart.Year => "%Y",
                            SqlDatePart.Month => "%m",
                            SqlDatePart.Day => "%d",
                            _ => throw CapabilityError($"date part {part}")
                        }),
                        value
                    ]
                },
                TypeName = "INTEGER"
            },
            _ => throw new ArgumentOutOfRangeException(nameof(DbType))
        };
    }

    private ExpressionCaseColumn MapTemplateCase(TemplateCaseSelectCondition expression) => new()
    {
        Cases = [.. expression.Cases.Select(branch =>
            (MapArithmeticFromTemplate(branch.Condition), MapArithmeticFromTemplate(branch.Value)))],
        ElseExpression = expression.ElseExpression == null
            ? null
            : MapArithmeticFromTemplate(expression.ElseExpression)
    };

    private Query ApplyGroupHaving(Query query, GroupHavingCondition g)
    {
        if (g.Groups?.Count == 0)
            return query;

        if (g.Groups?.Count == 1)
            return ApplySingleHaving(query, g.Groups[0]);

        if (g.IsOr)
        {
            return query.Having(q =>
            {
                var first = true;
                foreach (var c in g.Groups ?? [])
                {
                    if (first)
                    {
                        ApplySingleHaving(q, c);
                        first = false;
                    }
                    else
                    {
                        c.IsOr = true;
                        ApplySingleHaving(q, c);
                        c.IsOr = false;
                    }
                }
                return q;
            });
        }

        return query.Having(q => ApplyHavingConditions(q, g.Groups));
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

        // Auto-convert date-like strings to DateTime for proper type handling
        if (val is string strVal && _valueParser.TryToDateTime(strVal, out var dtVal))
        {
            bindings.Add(dtVal);
            return baseQuery.HavingRaw($"{expr} {sqlOp} ?::date", [.. bindings]);
        }

        bindings.Add(val!);
        return baseQuery.HavingRaw($"{expr} {sqlOp} ?", [.. bindings]);
    }

    private Query ApplyExpressionHaving(Query query, ExpressionHavingCondition c)
    {
        if (c.LeftExpression == null)
            return query;

        var bindings = new List<object>();
        var leftSql = BuildHavingArgPart(c.LeftExpression, bindings);
        var op = (c.Operator ?? "=").ToLowerInvariant().Replace(" ", "").Trim();
        var sqlOp = op switch
        {
            "is" or "isnull" => "IS NULL",
            "isnot" or "isnotnull" => "IS NOT NULL",
            _ => op
        };

        var baseQuery = c.IsOr ? query.Or() : query;
        baseQuery = c.IsNot ? baseQuery.Not() : baseQuery;

        if (sqlOp is "IS NULL" or "IS NOT NULL")
            return baseQuery.HavingRaw($"{leftSql} {sqlOp}", [.. bindings]);

        var rightSql = c.RightExpression != null
            ? BuildHavingArgPart(c.RightExpression, bindings)
            : "?";

        return baseQuery.HavingRaw($"{leftSql} {sqlOp} {rightSql}", [.. bindings]);
    }

    private string BuildHavingFuncExpr(SqlFunctionCondition func, List<object> bindings)
    {
        var compiled = CreateCompiler().Compile(new Query().Select(MapFunction(func)));
        const string selectPrefix = "SELECT ";
        if (!compiled.RawSql.StartsWith(selectPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unable to compile HAVING function expression.");
        bindings.AddRange(compiled.Bindings);
        return compiled.RawSql[selectPrefix.Length..].Trim();
    }

    private string BuildHavingArgPart(SelectCondition arg, List<object> bindings)
    {
        return arg switch
        {
            FieldSelectCondition f => f.FieldName,
            ConstantSelectCondition c => BuildHavingConstPart(c.Constant, bindings),
            CastSelectCondition cast => $"CAST({BuildHavingArgPart(cast.Expression, bindings)} AS {ValidateCastType(cast.TypeName)})",
            IntervalSelectCondition interval => BuildHavingIntervalPart(interval),
            FunctionSelectCondition n => BuildHavingFuncExpr(
                new SqlFunctionCondition
                {
                    FunctionName = n.FunctionName,
                    Arguments = n.Arguments,
                    IsDistinct = n.IsDistinct,
                    FilterWhereConditions = n.FilterWhereConditions
                },
                bindings),
            OperationSelectCondition a when a.Left != null => BuildHavingArithPart(a, bindings),
            CaseWhenSelectCondition => throw new InvalidOperationException(
                "CASE WHEN is not supported in HAVING clause function arguments. " +
                "Use it in SELECT columns instead."),
            _ => "?"
        };
    }

    private string BuildHavingConstPart(object constant, List<object> bindings)
    {
        var val = constant is JsonElement je ? _valueParser.UnwrapJsonElement(je) : constant;
        bindings.Add(val);
        return "?";
    }

    private string BuildHavingArithPart(OperationSelectCondition op, List<object> bindings)
    {
        var left = op.Left switch
        {
            FieldSelectCondition f => f.FieldName,
            ConstantSelectCondition c => BuildHavingConstPart(c.Constant, bindings),
            FunctionSelectCondition fn => BuildHavingFuncExpr(new SqlFunctionCondition
            {
                FunctionName = fn.FunctionName,
                Arguments = fn.Arguments,
                IsDistinct = fn.IsDistinct,
                FilterWhereConditions = fn.FilterWhereConditions,
                Window = fn.Window,
            }, bindings),
            OperationSelectCondition nested => BuildHavingArithPart(nested, bindings),
            _ => "?"
        };
        var right = op.Right switch
        {
            FieldSelectCondition f => f.FieldName,
            ConstantSelectCondition c => BuildHavingConstPart(c.Constant, bindings),
            FunctionSelectCondition fn => BuildHavingFuncExpr(new SqlFunctionCondition
            {
                FunctionName = fn.FunctionName,
                Arguments = fn.Arguments,
                IsDistinct = fn.IsDistinct,
                FilterWhereConditions = fn.FilterWhereConditions,
                Window = fn.Window,
            }, bindings),
            OperationSelectCondition nested => BuildHavingArithPart(nested, bindings),
            _ => "?"
        };
        if (op.Operator == ArithmeticOperator.Modulo
            && DbType is SqlAgentToolType.Oracle or SqlAgentToolType.Firebird)
            return $"MOD({left}, {right})";
        if (op.Operator == ArithmeticOperator.Concat && DbType == SqlAgentToolType.MySQL)
            return $"CONCAT({left}, {right})";
        var operatorText = op.Operator == ArithmeticOperator.Concat && DbType == SqlAgentToolType.MsSqlServer
            ? "+"
            : GetOperatorString(op.Operator);
        return $"({left} {operatorText} {right})";
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

    private Query ApplyOrderByColumns(Query query, List<OrderByCondition> cols)
    {
        if (cols == null || cols.Count == 0)
            return query;

        foreach (var col in cols)
        {
            EnsureNullOrderingSupported([col]);
            switch (col)
            {
                case FieldOrderByCondition f:
                    query = ApplyOrderByField(query, f.FieldName?.Trim() ?? string.Empty, f.Direction, f.NullOrdering);
                    break;

                case FunctionOrderByCondition f:
                    query = ApplyOrderByFunction(
                        query,
                        MapFunction(ToFunc(f.FunctionName, f.Arguments, f.IsDistinct, f.FilterWhereConditions)),
                        f.Direction,
                        f.NullOrdering);
                    break;
            }
        }

        return query;
    }

    private static Query ApplyOrderByField(
        Query query,
        string field,
        SortDirection direction,
        NullOrdering nullOrdering)
    {
        if (string.IsNullOrWhiteSpace(field))
            return query;

        if (nullOrdering != NullOrdering.Default)
            return query.OrderBy(
                new Column { Name = field },
                direction != SortDirection.Desc,
                NullOrderingSql(nullOrdering));

        return direction switch
        {
            SortDirection.Random => query.OrderByRandom(field),
            SortDirection.Desc => query.OrderByDesc(field),
            _ => query.OrderBy(field)
        };
    }

    private static Query ApplyOrderByFunction(
        Query query,
        AbstractColumn function,
        SortDirection direction,
        NullOrdering nullOrdering)
    {
        if (nullOrdering != NullOrdering.Default)
            return query.OrderBy(function, direction != SortDirection.Desc, NullOrderingSql(nullOrdering));
        return direction switch
        {
            SortDirection.Desc => query.OrderByDesc(function),
            _ => query.OrderBy(function)
        };
    }

    private void EnsureNullOrderingSupported(IEnumerable<OrderByCondition> orderBy)
    {
        if (!orderBy.Any(x => x.NullOrdering != NullOrdering.Default))
            return;
        if (DbType is SqlAgentToolType.MySQL or SqlAgentToolType.MsSqlServer)
            throw CapabilityError("NULLS FIRST/LAST ordering");
    }

    private static string NullOrderingSql(NullOrdering nullOrdering) => nullOrdering switch
    {
        NullOrdering.First => "first",
        NullOrdering.Last => "last",
        _ => string.Empty
    };

    private static string CompileWindowFrame(WindowFrameDefinition frame)
    {
        ValidateWindowFrame(frame);
        var unit = frame.Unit == WindowFrameUnit.Rows ? "ROWS" : "RANGE";
        var start = CompileWindowFrameBound(frame.Start);
        return frame.End == null
            ? $"{unit} {start}"
            : $"{unit} BETWEEN {start} AND {CompileWindowFrameBound(frame.End)}";
    }

    private static void ValidateWindowFrame(WindowFrameDefinition frame)
    {
        if (frame.Start == null)
            throw new InvalidOperationException("Window frame start bound is required.");
        if (frame.Start.Kind == WindowFrameBoundKind.UnboundedFollowing)
            throw new InvalidOperationException("Window frame cannot start with UNBOUNDED FOLLOWING.");
        if (frame.End?.Kind == WindowFrameBoundKind.UnboundedPreceding)
            throw new InvalidOperationException("Window frame cannot end with UNBOUNDED PRECEDING.");
        ValidateWindowFrameOffset(frame.Start);
        if (frame.End != null) ValidateWindowFrameOffset(frame.End);
    }

    private static void ValidateWindowFrameOffset(WindowFrameBound bound)
    {
        var requiresOffset = bound.Kind is WindowFrameBoundKind.Preceding or WindowFrameBoundKind.Following;
        if (requiresOffset && bound.Offset is null or < 0)
            throw new InvalidOperationException("Window PRECEDING/FOLLOWING bound requires a non-negative offset.");
        if (!requiresOffset && bound.Offset != null)
            throw new InvalidOperationException("Window frame offset is only valid for PRECEDING/FOLLOWING bounds.");
    }

    private static string CompileWindowFrameBound(WindowFrameBound bound) => bound.Kind switch
    {
        WindowFrameBoundKind.UnboundedPreceding => "UNBOUNDED PRECEDING",
        WindowFrameBoundKind.Preceding => $"{bound.Offset} PRECEDING",
        WindowFrameBoundKind.CurrentRow => "CURRENT ROW",
        WindowFrameBoundKind.Following => $"{bound.Offset} FOLLOWING",
        WindowFrameBoundKind.UnboundedFollowing => "UNBOUNDED FOLLOWING",
        _ => throw new ArgumentOutOfRangeException(nameof(bound.Kind), bound.Kind, "Unknown window frame bound.")
    };

    // =====================================================================
    // DML helpers
    // =====================================================================

    private Query BuildDmlSourceQuery(DmlDefinition dml)
    {
        var query = new Query(dml.TableName);
        return dml.WhereConditions?.Count > 0
            ? ApplyWhereConditions(query, dml.WhereConditions)
            : query;
    }

    private async Task<(int AffectedRows, string Preview)> PreviewDmlAsync(
        QueryFactory db,
        DmlDefinition dml,
        CancellationToken cancellationToken)
    {
        const int previewRowLimit = 20;

        if (dml.Operation == DmlOperation.Insert && dml.FromQuery == null)
        {
            var insertRows = BuildInsertPreviewRows(dml).ToList();
            return (insertRows.Count, FormatRowsDiffPreview(
                dml.TableName, "INSERT preview", insertRows.Take(previewRowLimit), '+'));
        }

        var source = dml.Operation == DmlOperation.Insert
            ? BuildQueryFromDefinition(dml.FromQuery!)
            : BuildDmlSourceQuery(dml);
        var affected = await db.CountAsync<long>(source.Clone(), cancellationToken: cancellationToken);
        if (affected > int.MaxValue)
            throw new InvalidOperationException($"DML preview matched {affected} rows, exceeding the supported maximum.");

        var previewRows = await db.GetAsync(
            source.Clone().Limit(previewRowLimit),
            cancellationToken: cancellationToken);
        var rows = previewRows
            .Select(row => ((IDictionary<string, object>)row).ToDictionary(
                pair => pair.Key,
                pair => (object?)pair.Value,
                StringComparer.OrdinalIgnoreCase))
            .ToList();

        var preview = dml.Operation switch
        {
            DmlOperation.Update => FormatUpdatePreview(rows, dml),
            DmlOperation.Delete => FormatRowsDiffPreview(dml.TableName, "DELETE preview", rows, '-'),
            DmlOperation.Insert => FormatRowsDiffPreview(dml.TableName, "INSERT preview", rows, '+'),
            _ => throw new ArgumentOutOfRangeException(nameof(dml.Operation), dml.Operation, "Unknown DML operation.")
        };
        return ((int)affected, preview);
    }

    private string FormatUpdatePreview(
        IReadOnlyList<Dictionary<string, object?>> rows,
        DmlDefinition dml)
    {
        var builder = new StringBuilder("### UPDATE preview");
        if (rows.Count == 0)
            return builder.AppendLine().Append("_No matching rows._").ToString();

        builder.AppendLine().AppendLine("```diff");
        builder.Append("Table: ").AppendLine(FormatCodeValue(dml.TableName));
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var before = rows[rowIndex];
            var after = ApplyUpdateValues(before, dml.Values);
            var identifier = FindPreviewIdentifier(before, dml.TableName);
            builder.Append("@@ ").Append(FormatCodeValue(identifier.Key)).Append(" = ")
                .Append(FormatCodeValue(identifier.Value)).AppendLine(" @@");
            foreach (var value in dml.Values ?? [])
            {
                var column = before.Keys.FirstOrDefault(key =>
                    string.Equals(key, value.FieldName, StringComparison.OrdinalIgnoreCase)) ?? value.FieldName;
                before.TryGetValue(column, out var beforeValue);
                after.TryGetValue(column, out var afterValue);
                builder.Append('-').Append(FormatCodeValue(column)).Append(": ")
                    .AppendLine(FormatCodeValue(beforeValue));
                builder.Append('+').Append(FormatCodeValue(column)).Append(": ")
                    .AppendLine(FormatCodeValue(afterValue));
            }
        }
        return builder.Append("```").ToString();
    }

    private static KeyValuePair<string, object?> FindPreviewIdentifier(
        IDictionary<string, object?> row,
        string tableName)
        => TryFindPreviewIdentifier(row, tableName) ?? row.First();

    private static KeyValuePair<string, object?>? TryFindPreviewIdentifier(
        IDictionary<string, object?> row,
        string tableName)
    {
        var unqualifiedTable = tableName.Split('.').Last();
        var singularTable = unqualifiedTable.EndsWith('s')
            ? unqualifiedTable[..^1]
            : unqualifiedTable;
        var candidates = new[] { "id", $"{singularTable}_id", $"{unqualifiedTable}_id" };
        foreach (var candidate in candidates)
        {
            var match = row.FirstOrDefault(pair =>
                string.Equals(pair.Key, candidate, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(match.Key))
                return match;
        }

        var foreignKey = row.FirstOrDefault(pair =>
            pair.Key.EndsWith("_id", StringComparison.OrdinalIgnoreCase));
        return !string.IsNullOrEmpty(foreignKey.Key) ? foreignKey : null;
    }

    private static string FormatCodeValue(object? value)
    {
        var text = value switch
        {
            null => "null",
            SqlDateValue date => date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            SqlTimeValue time => time.Value.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
            SqlLocalDateTimeValue timestamp => timestamp.Value.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
            SqlOffsetDateTimeValue timestamp => timestamp.Value.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture),
            DateOnly dateOnly => dateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
            byte[] bytes => Convert.ToBase64String(bytes),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
        };
        return text.Replace("\r\n", " ↵ ", StringComparison.Ordinal)
            .Replace("\n", " ↵ ", StringComparison.Ordinal)
            .Replace("\r", " ↵ ", StringComparison.Ordinal)
            .Replace("```", "` ` `", StringComparison.Ordinal);
    }

    private static string FormatRowsDiffPreview(
        string tableName,
        string title,
        IEnumerable<IDictionary<string, object?>> sourceRows,
        char prefix)
    {
        var rows = sourceRows.ToList();
        var builder = new StringBuilder("### ").Append(title);
        if (rows.Count == 0)
            return builder.AppendLine().Append("_No rows._").ToString();

        builder.AppendLine().AppendLine("```diff");
        builder.Append("Table: ").AppendLine(FormatCodeValue(tableName));
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            var identifier = TryFindPreviewIdentifier(row, tableName);
            if (identifier is { } found)
                builder.Append("@@ ").Append(FormatCodeValue(found.Key)).Append(" = ")
                    .Append(FormatCodeValue(found.Value)).AppendLine(" @@");
            else
                builder.Append("@@ new row ").Append(rowIndex + 1).AppendLine(" @@");

            foreach (var (column, value) in row)
                builder.Append(prefix).Append(FormatCodeValue(column)).Append(": ")
                    .AppendLine(FormatCodeValue(value));
        }
        return builder.Append("```").ToString();
    }

    private Dictionary<string, object?> ApplyUpdateValues(
        IDictionary<string, object?> source,
        List<NameValuePair>? values)
    {
        var result = new Dictionary<string, object?>(source, StringComparer.OrdinalIgnoreCase);
        foreach (var value in values ?? [])
        {
            var existingKey = result.Keys.FirstOrDefault(key =>
                string.Equals(key, value.FieldName, StringComparison.OrdinalIgnoreCase));
            result[existingKey ?? value.FieldName] = UnwrapDmlValue(value.Value);
        }
        return result;
    }

    private IEnumerable<IDictionary<string, object?>> BuildInsertPreviewRows(DmlDefinition dml)
    {
        if (dml.MultiValues?.Count > 0)
        {
            var columns = dml.Columns ?? [];
            foreach (var values in dml.MultiValues)
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < columns.Count && i < values.Count; i++)
                    row[columns[i]] = UnwrapDmlValue(values[i]);
                yield return row;
            }
            yield break;
        }

        if (dml.Values?.Count > 0)
        {
            yield return dml.Values.ToDictionary(
                value => value.FieldName,
                value => UnwrapDmlValue(value.Value),
                StringComparer.OrdinalIgnoreCase);
        }
    }

    private object? UnwrapDmlValue(object? value)
        => value is JsonElement json ? _valueParser.UnwrapJsonElement(json) : value;

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

    private string GenerateConfirmToken(DmlDefinition dml, int affectedRows)
    {
        var secret = _configuration["McpKeySettings:HmacSecretKey"]
                     ?? throw new InvalidOperationException("McpKeySettings:HmacSecretKey is required for DML confirmation.");
        // Bind approval to the complete parsed operation, not merely table and row
        // count. ConfirmToken itself is deliberately excluded from the payload.
        var payload = JsonSerializer.Serialize(new
        {
            dml.Operation,
            TableName = dml.TableName.ToLowerInvariant(),
            dml.Columns,
            dml.Values,
            dml.MultiValues,
            dml.WhereConditions,
            dml.FromQuery,
            AffectedRows = affectedRows
        });
        using var hmac = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)))[..24];
    }

    private static string ValidateCastType(string typeName)
    {
        var normalized = typeName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized) || !SafeCastTypePattern().IsMatch(normalized))
            throw new InvalidOperationException($"Unsupported or unsafe CAST type '{typeName}'.");
        return normalized.ToUpperInvariant();
    }

    private string BuildHavingIntervalPart(IntervalSelectCondition interval)
    {
        if (DbType != SqlAgentToolType.Postgres)
            throw CapabilityError("INTERVAL expressions");
        if (string.IsNullOrWhiteSpace(interval.Literal))
            throw new InvalidOperationException("INTERVAL literal must not be empty.");
        return $"INTERVAL '{interval.Literal.Replace("'", "''", StringComparison.Ordinal)}'";
    }

    // =====================================================================
    // Error / Serialization helpers
    // =====================================================================

    protected virtual string SerializeQueryResult(IEnumerable<dynamic> result)
    {
        var list = result
            .Select(r => ((IDictionary<string, object>)r).ToDictionary(
                pair => pair.Key,
                pair => NormalizeQueryResultValue(pair.Value)))
            .ToList();
        return JsonSerializer.Serialize(list);
    }

    private static object? NormalizeQueryResultValue(object? value) => value switch
    {
        FbZonedDateTime zoned => new DateTimeOffset(
                DateTime.SpecifyKind(zoned.DateTime, DateTimeKind.Unspecified),
                zoned.Offset ?? TimeSpan.Zero)
            .ToString("O", CultureInfo.InvariantCulture),
        FbZonedTime zoned => zoned.Offset is { } offset
            ? $"{TimeOnly.FromTimeSpan(zoned.Time):HH:mm:ss.fffffff}{FormatUtcOffset(offset)}"
            : $"{TimeOnly.FromTimeSpan(zoned.Time):HH:mm:ss.fffffff}",
        _ => value
    };

    private static string FormatUtcOffset(TimeSpan offset) =>
        $"{(offset < TimeSpan.Zero ? '-' : '+')}{offset.Duration():hh\\:mm}";

    protected virtual string BuildExecutionErrorMessage(Exception ex, string type)
    {
        return $"Error executing query | message={ex.GetBaseException().Message}";
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
