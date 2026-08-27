using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace HsSqlAgent.SqlCore.Core.Lowering;

/// <summary>
/// Structured-text expression rendering for JSON mutation/extraction and regular-expression
/// predicates. SQL spelling remains provider-specific while values and JSON path segments stay
/// parameterized or structurally validated before emission.
/// </summary>
internal static partial class NativeSqlExpressionRenderer
{
    private static NativeSqlFragment RenderJsonExtract(
        FunctionCallExpr function,
        SqlAgentToolType provider,
        Func<SqlStatement, NativeSqlFragment> renderSubquery,
        bool dmlContext)
    {
        RequireArguments(function, 2);
        var value = Render(
            function.Arguments[0],
            provider,
            renderSubquery,
            dmlContext);

        if (provider is SqlAgentToolType.MySQL or SqlAgentToolType.Sqlite)
        {
            var path = Render(
                function.Arguments[1],
                provider,
                renderSubquery,
                dmlContext);
            return Combine(
                "JSON_EXTRACT(" + value.Sql + ", " + path.Sql + ")",
                value,
                path);
        }

        if (provider != SqlAgentToolType.Postgres)
        {
            throw new SqlCompilationException(
                "JSON_EXTRACT is not supported losslessly by this provider.");
        }

        var segments = JsonPathSegments(function.Arguments[1]);
        var bindings = value.Bindings.ToBuilder();
        var placeholders = new List<string>();
        foreach (var segment in segments)
        {
            placeholders.Add(P);
            bindings.Add(segment);
        }

        return new NativeSqlFragment(
            "JSONB_EXTRACT_PATH(CAST(" + value.Sql + " AS jsonb), " +
            string.Join(", ", placeholders) + ")",
            bindings.ToImmutable());
    }

    private static NativeSqlFragment RenderJsonSet(
        FunctionCallExpr function,
        SqlAgentToolType provider,
        Func<SqlStatement, NativeSqlFragment> renderSubquery,
        bool dmlContext)
    {
        RequireArguments(function, 3);
        var value = Render(
            function.Arguments[0],
            provider,
            renderSubquery,
            dmlContext);
        var newValue = Render(
            function.Arguments[2],
            provider,
            renderSubquery,
            dmlContext);

        if (provider is SqlAgentToolType.MySQL or SqlAgentToolType.Sqlite)
        {
            var path = Render(
                function.Arguments[1],
                provider,
                renderSubquery,
                dmlContext);
            return new NativeSqlFragment(
                "JSON_SET(" + value.Sql + ", " + path.Sql + ", " + newValue.Sql + ")",
                value.Bindings
                    .Concat(path.Bindings)
                    .Concat(newValue.Bindings)
                    .ToImmutableArray());
        }

        if (provider == SqlAgentToolType.MsSqlServer)
        {
            var path = Render(
                function.Arguments[1],
                provider,
                renderSubquery,
                dmlContext);
            return new NativeSqlFragment(
                "JSON_MODIFY(" + value.Sql + ", " + path.Sql + ", " + newValue.Sql + ")",
                value.Bindings
                    .Concat(path.Bindings)
                    .Concat(newValue.Bindings)
                    .ToImmutableArray());
        }

        if (provider != SqlAgentToolType.Postgres)
        {
            throw new SqlCompilationException(
                "JSON_SET is not supported by this provider.");
        }

        var pgPath = "{" +
            string.Join(',', JsonPathSegments(function.Arguments[1])) +
            "}";
        return new NativeSqlFragment(
            "JSONB_SET(CAST(" + value.Sql + " AS jsonb), CAST(" + P +
            " AS text[]), TO_JSONB(" + newValue.Sql + "))",
            value.Bindings
                .Concat([pgPath])
                .Concat(newValue.Bindings)
                .ToImmutableArray());
    }

    private static NativeSqlFragment RenderRegexMatch(
        FunctionCallExpr function,
        SqlAgentToolType provider,
        Func<SqlStatement, NativeSqlFragment> renderSubquery,
        bool dmlContext)
    {
        RequireArguments(function, 2);
        if (provider is SqlAgentToolType.MsSqlServer
            or SqlAgentToolType.Sqlite
            or SqlAgentToolType.Firebird)
        {
            throw new SqlCompilationException(
                "REGEXP_LIKE is not supported by this provider.");
        }

        var value = Render(
            function.Arguments[0],
            provider,
            renderSubquery,
            dmlContext);
        var pattern = Render(
            function.Arguments[1],
            provider,
            renderSubquery,
            dmlContext);

        return provider == SqlAgentToolType.Postgres
            ? Combine(
                "(" + value.Sql + " ~ " + pattern.Sql + ")",
                value,
                pattern)
            : Combine(
                "REGEXP_LIKE(" + value.Sql + ", " + pattern.Sql + ")",
                value,
                pattern);
    }

    private static IReadOnlyList<string> JsonPathSegments(
        SqlExpr expression)
    {
        if (expression is not LiteralExpr { Value: string path })
        {
            throw new SqlCompilationException(
                "JSON path must be a string literal for structured PostgreSQL lowering.");
        }

        var trimmed = path.Trim();
        if (!trimmed.StartsWith('$'))
            throw new SqlCompilationException("Unsupported JSON path '" + path + "'.");

        var remainder = trimmed[1..].TrimStart('.');
        if (string.IsNullOrEmpty(remainder))
            return [];

        var segments = remainder.Split(
            '.',
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries);
        if (segments.Any(segment => !Regex.IsMatch(
                segment,
                "^[A-Za-z_][A-Za-z0-9_]*$",
                RegexOptions.CultureInvariant)))
        {
            throw new SqlCompilationException(
                "Unsupported structured JSON path '" + path + "'.");
        }

        return segments;
    }
}
