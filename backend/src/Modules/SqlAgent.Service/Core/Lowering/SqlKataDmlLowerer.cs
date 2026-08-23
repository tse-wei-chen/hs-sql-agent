using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Core.Binding;
using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Execution;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
using SqlKata;
using SqlKata.Compilers;

namespace SqlAgent.Service.Core.Lowering;

/// <summary>
/// DML backend for the Core AST. SqlKata owns statement emission; raw predicate fragments are
/// generated only from closed canonical nodes, with every literal represented as a binding.
/// </summary>
public sealed class SqlKataDmlLowerer(SqlAgentToolType provider)
{
    private static readonly Regex SafeCastType = new(
        @"^[A-Za-z_][A-Za-z0-9_.]*(?:\s+(?:PRECISION|VARYING|WITH|WITHOUT|TIME|ZONE|SIGNED|UNSIGNED))*(?:\([0-9]+(?:,[0-9]+)?\))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public CompiledSqlCommand Lower(ExecutableSqlPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.TargetProvider != provider)
            throw new SqlCompilationException($"Plan targets {plan.TargetProvider}, but this DML lowerer targets {provider}.");

        var compiler = CreateCompiler(provider);
        var query = plan.Statement switch
        {
            UpdateStatement update => LowerUpdate(update, compiler),
            DeleteStatement delete => LowerDelete(delete, compiler),
            _ => throw new SqlCompilationException(
                $"SqlKata DML lowering requires UPDATE or DELETE, not {plan.Statement.GetType().Name}.")
        };
        var kind = plan.Statement is UpdateStatement ? SqlStatementKind.Update : SqlStatementKind.Delete;
        var result = compiler.Compile(query);
        var parameters = result.NamedBindings
            .OrderBy(pair => ParameterOrdinal(pair.Key))
            .Select(pair => new SqlParameterValue(pair.Key, pair.Value))
            .ToImmutableArray();
        var command = new CompiledSqlCommand(result.Sql, parameters, kind, string.Empty, provider);
        return command with
        {
            PlanFingerprint = DmlFingerprintService.ComputePlanFingerprint(command, plan.PolicyVersion)
        };
    }

    private static Query LowerUpdate(UpdateStatement update, Compiler compiler)
    {
        if (update.Assignments.IsDefaultOrEmpty)
            throw new SqlCompilationException("UPDATE requires at least one assignment.");

        var query = new Query(IdentifierText(update.Target.Name));
        ApplyPredicate(query, update.Predicate, compiler);

        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var assignment in update.Assignments)
        {
            if (assignment.Column.Parts.Length != 1)
                throw new SqlCompilationException("UPDATE assignment columns must be unqualified canonical identifiers.");
            values.Add(
                assignment.Column.Parts[0].Value,
                LowerAssignmentValue(assignment.Value, compiler, IdentifierText(assignment.Column)));
        }
        return query.AsUpdate(values);
    }

    private static object? LowerAssignmentValue(SqlExpr expression, Compiler compiler, string column)
    {
        if (expression is LiteralExpr literal)
            return NormalizeLiteral(literal.Value);

        if (expression is FunctionCallExpr function && IsCanonicalCurrentTemporal(function))
        {
            var rendered = RenderFunction(function, compiler);
            if (!rendered.Bindings.IsDefaultOrEmpty)
                throw new SqlCompilationException(
                    $"UPDATE assignment '{column}' produced bindings for a current-temporal expression; compilation was rejected.");
            return SqlKata.Expressions.UnsafeLiteral(rendered.Sql, replaceQuotes: false);
        }

        throw new SqlCompilationException(
            $"UPDATE assignment '{column}' is not an approved value expression. " +
            "Only canonical literals and current temporal expressions are supported.");
    }

    private static bool IsCanonicalCurrentTemporal(FunctionCallExpr function)
    {
        if (function.IsDistinct || !function.Arguments.IsDefaultOrEmpty || function.Name.Parts.Length != 1)
            return false;
        return function.Name.Parts[0].Value.ToUpperInvariant() is
            "CORE_CURRENT_DATE" or "CORE_CURRENT_TIME" or "CORE_CURRENT_TIMESTAMP";
    }

    private static Query LowerDelete(DeleteStatement delete, Compiler compiler)
    {
        var query = new Query(IdentifierText(delete.Target.Name));
        ApplyPredicate(query, delete.Predicate, compiler);
        return query.AsDelete();
    }

    private static void ApplyPredicate(Query query, SqlExpr? predicate, Compiler compiler)
    {
        if (predicate is null) return;
        var rendered = RenderExpression(predicate, compiler);
        query.WhereRaw(rendered.Sql, rendered.Bindings.ToArray());
    }

    private static RenderedExpression RenderExpression(SqlExpr expression, Compiler compiler) => expression switch
    {
        BoundColumnExpr column => RenderIdentifier(column.Name, compiler),
        ColumnExpr column => RenderIdentifier(column.Name, compiler),
        LiteralExpr literal => new RenderedExpression("?", [NormalizeLiteral(literal.Value)]),
        UnaryExpr unary => RenderUnary(unary, compiler),
        BinaryExpr binary => RenderBinary(binary, compiler),
        FunctionCallExpr function => RenderFunction(function, compiler),
        CastExpr cast => RenderCast(cast, compiler),
        CaseExpr @case => RenderCase(@case, compiler),
        InExpr @in => RenderIn(@in, compiler),
        BetweenExpr between => RenderBetween(between, compiler),
        IsNullExpr isNull => RenderIsNull(isNull, compiler),
        SubqueryExpr or ExistsExpr => throw new SqlCompilationException(
            "Subquery predicates in Core DML are not yet supported by the DML backend; compilation was rejected."),
        IntervalExpr or FilterExpr or WindowedExpr => throw new SqlCompilationException(
            $"Expression '{expression.GetType().Name}' is not supported in Core DML predicates."),
        _ => throw new SqlCompilationException(
            $"Unsupported expression during Core DML lowering: {expression.GetType().Name}")
    };

    private static RenderedExpression RenderUnary(UnaryExpr unary, Compiler compiler)
    {
        if (unary.Operator != "NOT") throw new SqlCompilationException($"Unsupported unary operator '{unary.Operator}'.");
        var operand = RenderExpression(unary.Operand, compiler);
        return operand with { Sql = $"NOT ({operand.Sql})" };
    }

    private static RenderedExpression RenderBinary(BinaryExpr binary, Compiler compiler)
    {
        var left = RenderExpression(binary.Left, compiler);
        var right = RenderExpression(binary.Right, compiler);
        var op = binary.Operator switch
        {
            "=" or "<>" or "!=" or ">" or "<" or ">=" or "<=" or "LIKE" or "ILIKE" or "AND" or "OR" => binary.Operator,
            _ => throw new SqlCompilationException($"Unsupported DML predicate operator '{binary.Operator}'.")
        };
        return Combine($"({left.Sql} {op} {right.Sql})", left, right);
    }

    private static RenderedExpression RenderFunction(FunctionCallExpr function, Compiler compiler)
    {
        var name = IdentifierText(function.Name).ToUpperInvariant();
        return name switch
        {
            "CORE_CURRENT_DATE" => RenderCurrentDate(function, compiler),
            "CORE_CURRENT_TIME" => RenderCurrentTime(function, compiler),
            "CORE_CURRENT_TIMESTAMP" => RenderCurrentTimestamp(function),
            _ => RenderOrdinaryFunction(function, compiler)
        };
    }

    private static RenderedExpression RenderOrdinaryFunction(FunctionCallExpr function, Compiler compiler)
    {
        var name = IdentifierText(function.Name);
        if (!Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant))
            throw new SqlCompilationException($"Unsafe function identifier '{name}'.");
        var args = function.Arguments.Select(argument => RenderExpression(argument, compiler)).ToArray();
        var sql = string.Join(", ", args.Select(argument => argument.Sql));
        if (function.IsDistinct) sql = "DISTINCT " + sql;
        return new RenderedExpression(
            $"{name}({sql})",
            args.SelectMany(argument => argument.Bindings).ToImmutableArray());
    }

    private static RenderedExpression RenderCurrentDate(FunctionCallExpr function, Compiler compiler)
    {
        RequireCurrentTemporalShape(function);
        return new RenderedExpression(
            compiler is SqlServerCompiler ? "CAST(CURRENT_TIMESTAMP AS date)" : "CURRENT_DATE",
            ImmutableArray<object?>.Empty);
    }

    private static RenderedExpression RenderCurrentTime(FunctionCallExpr function, Compiler compiler)
    {
        RequireCurrentTemporalShape(function);
        if (compiler is OracleCompiler)
            throw new SqlCompilationException("CURRENT_TIME is not supported by Oracle.");
        return new RenderedExpression(
            compiler is SqlServerCompiler ? "CAST(CURRENT_TIMESTAMP AS time)" : "CURRENT_TIME",
            ImmutableArray<object?>.Empty);
    }

    private static RenderedExpression RenderCurrentTimestamp(FunctionCallExpr function)
    {
        RequireCurrentTemporalShape(function);
        return new RenderedExpression("CURRENT_TIMESTAMP", ImmutableArray<object?>.Empty);
    }

    private static void RequireCurrentTemporalShape(FunctionCallExpr function)
    {
        if (function.IsDistinct || !function.Arguments.IsDefaultOrEmpty)
            throw new SqlCompilationException(
                $"Canonical current temporal function '{IdentifierText(function.Name)}' must have zero arguments and cannot be DISTINCT.");
    }

    private static RenderedExpression RenderCast(CastExpr cast, Compiler compiler)
    {
        if (!SafeCastType.IsMatch(cast.TypeName)) throw new SqlCompilationException($"Unsafe CAST type '{cast.TypeName}'.");
        var value = RenderExpression(cast.Expression, compiler);
        return value with { Sql = $"CAST({value.Sql} AS {cast.TypeName})" };
    }

    private static RenderedExpression RenderCase(CaseExpr @case, Compiler compiler)
    {
        var bindings = ImmutableArray.CreateBuilder<object?>();
        var clauses = new List<string>();
        foreach (var branch in @case.Branches)
        {
            var condition = RenderExpression(branch.Condition, compiler);
            var value = RenderExpression(branch.Value, compiler);
            clauses.Add($"WHEN {condition.Sql} THEN {value.Sql}");
            bindings.AddRange(condition.Bindings);
            bindings.AddRange(value.Bindings);
        }
        if (@case.ElseExpression is not null)
        {
            var otherwise = RenderExpression(@case.ElseExpression, compiler);
            clauses.Add($"ELSE {otherwise.Sql}");
            bindings.AddRange(otherwise.Bindings);
        }
        return new RenderedExpression($"CASE {string.Join(" ", clauses)} END", bindings.ToImmutable());
    }

    private static RenderedExpression RenderIn(InExpr @in, Compiler compiler)
    {
        if (@in.Items.IsDefaultOrEmpty) throw new SqlCompilationException("IN requires at least one item.");
        var value = RenderExpression(@in.Value, compiler);
        var items = @in.Items.Select(item => RenderExpression(item, compiler)).ToArray();
        var op = @in.IsNegated ? "NOT IN" : "IN";
        return new RenderedExpression(
            $"({value.Sql} {op} ({string.Join(", ", items.Select(item => item.Sql))}))",
            value.Bindings.Concat(items.SelectMany(item => item.Bindings)).ToImmutableArray());
    }

    private static RenderedExpression RenderBetween(BetweenExpr between, Compiler compiler)
    {
        var value = RenderExpression(between.Value, compiler);
        var lower = RenderExpression(between.Lower, compiler);
        var upper = RenderExpression(between.Upper, compiler);
        var op = between.IsNegated ? "NOT BETWEEN" : "BETWEEN";
        return new RenderedExpression(
            $"({value.Sql} {op} {lower.Sql} AND {upper.Sql})",
            value.Bindings.Concat(lower.Bindings).Concat(upper.Bindings).ToImmutableArray());
    }

    private static RenderedExpression RenderIsNull(IsNullExpr isNull, Compiler compiler)
    {
        var value = RenderExpression(isNull.Value, compiler);
        return value with { Sql = $"({value.Sql} IS {(isNull.IsNegated ? "NOT " : string.Empty)}NULL)" };
    }

    private static RenderedExpression RenderIdentifier(SqlIdentifier identifier, Compiler compiler)
    {
        if (identifier.Parts.IsDefaultOrEmpty) throw new SqlCompilationException("SQL identifier has no parts.");
        foreach (var part in identifier.Parts)
        {
            if (!Regex.IsMatch(part.Value, @"^[A-Za-z_][A-Za-z0-9_$]*$", RegexOptions.CultureInvariant))
                throw new SqlCompilationException($"Unsafe SQL identifier part '{part.Value}'.");
        }
        return new RenderedExpression(compiler.Wrap(IdentifierText(identifier)), ImmutableArray<object?>.Empty);
    }

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
                    $"DML literal JSON kind '{json.ValueKind}' is not a scalar SQL value.")
            };
        }

        return value switch
        {
            SqlDateValue date => date.Value.ToDateTime(TimeOnly.MinValue),
            SqlTimeValue time => time.Value.ToTimeSpan(),
            SqlLocalDateTimeValue local => DateTime.SpecifyKind(local.Value, DateTimeKind.Unspecified),
            SqlOffsetDateTimeValue offset => offset.Value,
            _ => value
        };
    }

    private static RenderedExpression Combine(string sql, RenderedExpression left, RenderedExpression right) =>
        new(sql, left.Bindings.Concat(right.Bindings).ToImmutableArray());

    private static Compiler CreateCompiler(SqlAgentToolType provider) => provider switch
    {
        SqlAgentToolType.Sqlite => new SqliteCompiler(),
        SqlAgentToolType.Postgres => new PostgresCompiler(),
        SqlAgentToolType.MySQL => new MySqlCompiler(),
        SqlAgentToolType.MsSqlServer => new SqlServerCompiler(),
        SqlAgentToolType.Oracle => new OracleCompiler(),
        SqlAgentToolType.Firebird => new FirebirdCompiler(),
        _ => throw new SqlCompilationException($"Unsupported target provider '{provider}'.")
    };

    private static int ParameterOrdinal(string name)
    {
        var digits = new string(name.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        return int.TryParse(digits, out var value) ? value : int.MaxValue;
    }

    private static string IdentifierText(SqlIdentifier identifier) =>
        string.Join('.', identifier.Parts.Select(part => part.Value));

    private sealed record RenderedExpression(string Sql, ImmutableArray<object?> Bindings);
}
