using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using HsSqlAgent.SqlCore.Core.Ast;
using HsSqlAgent.SqlCore.Core.Binding;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Execution;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Models;
using SqlKata;
using SqlKata.Compilers;

namespace HsSqlAgent.SqlCore.Core.Lowering;

/// <summary>
/// DML backend for the Core AST. SqlKata owns statement emission; raw predicate fragments are
/// generated only from closed canonical nodes, with every runtime value represented as a binding.
/// Canonical scalar functions use the same provider semantics as query lowering so normalization
/// cannot leak CORE_* pseudo-functions into executable DML SQL.
/// </summary>
public sealed class SqlKataDmlLowerer(SqlAgentToolType provider)
{
    private static readonly Regex SafeCastType = new(
        @"^[A-Za-z_][A-Za-z0-9_.]*(?:\s+(?:PRECISION|VARYING|WITH|WITHOUT|TIME|ZONE|SIGNED|UNSIGNED))*(?:\((?:MAX|[0-9]+(?:,[0-9]+)?)\))?(?:\s+(?:PRECISION|VARYING|WITH|WITHOUT|TIME|ZONE|SIGNED|UNSIGNED))*$",
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
            .Select(pair => new SqlParameterValue(pair.Key, NormalizeLiteral(pair.Value)))
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

        var query = NewTargetQuery(update.Target.Name, compiler);
        ApplyPredicate(query, update.Predicate, compiler);

        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var assignment in update.Assignments)
        {
            values.Add(
                CoreIdentifierSqlRenderer.NormalizeSinglePart(
                    assignment.Column,
                    compiler,
                    "UPDATE assignment column"),
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
        var query = NewTargetQuery(delete.Target.Name, compiler);
        ApplyPredicate(query, delete.Predicate, compiler);
        return query.AsDelete();
    }

    private static Query NewTargetQuery(SqlIdentifier identifier, Compiler compiler) =>
        new Query().FromRaw(CoreIdentifierSqlRenderer.Render(identifier, compiler, allowWildcard: false));

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
        IntervalExpr interval => RenderInterval(interval, compiler),
        UnaryExpr unary => RenderUnary(unary, compiler),
        BinaryExpr binary => RenderBinary(binary, compiler),
        FunctionCallExpr function => RenderFunction(function, compiler),
        CastExpr cast => RenderCast(cast, compiler),
        SimpleCaseExpr simpleCase => RenderSimpleCase(simpleCase, compiler),
        CaseExpr @case => RenderCase(@case, compiler),
        InExpr @in => RenderIn(@in, compiler),
        BetweenExpr between => RenderBetween(between, compiler),
        IsNullExpr isNull => RenderIsNull(isNull, compiler),
        SubqueryExpr subquery => RenderSubquery(subquery.Query, compiler),
        ExistsExpr exists => RenderExists(exists, compiler),
        FilterExpr or WindowedExpr => throw new SqlCompilationException(
            $"Expression '{expression.GetType().Name}' is not supported in Core DML predicates."),
        _ => throw new SqlCompilationException(
            $"Unsupported expression during Core DML lowering: {expression.GetType().Name}")
    };

    private static RenderedExpression RenderInterval(IntervalExpr interval, Compiler compiler)
    {
        if (compiler is not PostgresCompiler)
            throw new SqlCompilationException("INTERVAL expressions are supported only by PostgreSQL in the Core DML backend.");
        var literal = interval.Literal.Replace("'", "''", StringComparison.Ordinal);
        return new RenderedExpression($"INTERVAL '{literal}'", ImmutableArray<object?>.Empty);
    }

    private static RenderedExpression RenderUnary(UnaryExpr unary, Compiler compiler)
    {
        if (unary.Operator != "NOT")
            throw new SqlCompilationException($"Unsupported unary operator '{unary.Operator}'.");
        var operand = RenderExpression(unary.Operand, compiler);
        return operand with { Sql = $"NOT ({operand.Sql})" };
    }

    private static RenderedExpression RenderBinary(BinaryExpr binary, Compiler compiler)
    {
        var left = RenderExpression(binary.Left, compiler);
        var right = RenderExpression(binary.Right, compiler);
        var likeEscape = CoreLikeEscapeSqlRenderer.RenderSuffix(binary, compiler);

        if (binary.Operator == "%" && compiler is OracleCompiler or FirebirdCompiler)
            return Combine($"MOD({left.Sql}, {right.Sql})", left, right);
        if (binary.Operator == "||" && compiler is MySqlCompiler)
            return Combine($"CONCAT({left.Sql}, {right.Sql})", left, right);
        if (binary.Operator == "||" && compiler is SqlServerCompiler)
            return Combine($"({left.Sql} + {right.Sql})", left, right);

        var op = binary.Operator switch
        {
            "+" or "-" or "*" or "/" or "%" or "||" or
            "=" or "<>" or "!=" or ">" or "<" or ">=" or "<=" or
            "LIKE" or "ILIKE" or "AND" or "OR" or "IN" or "NOT IN" => binary.Operator,
            _ => throw new SqlCompilationException($"Unsupported DML predicate operator '{binary.Operator}'.")
        };
        return Combine($"({left.Sql} {op} {right.Sql}{likeEscape})", left, right);
    }

    private static RenderedExpression RenderFunction(FunctionCallExpr function, Compiler compiler)
    {
        var name = IdentifierText(function.Name).ToUpperInvariant();
        return name switch
        {
            "CORE_DATE_ADD" => RenderDateAdd(function, compiler),
            "CORE_DATE_DIFF" => RenderDateDiff(function, compiler),
            "CORE_DATE_PART" => RenderDatePart(function, compiler),
            "CORE_DATE_FORMAT" => RenderDateFormat(function, compiler),
            "CORE_DATE_PARSE" => RenderDateParse(function, compiler),
            "CORE_POSITION" => RenderPosition(function, compiler),
            "CORE_JSON_EXTRACT" => RenderJsonExtract(function, compiler),
            "CORE_JSON_SET" => RenderJsonSet(function, compiler),
            "CORE_REGEX_MATCH" => RenderRegexMatch(function, compiler),
            "CORE_CURRENT_DATE" => RenderCurrentDate(function, compiler),
            "CORE_CURRENT_TIME" => RenderCurrentTime(function, compiler),
            "CORE_CURRENT_TIMESTAMP" => RenderCurrentTimestamp(function),
            "CORE_STRING_AGG" => throw new SqlCompilationException(
                "Aggregate function CORE_STRING_AGG is not valid in a DML predicate."),
            _ => RenderOrdinaryFunction(function, compiler)
        };
    }

    private static RenderedExpression RenderOrdinaryFunction(FunctionCallExpr function, Compiler compiler)
    {
        var name = IdentifierText(function.Name);
        if (!Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant))
            throw new SqlCompilationException($"Unsafe function identifier '{name}'.");

        if (name.StartsWith("CORE_", StringComparison.OrdinalIgnoreCase))
        {
            throw new SqlCompilationException(
                $"Canonical function '{name}' has no DML lowering implementation; compilation was rejected.");
        }

        var args = function.Arguments.Select(argument => RenderExpression(argument, compiler)).ToArray();
        var renderedArgs = args.Select(argument => argument.Sql).ToArray();
        if (compiler is PostgresCompiler
            && name.Equals("ROUND", StringComparison.OrdinalIgnoreCase)
            && args.Length == 2)
        {
            renderedArgs[0] = $"CAST({renderedArgs[0]} AS numeric)";
        }

        var sql = string.Join(", ", renderedArgs);
        if (function.IsDistinct) sql = "DISTINCT " + sql;
        return new RenderedExpression(
            $"{name}({sql})",
            args.SelectMany(argument => argument.Bindings).ToImmutableArray());
    }

    private static RenderedExpression RenderDateAdd(FunctionCallExpr function, Compiler compiler)
    {
        RequireArguments(function, 3);
        var unit = LiteralKeyword(function.Arguments[0], "DATEADD unit");
        if (unit != "DAY" && compiler is PostgresCompiler or OracleCompiler or SqliteCompiler)
            throw new SqlCompilationException($"DATEADD unit {unit} is not supported by {compiler.GetType().Name}.");
        var amount = RenderExpression(function.Arguments[1], compiler);
        var value = RenderExpression(function.Arguments[2], compiler);
        return compiler switch
        {
            SqlServerCompiler => Combine($"DATEADD({unit}, {amount.Sql}, {value.Sql})", amount, value),
            MySqlCompiler => Combine($"TIMESTAMPADD({unit}, {amount.Sql}, {value.Sql})", amount, value),
            PostgresCompiler => Combine($"({value.Sql} + ({amount.Sql} * INTERVAL '1 day'))", value, amount),
            OracleCompiler => Combine($"({value.Sql} + {amount.Sql})", value, amount),
            SqliteCompiler => Combine($"DATETIME({value.Sql}, PRINTF('%+d day', {amount.Sql}))", value, amount),
            FirebirdCompiler => Combine($"DATEADD({unit}, {amount.Sql}, {value.Sql})", amount, value),
            _ => throw new SqlCompilationException("Unsupported DATEADD provider.")
        };
    }

    private static RenderedExpression RenderDateDiff(FunctionCallExpr function, Compiler compiler)
    {
        RequireArguments(function, 3);
        var unit = LiteralKeyword(function.Arguments[0], "DATEDIFF unit");
        if (unit != "DAY" && compiler is PostgresCompiler or OracleCompiler or SqliteCompiler)
            throw new SqlCompilationException($"DATEDIFF unit {unit} is not supported by {compiler.GetType().Name}.");
        var start = RenderExpression(function.Arguments[1], compiler);
        var end = RenderExpression(function.Arguments[2], compiler);
        return compiler switch
        {
            SqlServerCompiler => Combine($"DATEDIFF({unit}, {start.Sql}, {end.Sql})", start, end),
            MySqlCompiler => Combine($"TIMESTAMPDIFF({unit}, {start.Sql}, {end.Sql})", start, end),
            PostgresCompiler => Combine($"(CAST({end.Sql} AS date) - CAST({start.Sql} AS date))", end, start),
            OracleCompiler => Combine($"(CAST({end.Sql} AS DATE) - CAST({start.Sql} AS DATE))", end, start),
            SqliteCompiler => Combine($"(JULIANDAY({end.Sql}) - JULIANDAY({start.Sql}))", end, start),
            FirebirdCompiler => Combine($"DATEDIFF({unit} FROM {start.Sql} TO {end.Sql})", start, end),
            _ => throw new SqlCompilationException("Unsupported DATEDIFF provider.")
        };
    }

    private static RenderedExpression RenderDatePart(FunctionCallExpr function, Compiler compiler)
    {
        RequireArguments(function, 2);
        var part = LiteralKeyword(function.Arguments[0], "date part");
        var value = RenderExpression(function.Arguments[1], compiler);
        var sql = compiler switch
        {
            SqlServerCompiler or MySqlCompiler => $"{part}({value.Sql})",
            PostgresCompiler or OracleCompiler => $"EXTRACT({part} FROM {value.Sql})",
            FirebirdCompiler => $"EXTRACT({part} FROM CAST({value.Sql} AS DATE))",
            SqliteCompiler => part switch
            {
                "YEAR" => $"CAST(STRFTIME('%Y', {value.Sql}) AS INTEGER)",
                "MONTH" => $"CAST(STRFTIME('%m', {value.Sql}) AS INTEGER)",
                "DAY" => $"CAST(STRFTIME('%d', {value.Sql}) AS INTEGER)",
                _ => throw new SqlCompilationException($"SQLite does not support date part {part}.")
            },
            _ => throw new SqlCompilationException("Unsupported date-part provider.")
        };
        return value with { Sql = sql };
    }

    private static RenderedExpression RenderDateFormat(FunctionCallExpr function, Compiler compiler)
    {
        RequireArguments(function, 2);
        var value = RenderExpression(function.Arguments[0], compiler);
        var format = SqlStringLiteral(function.Arguments[1], "date format");
        var sql = compiler switch
        {
            SqlServerCompiler => $"FORMAT({value.Sql}, {format})",
            PostgresCompiler or OracleCompiler => $"TO_CHAR({value.Sql}, {format})",
            MySqlCompiler => $"DATE_FORMAT({value.Sql}, {format})",
            SqliteCompiler => $"STRFTIME({format}, {value.Sql})",
            FirebirdCompiler => throw new SqlCompilationException("portable date formatting is not supported by Firebird."),
            _ => throw new SqlCompilationException("Unsupported date-format provider.")
        };
        return value with { Sql = sql };
    }

    private static RenderedExpression RenderDateParse(FunctionCallExpr function, Compiler compiler)
    {
        RequireArguments(function, 2);
        var value = RenderExpression(function.Arguments[0], compiler);
        var format = SqlStringLiteral(function.Arguments[1], "date parse format");
        var sql = compiler switch
        {
            MySqlCompiler => $"DATE(STR_TO_DATE({value.Sql}, {format}))",
            PostgresCompiler or OracleCompiler => $"TO_DATE({value.Sql}, {format})",
            _ => throw new SqlCompilationException("formatted date parsing is not supported by this provider.")
        };
        return value with { Sql = sql };
    }

    private static RenderedExpression RenderPosition(FunctionCallExpr function, Compiler compiler)
    {
        RequireArguments(function, 2);
        var haystack = RenderExpression(function.Arguments[0], compiler);
        var needle = RenderExpression(function.Arguments[1], compiler);
        return compiler switch
        {
            SqlServerCompiler => Combine($"CHARINDEX({needle.Sql}, {haystack.Sql})", needle, haystack),
            PostgresCompiler => Combine($"STRPOS({haystack.Sql}, {needle.Sql})", haystack, needle),
            MySqlCompiler => Combine($"LOCATE({needle.Sql}, {haystack.Sql})", needle, haystack),
            SqliteCompiler or OracleCompiler => Combine($"INSTR({haystack.Sql}, {needle.Sql})", haystack, needle),
            FirebirdCompiler => Combine($"POSITION({needle.Sql}, {haystack.Sql})", needle, haystack),
            _ => throw new SqlCompilationException("Unsupported position provider.")
        };
    }

    private static RenderedExpression RenderJsonExtract(FunctionCallExpr function, Compiler compiler)
    {
        RequireArguments(function, 2);
        var value = RenderExpression(function.Arguments[0], compiler);
        var path = RenderExpression(function.Arguments[1], compiler);
        if (compiler is MySqlCompiler or SqliteCompiler)
            return Combine($"JSON_EXTRACT({value.Sql}, {path.Sql})", value, path);
        if (compiler is not PostgresCompiler)
            throw new SqlCompilationException("JSON_EXTRACT is not supported losslessly by this provider.");

        var segments = JsonPathSegments(function.Arguments[1]);
        var bindings = value.Bindings.ToBuilder();
        var placeholders = new List<string>();
        foreach (var segment in segments)
        {
            placeholders.Add("?");
            bindings.Add(segment);
        }
        return new RenderedExpression(
            $"JSONB_EXTRACT_PATH(CAST({value.Sql} AS jsonb), {string.Join(", ", placeholders)})",
            bindings.ToImmutable());
    }

    private static RenderedExpression RenderJsonSet(FunctionCallExpr function, Compiler compiler)
    {
        RequireArguments(function, 3);
        var value = RenderExpression(function.Arguments[0], compiler);
        var path = RenderExpression(function.Arguments[1], compiler);
        var newValue = RenderExpression(function.Arguments[2], compiler);
        if (compiler is MySqlCompiler or SqliteCompiler)
        {
            return new RenderedExpression(
                $"JSON_SET({value.Sql}, {path.Sql}, {newValue.Sql})",
                value.Bindings.Concat(path.Bindings).Concat(newValue.Bindings).ToImmutableArray());
        }
        if (compiler is SqlServerCompiler)
        {
            return new RenderedExpression(
                $"JSON_MODIFY({value.Sql}, {path.Sql}, {newValue.Sql})",
                value.Bindings.Concat(path.Bindings).Concat(newValue.Bindings).ToImmutableArray());
        }
        if (compiler is not PostgresCompiler)
            throw new SqlCompilationException("JSON_SET is not supported by this provider.");

        var pgPath = "{" + string.Join(',', JsonPathSegments(function.Arguments[1])) + "}";
        return new RenderedExpression(
            $"JSONB_SET(CAST({value.Sql} AS jsonb), CAST(? AS text[]), TO_JSONB({newValue.Sql}))",
            value.Bindings.Concat([pgPath]).Concat(newValue.Bindings).ToImmutableArray());
    }

    private static RenderedExpression RenderRegexMatch(FunctionCallExpr function, Compiler compiler)
    {
        RequireArguments(function, 2);
        if (compiler is SqlServerCompiler or SqliteCompiler or FirebirdCompiler)
            throw new SqlCompilationException("REGEXP_LIKE is not supported by this provider.");
        var value = RenderExpression(function.Arguments[0], compiler);
        var pattern = RenderExpression(function.Arguments[1], compiler);
        return compiler switch
        {
            PostgresCompiler => Combine($"({value.Sql} ~ {pattern.Sql})", value, pattern),
            _ => Combine($"REGEXP_LIKE({value.Sql}, {pattern.Sql})", value, pattern)
        };
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
        {
            throw new SqlCompilationException(
                $"Canonical current temporal function '{IdentifierText(function.Name)}' must have zero arguments and cannot be DISTINCT.");
        }
    }

    private static void RequireArguments(FunctionCallExpr function, int count)
    {
        if (function.Arguments.Length != count)
        {
            throw new SqlCompilationException(
                $"Canonical function '{IdentifierText(function.Name)}' requires {count} argument(s).");
        }
    }

    private static string LiteralKeyword(SqlExpr expression, string label)
    {
        if (expression is not LiteralExpr { Value: string value })
            throw new SqlCompilationException($"{label} must be a canonical literal keyword.");
        var normalized = value.Trim().ToUpperInvariant();
        if (!Regex.IsMatch(normalized, "^[A-Z_]+$", RegexOptions.CultureInvariant))
            throw new SqlCompilationException($"Unsafe {label} '{value}'.");
        return normalized;
    }

    private static string SqlStringLiteral(SqlExpr expression, string label)
    {
        if (expression is not LiteralExpr { Value: string value })
            throw new SqlCompilationException($"{label} must be a string literal.");
        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    private static IReadOnlyList<string> JsonPathSegments(SqlExpr expression)
    {
        if (expression is not LiteralExpr { Value: string path })
            throw new SqlCompilationException("JSON path must be a string literal for structured PostgreSQL lowering.");
        var trimmed = path.Trim();
        if (!trimmed.StartsWith('$'))
            throw new SqlCompilationException($"Unsupported JSON path '{path}'.");
        var remainder = trimmed[1..].TrimStart('.');
        if (string.IsNullOrEmpty(remainder)) return [];
        var segments = remainder.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Any(segment => !Regex.IsMatch(segment, "^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)))
            throw new SqlCompilationException($"Unsupported structured JSON path '{path}'.");
        return segments;
    }

    private static RenderedExpression RenderCast(CastExpr cast, Compiler compiler)
    {
        if (!SafeCastType.IsMatch(cast.TypeName))
            throw new SqlCompilationException($"Unsafe CAST type '{cast.TypeName}'.");
        var value = RenderExpression(cast.Expression, compiler);
        return value with { Sql = $"CAST({value.Sql} AS {cast.TypeName})" };
    }

    private static RenderedExpression RenderSimpleCase(SimpleCaseExpr @case, Compiler compiler)
    {
        if (@case.Branches.IsDefaultOrEmpty)
            throw new SqlCompilationException("Simple CASE requires at least one WHEN branch.");

        var first = RequireSimpleCaseComparison(@case.Branches[0]);
        var operand = RenderExpression(first.Left, compiler);
        var bindings = ImmutableArray.CreateBuilder<object?>();
        bindings.AddRange(operand.Bindings);
        var clauses = new List<string>();

        foreach (var branch in @case.Branches)
        {
            var comparison = RequireSimpleCaseComparison(branch);
            var match = RenderExpression(comparison.Right, compiler);
            var value = RenderExpression(branch.Value, compiler);
            clauses.Add($"WHEN {match.Sql} THEN {value.Sql}");
            bindings.AddRange(match.Bindings);
            bindings.AddRange(value.Bindings);
        }

        if (@case.ElseExpression is not null)
        {
            var otherwise = RenderExpression(@case.ElseExpression, compiler);
            clauses.Add($"ELSE {otherwise.Sql}");
            bindings.AddRange(otherwise.Bindings);
        }

        return new RenderedExpression(
            $"CASE {operand.Sql} {string.Join(" ", clauses)} END",
            bindings.ToImmutable());
    }

    private static BinaryExpr RequireSimpleCaseComparison(CaseBranch branch) =>
        branch.Condition is BinaryExpr { Operator: "=" } comparison
            ? comparison
            : throw new SqlCompilationException(
                "Simple CASE branch lost its canonical equality shape before DML lowering.");

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
        if (@in.Items.IsDefaultOrEmpty)
            throw new SqlCompilationException("IN requires at least one item.");
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

    private static RenderedExpression RenderSubquery(SqlStatement statement, Compiler compiler)
    {
        var result = compiler.Compile(SqlKataProviderLowerer.BuildQuery(statement, compiler));
        return new RenderedExpression(
            $"({ToPositionalSql(result.Sql, result.NamedBindings)})",
            OrderedValues(result.NamedBindings));
    }

    private static RenderedExpression RenderExists(ExistsExpr exists, Compiler compiler)
    {
        var subquery = RenderSubquery(exists.Query, compiler);
        return subquery with
        {
            Sql = $"{(exists.IsNegated ? "NOT " : string.Empty)}EXISTS {subquery.Sql}"
        };
    }

    private static string ToPositionalSql(string sql, IReadOnlyDictionary<string, object> bindings)
    {
        foreach (var pair in bindings.OrderByDescending(pair => ParameterOrdinal(pair.Key)))
            sql = sql.Replace(pair.Key, "?", StringComparison.Ordinal);
        return sql;
    }

    private static ImmutableArray<object?> OrderedValues(IReadOnlyDictionary<string, object> bindings) =>
        bindings.OrderBy(pair => ParameterOrdinal(pair.Key))
            .Select(pair => NormalizeLiteral(pair.Value))
            .ToImmutableArray();

    private static RenderedExpression RenderIdentifier(SqlIdentifier identifier, Compiler compiler) =>
        new(
            CoreIdentifierSqlRenderer.Render(identifier, compiler, allowWildcard: true),
            ImmutableArray<object?>.Empty);

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

    private static Compiler CreateCompiler(SqlAgentToolType provider) =>
        SqlKataProviderLowerer.CreateCompiler(provider);

    private static int ParameterOrdinal(string name)
    {
        var digits = new string(name.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        return int.TryParse(digits, out var value) ? value : int.MaxValue;
    }

    private static string IdentifierText(SqlIdentifier identifier) =>
        string.Join('.', identifier.Parts.Select(part => part.Value));

    private sealed record RenderedExpression(string Sql, ImmutableArray<object?> Bindings);
}
