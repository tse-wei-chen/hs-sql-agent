using System.Collections.Immutable;
using System.Text.Json;
using HsSqlAgent.SqlCore.Core.Ast;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Execution;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Models;
using SqlKata;
using SqlKata.Compilers;

namespace HsSqlAgent.SqlCore.Core.Lowering;

/// <summary>
/// Structured SqlKata backend for canonical INSERT. Literal VALUES and INSERT..SELECT stay in
/// SqlKata's structured query IR. VALUES rows containing scalar expressions are rendered only from
/// validated Core AST nodes and then passed through the Core-owned SqlKata parameter preparation
/// contract, so runtime values remain bindings rather than inline SQL.
/// </summary>
public sealed class SqlKataInsertLowerer(SqlAgentToolType provider)
{
    private const string ProjectionAlias = "__core_insert_value";

    public CompiledSqlCommand Lower(
        ExecutableSqlPlan plan,
        InsertStatement insert)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(insert);
        if (plan.TargetProvider != provider)
            throw new SqlCompilationException(
                $"Plan targets {plan.TargetProvider}, but this INSERT lowerer targets {provider}.");

        if (insert.Columns.IsDefaultOrEmpty)
            throw new SqlCompilationException("INSERT requires at least one target column.");

        var compiler = SqlKataProviderLowerer.CreateCompiler(provider);
        SqlResult result;

        if (insert.Source is InsertValuesSource expressionValues
            && ContainsStructuredExpression(expressionValues))
        {
            result = LowerExpressionValues(insert, expressionValues, compiler);
        }
        else
        {
            var columns = insert.Columns
                .Select(column => CoreIdentifierSqlRenderer.NormalizeSinglePart(
                    column,
                    compiler,
                    "INSERT target column"))
                .ToArray();

            Query query = insert.Source switch
            {
                InsertValuesSource values => LowerLiteralValues(insert, columns, values, compiler),
                InsertQuerySource querySource => LowerQuerySource(insert, columns, querySource, compiler),
                _ => throw new SqlCompilationException(
                    $"Unsupported INSERT source during lowering: {insert.Source.GetType().Name}")
            };
            result = compiler.Compile(query);
        }

        var parameters = result.NamedBindings
            .OrderBy(pair => ParameterOrdinal(pair.Key))
            .Select(pair => new SqlParameterValue(pair.Key, NormalizeLiteral(pair.Value)))
            .ToImmutableArray();
        var command = new CompiledSqlCommand(
            result.Sql,
            parameters,
            SqlStatementKind.Insert,
            string.Empty,
            provider);
        return command with
        {
            PlanFingerprint = DmlFingerprintService.ComputePlanFingerprint(command, plan.PolicyVersion)
        };
    }

    private static bool ContainsStructuredExpression(InsertValuesSource values) =>
        values.Rows.SelectMany(row => row).Any(value => value is not LiteralExpr);

    private static Query LowerLiteralValues(
        InsertStatement insert,
        string[] columns,
        InsertValuesSource values,
        Compiler compiler)
    {
        if (values.Rows.IsDefaultOrEmpty)
            throw new SqlCompilationException("INSERT VALUES requires at least one row.");

        var rows = values.Rows.Select((row, index) =>
        {
            if (row.Length != columns.Length)
            {
                throw new SqlCompilationException(
                    $"INSERT row {index + 1} has {row.Length} values but {columns.Length} columns were declared.");
            }

            return row.Select(value => value switch
            {
                LiteralExpr literal => NormalizeLiteral(literal.Value),
                _ => throw new SqlCompilationException(
                    $"Literal INSERT path received structured expression {value.GetType().Name}.")
            }).ToArray();
        }).ToArray();

        return NewTargetQuery(insert.Target.Name, compiler).AsInsert(columns, rows);
    }

    private static SqlResult LowerExpressionValues(
        InsertStatement insert,
        InsertValuesSource values,
        Compiler compiler)
    {
        if (values.Rows.IsDefaultOrEmpty)
            throw new SqlCompilationException("INSERT VALUES requires at least one row.");

        var table = CoreIdentifierSqlRenderer.Render(
            insert.Target.Name,
            compiler,
            allowWildcard: false);
        var columns = insert.Columns
            .Select(column => CoreIdentifierSqlRenderer.Render(
                column,
                compiler,
                allowWildcard: false))
            .ToArray();
        var columnSql = string.Join(", ", columns);
        var bindings = ImmutableArray.CreateBuilder<object?>();
        var rows = new List<RenderedRow>(values.Rows.Length);

        for (var rowIndex = 0; rowIndex < values.Rows.Length; rowIndex++)
        {
            var row = values.Rows[rowIndex];
            if (row.Length != columns.Length)
            {
                throw new SqlCompilationException(
                    $"INSERT row {rowIndex + 1} has {row.Length} values but {columns.Length} columns were declared.");
            }

            var fragments = row.Select(value => RenderProjectionExpression(value, compiler)).ToArray();
            foreach (var fragment in fragments)
                bindings.AddRange(fragment.Bindings);
            rows.Add(new RenderedRow(fragments.Select(fragment => fragment.Sql).ToImmutableArray()));
        }

        var rawSql = compiler switch
        {
            OracleCompiler when rows.Count > 1 => RenderOracleMultiRow(table, columnSql, rows),
            FirebirdCompiler when rows.Count > 1 => RenderFirebirdMultiRow(table, columnSql, rows),
            _ => RenderValuesRows(table, columnSql, rows)
        };

        return CoreSqlKataRawCompiler.Prepare(
            compiler,
            rawSql,
            bindings.ToImmutable());
    }

    private static string RenderValuesRows(
        string table,
        string columns,
        IReadOnlyList<RenderedRow> rows) =>
        $"INSERT INTO {table} ({columns}) VALUES " +
        string.Join(", ", rows.Select(row => $"({string.Join(", ", row.Expressions)})"));

    private static string RenderOracleMultiRow(
        string table,
        string columns,
        IReadOnlyList<RenderedRow> rows) =>
        "INSERT ALL" +
        string.Concat(rows.Select(row =>
            $" INTO {table} ({columns}) VALUES ({string.Join(", ", row.Expressions)})")) +
        " SELECT 1 FROM DUAL";

    private static string RenderFirebirdMultiRow(
        string table,
        string columns,
        IReadOnlyList<RenderedRow> rows) =>
        $"INSERT INTO {table} ({columns}) " +
        string.Join(
            " UNION ALL ",
            rows.Select(row =>
                $"SELECT {string.Join(", ", row.Expressions)} FROM RDB$DATABASE"));

    private static RenderedExpression RenderProjectionExpression(
        SqlExpr expression,
        Compiler compiler)
    {
        var select = EmptySelect(
            new SelectItem(
                expression,
                new IdentifierPart(
                    ProjectionAlias,
                    WasQuoted: false,
                    SourceSpan.Unknown,
                    PreserveSpelling: true),
                expression.Span));
        var result = compiler.Compile(SqlKataProviderLowerer.BuildQuery(select, compiler));
        var marker = " AS " + compiler.WrapValue(ProjectionAlias);
        var markerIndex = result.Sql.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        const string prefix = "SELECT ";
        if (!result.Sql.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || markerIndex < prefix.Length)
        {
            throw new SqlCompilationException(
                "Core INSERT expression rendering could not isolate the compiled SELECT projection.");
        }

        var fragment = result.Sql[prefix.Length..markerIndex].Trim();
        return new RenderedExpression(
            ToPositionalSql(fragment, result.NamedBindings),
            OrderedValues(result.NamedBindings));
    }

    private static SelectStatement EmptySelect(SelectItem item) =>
        new(
            Ctes: ImmutableArray<CteDefinition>.Empty,
            Distinct: false,
            Select: ImmutableArray.Create(item),
            From: null,
            Joins: ImmutableArray<JoinSource>.Empty,
            Where: null,
            GroupBy: ImmutableArray<SqlExpr>.Empty,
            Having: null,
            OrderBy: ImmutableArray<OrderByItem>.Empty,
            Limit: null,
            Offset: null,
            Span: item.Span);

    private static string ToPositionalSql(
        string sql,
        IReadOnlyDictionary<string, object> bindings)
    {
        foreach (var pair in bindings.OrderByDescending(pair => ParameterOrdinal(pair.Key)))
            sql = sql.Replace(pair.Key, "?", StringComparison.Ordinal);
        return sql;
    }

    private static ImmutableArray<object?> OrderedValues(
        IReadOnlyDictionary<string, object> bindings) =>
        bindings
            .OrderBy(pair => ParameterOrdinal(pair.Key))
            .Select(pair => NormalizeLiteral(pair.Value))
            .ToImmutableArray();

    private static Query LowerQuerySource(
        InsertStatement insert,
        string[] columns,
        InsertQuerySource source,
        Compiler compiler)
    {
        var sourceQuery = SqlKataProviderLowerer.BuildQuery(source.Query, compiler);
        return NewTargetQuery(insert.Target.Name, compiler).AsInsert(columns, sourceQuery);
    }

    private static Query NewTargetQuery(SqlIdentifier identifier, Compiler compiler) =>
        new Query().FromRaw(CoreIdentifierSqlRenderer.Render(identifier, compiler, allowWildcard: false));

    private static object? NormalizeLiteral(object? value)
    {
        if (value is JsonElement json)
        {
            return json.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                JsonValueKind.String => json.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number when json.TryGetInt64(out var integer) => integer,
                JsonValueKind.Number when json.TryGetDecimal(out var number) => number,
                JsonValueKind.Number => json.GetDouble(),
                _ => throw new SqlCompilationException(
                    $"INSERT literal JSON kind '{json.ValueKind}' is not a scalar SQL value.")
            };
        }

        return value switch
        {
            SqlDateValue date => date.Value.ToDateTime(TimeOnly.MinValue),
            SqlTimeValue time => time.Value.ToTimeSpan(),
            SqlLocalDateTimeValue local => DateTime.SpecifyKind(local.Value, DateTimeKind.Unspecified),
            SqlOffsetDateTimeValue offset => offset.Value,
            DateOnly date => date.ToDateTime(TimeOnly.MinValue),
            TimeOnly time => time.ToTimeSpan(),
            _ => value
        };
    }

    private static int ParameterOrdinal(string name)
    {
        var digits = new string(name.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        return int.TryParse(digits, out var ordinal) ? ordinal : int.MaxValue;
    }

    private sealed record RenderedExpression(
        string Sql,
        ImmutableArray<object?> Bindings);

    private sealed record RenderedRow(
        ImmutableArray<string> Expressions);
}
