using System.Collections.Immutable;
using System.Text.Json;
using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Execution;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
using SqlKata;
using SqlKata.Compilers;

namespace SqlAgent.Service.Core.Lowering;

/// <summary>
/// Structured SqlKata backend for canonical INSERT. VALUES, multi-row VALUES, and INSERT..SELECT
/// all stay in SqlKata's structured query IR; raw user SQL never crosses this boundary.
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

        var compiler = SqlKataProviderLowerer.CreateCompiler(provider);
        var columns = insert.Columns
            .Select(column => CoreIdentifierSqlRenderer.NormalizeSinglePart(
                column,
                compiler,
                "INSERT target column"))
            .ToArray();

        Query query = insert.Source switch
        {
            InsertValuesSource values => LowerValues(insert, columns, values, compiler),
            InsertQuerySource querySource => LowerQuerySource(insert, columns, querySource, compiler),
            _ => throw new SqlCompilationException(
                $"Unsupported INSERT source during lowering: {insert.Source.GetType().Name}")
        };

        var result = compiler.Compile(query);
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

    private static Query LowerValues(
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
                    $"INSERT VALUES supports literal canonical values only, not {value.GetType().Name}.")
            }).ToArray();
        }).ToArray();

        return NewTargetQuery(insert.Target.Name, compiler).AsInsert(columns, rows);
    }

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
}
