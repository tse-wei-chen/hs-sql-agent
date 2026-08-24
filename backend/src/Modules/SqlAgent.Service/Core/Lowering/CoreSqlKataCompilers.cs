using System.Globalization;
using SqlAgent.Service.Core.Ast;
using SqlKata;
using SqlKata.Compilers;

namespace SqlAgent.Service.Core.Lowering;

/// <summary>
/// Core-owned SqlKata compilers preserve the semantic distinction between an absent LIMIT clause
/// and an explicit LIMIT 0. The upstream Query object retains a zero-valued LimitClause, but its
/// stock compiler helpers interpret numeric zero as "not set". These provider adapters look at
/// clause presence instead, without changing legacy SqlKata behavior outside the Core pipeline.
/// </summary>
internal static class CoreSqlKataPagination
{
    public static LimitClause? Limit(SqlResult ctx, string engineCode) =>
        ctx.Query.GetOneComponent<LimitClause>("limit", engineCode);

    public static OffsetClause? Offset(SqlResult ctx, string engineCode) =>
        ctx.Query.GetOneComponent<OffsetClause>("offset", engineCode);

    /// <summary>
    /// SqlKata's compiler contract historically uses null as the sentinel for an absent clause,
    /// even though the override return type is annotated as non-nullable. Keep that upstream
    /// behavior isolated here instead of spreading nullable suppressions through each compiler.
    /// </summary>
    public static string AbsentClause() => null!;

    public static string CompileAnsiLimitOffset(
        SqlResult ctx,
        string engineCode,
        string placeholder)
    {
        var limit = Limit(ctx, engineCode);
        var offset = Offset(ctx, engineCode);
        if (limit is null && offset is null)
            return AbsentClause();

        if (offset is null)
        {
            ctx.Bindings.Add(limit!.Limit);
            return $"LIMIT {placeholder}";
        }

        if (limit is null)
        {
            ctx.Bindings.Add(offset.Offset);
            return $"OFFSET {placeholder}";
        }

        ctx.Bindings.Add(limit.Limit);
        ctx.Bindings.Add(offset.Offset);
        return $"LIMIT {placeholder} OFFSET {placeholder}";
    }
}

/// <summary>
/// Gives Core-owned statement renderers access to SqlKata's protected PrepareResult parameter
/// pipeline without exposing it outside the Core compiler adapters. Callers must provide SQL that
/// was rendered from closed Core AST nodes plus the matching positional bindings.
/// </summary>
internal interface ICoreSqlKataRawCompiler
{
    SqlResult PrepareCoreRaw(string rawSql, IReadOnlyList<object?> bindings);
}

internal static class CoreSqlKataRawCompiler
{
    public static SqlResult Prepare(
        Compiler compiler,
        string rawSql,
        IReadOnlyList<object?> bindings)
    {
        if (compiler is not ICoreSqlKataRawCompiler coreCompiler)
        {
            throw new InvalidOperationException(
                $"Compiler '{compiler.GetType().Name}' does not expose the Core raw parameterization contract.");
        }

        return coreCompiler.PrepareCoreRaw(rawSql, bindings);
    }

    public static SqlResult CreateResult(
        string placeholder,
        string escapeCharacter,
        string rawSql,
        IReadOnlyList<object?> bindings) =>
        new(placeholder, escapeCharacter)
        {
            RawSql = rawSql,
            Bindings = bindings.Select(value => value!).ToList()
        };
}

/// <summary>
/// A statement-level ORDER BY integer is an output position, not a scalar value. Core represents
/// that distinction with an internal marker while the SqlKata query graph still carries an
/// AbstractColumn. Intercept only that marker and emit canonical decimal digits; every ordinary
/// literal continues through SqlKata's parameter binding path.
/// </summary>
internal static class CoreSqlKataOrderByOrdinal
{
    public static bool TryCompile(AbstractColumn column, out string sql)
    {
        if (column is RawColumn raw
            && raw.Expression == "?"
            && raw.Bindings is { Length: 1 }
            && raw.Bindings[0] is OrderByOrdinalValue ordinal)
        {
            if (ordinal.Position <= 0)
                throw new InvalidOperationException("ORDER BY output position must be positive before lowering.");
            sql = ordinal.Position.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        sql = string.Empty;
        return false;
    }
}

/// <summary>
/// SqlKata models GROUP_CONCAT as an ordinary function call, while MySQL's explicit delimiter is
/// clause syntax: GROUP_CONCAT(expr SEPARATOR '...'). Core's canonical string aggregate always has
/// exactly one value expression and one validated literal delimiter, so this adapter rewrites only
/// that closed generated shape after SqlKata has compiled the column. Native multi-expression
/// GROUP_CONCAT input is rejected by source normalization and is never reinterpreted as a delimiter.
/// </summary>
internal static class CoreMySqlStringAggregateSyntax
{
    private const string Prefix = "GROUP_CONCAT(";

    public static string Rewrite(string sql)
    {
        var searchFrom = 0;
        while (searchFrom < sql.Length)
        {
            var start = sql.IndexOf(Prefix, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (start < 0) break;

            var openParen = start + Prefix.Length - 1;
            if (!TryFindArguments(sql, openParen, out var separatorComma, out var closeParen))
            {
                searchFrom = openParen + 1;
                continue;
            }

            var separator = sql[(separatorComma + 1)..closeParen].Trim();
            if (!IsSqlStringLiteral(separator))
            {
                searchFrom = closeParen + 1;
                continue;
            }

            var value = sql[(openParen + 1)..separatorComma].TrimEnd();
            sql = sql[..(openParen + 1)]
                + value
                + " SEPARATOR "
                + separator
                + sql[closeParen..];
            searchFrom = openParen + 1 + value.Length + " SEPARATOR ".Length + separator.Length + 1;
        }

        return sql;
    }

    private static bool TryFindArguments(
        string sql,
        int openParen,
        out int separatorComma,
        out int closeParen)
    {
        separatorComma = -1;
        closeParen = -1;
        var depth = 1;
        char quote = '\0';

        for (var i = openParen + 1; i < sql.Length; i++)
        {
            var current = sql[i];
            if (quote != '\0')
            {
                if (current != quote) continue;
                if (i + 1 < sql.Length && sql[i + 1] == quote)
                {
                    i++;
                    continue;
                }
                quote = '\0';
                continue;
            }

            if (current is '\'' or '`' or '"')
            {
                quote = current;
                continue;
            }

            if (current == '(')
            {
                depth++;
                continue;
            }
            if (current == ')')
            {
                depth--;
                if (depth == 0)
                {
                    closeParen = i;
                    return separatorComma >= 0;
                }
                continue;
            }
            if (current == ',' && depth == 1)
            {
                if (separatorComma >= 0)
                    return false;
                separatorComma = i;
            }
        }

        return false;
    }

    private static bool IsSqlStringLiteral(string value)
    {
        if (value.Length < 2 || value[0] != '\'' || value[^1] != '\'')
            return false;

        for (var i = 1; i < value.Length - 1; i++)
        {
            if (value[i] != '\'') continue;
            if (i + 1 >= value.Length - 1 || value[i + 1] != '\'')
                return false;
            i++;
        }
        return true;
    }
}

internal sealed class CorePostgresCompiler : PostgresCompiler, ICoreSqlKataRawCompiler
{
    public SqlResult PrepareCoreRaw(string rawSql, IReadOnlyList<object?> bindings) =>
        PrepareResult(CoreSqlKataRawCompiler.CreateResult(
            parameterPlaceholder,
            EscapeCharacter,
            rawSql,
            bindings));

    public override string CompileColumn(SqlResult ctx, AbstractColumn column) =>
        CoreSqlKataOrderByOrdinal.TryCompile(column, out var ordinal)
            ? ordinal
            : base.CompileColumn(ctx, column);

    public override string CompileLimit(SqlResult ctx) =>
        CoreSqlKataPagination.CompileAnsiLimitOffset(ctx, EngineCode, parameterPlaceholder);
}

internal sealed class CoreMySqlCompiler : MySqlCompiler, ICoreSqlKataRawCompiler
{
    public SqlResult PrepareCoreRaw(string rawSql, IReadOnlyList<object?> bindings) =>
        PrepareResult(CoreSqlKataRawCompiler.CreateResult(
            parameterPlaceholder,
            EscapeCharacter,
            rawSql,
            bindings));

    public override string CompileColumn(SqlResult ctx, AbstractColumn column)
    {
        if (CoreSqlKataOrderByOrdinal.TryCompile(column, out var ordinal))
            return ordinal;
        return CoreMySqlStringAggregateSyntax.Rewrite(base.CompileColumn(ctx, column));
    }

    public override string CompileLimit(SqlResult ctx)
    {
        var limit = CoreSqlKataPagination.Limit(ctx, EngineCode);
        var offset = CoreSqlKataPagination.Offset(ctx, EngineCode);
        if (limit is null && offset is null)
            return CoreSqlKataPagination.AbsentClause();

        if (offset is null)
        {
            ctx.Bindings.Add(limit!.Limit);
            return $"LIMIT {parameterPlaceholder}";
        }

        if (limit is null)
        {
            ctx.Bindings.Add(offset.Offset);
            return $"LIMIT 18446744073709551615 OFFSET {parameterPlaceholder}";
        }

        ctx.Bindings.Add(limit.Limit);
        ctx.Bindings.Add(offset.Offset);
        return $"LIMIT {parameterPlaceholder} OFFSET {parameterPlaceholder}";
    }
}

internal sealed class CoreSqliteCompiler : SqliteCompiler, ICoreSqlKataRawCompiler
{
    public SqlResult PrepareCoreRaw(string rawSql, IReadOnlyList<object?> bindings) =>
        PrepareResult(CoreSqlKataRawCompiler.CreateResult(
            parameterPlaceholder,
            EscapeCharacter,
            rawSql,
            bindings));

    public override string CompileColumn(SqlResult ctx, AbstractColumn column) =>
        CoreSqlKataOrderByOrdinal.TryCompile(column, out var ordinal)
            ? ordinal
            : base.CompileColumn(ctx, column);

    public override string CompileLimit(SqlResult ctx)
    {
        var limit = CoreSqlKataPagination.Limit(ctx, EngineCode);
        var offset = CoreSqlKataPagination.Offset(ctx, EngineCode);
        if (limit is null && offset is null)
            return CoreSqlKataPagination.AbsentClause();

        if (limit is null)
        {
            ctx.Bindings.Add(offset!.Offset);
            return $"LIMIT -1 OFFSET {parameterPlaceholder}";
        }

        if (offset is null)
        {
            ctx.Bindings.Add(limit.Limit);
            return $"LIMIT {parameterPlaceholder}";
        }

        ctx.Bindings.Add(limit.Limit);
        ctx.Bindings.Add(offset.Offset);
        return $"LIMIT {parameterPlaceholder} OFFSET {parameterPlaceholder}";
    }
}

internal sealed class CoreOracleCompiler : OracleCompiler, ICoreSqlKataRawCompiler
{
    public SqlResult PrepareCoreRaw(string rawSql, IReadOnlyList<object?> bindings) =>
        PrepareResult(CoreSqlKataRawCompiler.CreateResult(
            parameterPlaceholder,
            EscapeCharacter,
            rawSql,
            bindings));

    public override string CompileColumn(SqlResult ctx, AbstractColumn column) =>
        CoreSqlKataOrderByOrdinal.TryCompile(column, out var ordinal)
            ? ordinal
            : base.CompileColumn(ctx, column);

    public override string CompileFrom(SqlResult ctx) =>
        ctx.Query.HasComponent("from", EngineCode)
            ? base.CompileFrom(ctx)
            : "FROM DUAL";

    public override string CompileLimit(SqlResult ctx)
    {
        if (UseLegacyPagination)
            return base.CompileLimit(ctx);

        var limit = CoreSqlKataPagination.Limit(ctx, EngineCode);
        var offset = CoreSqlKataPagination.Offset(ctx, EngineCode);
        if (limit is null && offset is null)
            return CoreSqlKataPagination.AbsentClause();

        var safeOrder = ctx.Query.HasComponent("order", EngineCode)
            ? string.Empty
            : "ORDER BY (SELECT 0 FROM DUAL) ";

        if (limit is null)
        {
            ctx.Bindings.Add(offset!.Offset);
            return $"{safeOrder}OFFSET {parameterPlaceholder} ROWS";
        }

        // Oracle treats a rowcount of zero as an empty result set. Keep the stock compiler's
        // OFFSET/FETCH shape for positive limits and use the same shape for explicit zero.
        ctx.Bindings.Add(offset?.Offset ?? 0L);
        ctx.Bindings.Add(limit.Limit);
        return $"{safeOrder}OFFSET {parameterPlaceholder} ROWS FETCH NEXT {parameterPlaceholder} ROWS ONLY";
    }
}

internal sealed class CoreFirebirdCompiler : FirebirdCompiler, ICoreSqlKataRawCompiler
{
    public SqlResult PrepareCoreRaw(string rawSql, IReadOnlyList<object?> bindings) =>
        PrepareResult(CoreSqlKataRawCompiler.CreateResult(
            parameterPlaceholder,
            EscapeCharacter,
            rawSql,
            bindings));

    public override string CompileColumn(SqlResult ctx, AbstractColumn column) =>
        CoreSqlKataOrderByOrdinal.TryCompile(column, out var ordinal)
            ? ordinal
            : base.CompileColumn(ctx, column);

    public override string CompileFrom(SqlResult ctx) =>
        ctx.Query.HasComponent("from", EngineCode)
            ? base.CompileFrom(ctx)
            : "FROM RDB$DATABASE";

    public override string WrapValue(string value)
    {
        if (value == "*") return value;

        // The Core lowerer owns quoted-vs-unquoted case semantics. The stock Firebird compiler
        // uppercases every value before quoting, which destroys the case of a delimited identifier.
        // Here we only delimit and escape the already-normalized Core value.
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    protected override string CompileColumns(SqlResult ctx)
    {
        var zeroLimit = CoreSqlKataPagination.Limit(ctx, EngineCode) is { Limit: 0 };
        var compiled = base.CompileColumns(ctx);
        if (!zeroLimit)
            return compiled;

        // Firebird 1.5+ defines FIRST 0 as an empty result set. If SKIP was present, the base
        // compiler has already rendered it; FIRST 0 remains semantically empty for every offset.
        ctx.Bindings.Insert(0, 0);
        ctx.Query.ClearComponent("limit", EngineCode);
        return $"SELECT FIRST {parameterPlaceholder}{compiled[6..]}";
    }
}

internal sealed class CoreSqlServerCompiler : SqlServerCompiler, ICoreSqlKataRawCompiler
{
    public SqlResult PrepareCoreRaw(string rawSql, IReadOnlyList<object?> bindings) =>
        PrepareResult(CoreSqlKataRawCompiler.CreateResult(
            parameterPlaceholder,
            EscapeCharacter,
            rawSql,
            bindings));

    public override string CompileColumn(SqlResult ctx, AbstractColumn column) =>
        CoreSqlKataOrderByOrdinal.TryCompile(column, out var ordinal)
            ? ordinal
            : base.CompileColumn(ctx, column);

    protected override string CompileColumns(SqlResult ctx)
    {
        var zeroLimit = CoreSqlKataPagination.Limit(ctx, EngineCode) is { Limit: 0 };
        var compiled = base.CompileColumns(ctx);
        if (!zeroLimit)
            return compiled;

        // SQL Server rejects FETCH NEXT 0, while TOP (0) is a native empty-result operator and
        // remains correct for aggregates. Remove the pagination components after capturing the
        // zero so non-legacy compilation cannot combine TOP with OFFSET/FETCH.
        ctx.Bindings.Insert(0, 0);
        ctx.Query.ClearComponent("limit", EngineCode);
        ctx.Query.ClearComponent("offset", EngineCode);

        if (compiled.StartsWith("SELECT DISTINCT", StringComparison.Ordinal))
            return $"SELECT DISTINCT TOP ({parameterPlaceholder}){compiled[15..]}";

        return $"SELECT TOP ({parameterPlaceholder}){compiled[6..]}";
    }
}
