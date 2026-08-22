using System.Collections.Immutable;
using System.Text.Json;
using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Execution;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;
using SqlKata;
using SqlKata.Compilers;

namespace SqlAgent.Service.Core.Lowering;

/// <summary>
/// Structured SqlKata backend for canonical INSERT. Literal VALUES and multi-row VALUES are
/// supported without raw SQL. INSERT..SELECT remains fail-closed until the query-to-SqlKata
/// builder is shared with <see cref="SqlKataProviderLowerer"/>.
/// </summary>
public sealed class SqlKataInsertLowerer(SqlAgentToolType provider)
{
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
        var columns = insert.Columns.Select(IdentifierText).ToArray();
        if (columns.Any(column => column.Contains('.', StringComparison.Ordinal)))
            throw new SqlCompilationException("INSERT target columns must be unqualified.");

        var query = insert.Source switch
        {
            InsertValuesSource values => LowerValues(insert, columns, values),
            InsertQuerySource => throw new SqlCompilationException(
                "INSERT..SELECT is represented in the Core AST but its shared SqlKata query builder is not wired into the INSERT backend yet; compilation was rejected."),
            _ => throw new SqlCompilationException(
                $"Unsupported INSERT source during lowering: {insert.Source.GetType().Name}")
        };

        var compiler = CreateCompiler(provider);
        var result = compiler.Compile(query);
        var parameters = result.NamedBindings
            .OrderBy(pair => ParameterOrdinal(pair.Key))
            .Select(pair => new SqlParameterValue(pair.Key, pair.Value))
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

    private static Query LowerValues(
        InsertStatement insert,
        string[] columns,
        InsertValuesSource values)
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
                    $"INSERT VALUES supports literal canonical values only, not {value.GetType().Name}.")
            }).ToArray();
        }).ToArray();

        return new Query(IdentifierText(insert.Target.Name)).AsInsert(columns, rows);
    }

    private static object? NormalizeLiteral(object? value) => value switch
    {
        JsonElement element => element.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number when element.TryGetDecimal(out var number) => number,
            _ => throw new SqlCompilationException(
                $"Unsupported JSON literal kind '{element.ValueKind}' in INSERT.")
        },
        SqlDateValue date => date.Value,
        SqlTimeValue time => time.Value,
        SqlDateTimeValue timestamp => timestamp.Value,
        SqlOffsetDateTimeValue offset => offset.Value,
        _ => value
    };

    private static string IdentifierText(SqlIdentifier identifier) =>
        string.Join('.', identifier.Parts.Select(part => part.Value));

    private static int ParameterOrdinal(string name)
    {
        var digits = new string(name.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var ordinal) ? ordinal : int.MaxValue;
    }

    private static Compiler CreateCompiler(SqlAgentToolType type) => type switch
    {
        SqlAgentToolType.MsSqlServer => new SqlServerCompiler(),
        SqlAgentToolType.MySQL => new MySqlCompiler(),
        SqlAgentToolType.Postgres => new PostgresCompiler(),
        SqlAgentToolType.Oracle => new OracleCompiler(),
        SqlAgentToolType.Firebird => new FirebirdCompiler(),
        SqlAgentToolType.Sqlite => new SqliteCompiler(),
        _ => throw new SqlCompilationException($"Unsupported provider: {type}")
    };
}
