using System.Collections.Immutable;

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
        var capabilityError = SqlJsonCapabilityRules.TargetValidationError(
            "CORE_JSON_EXTRACT",
            provider);
        if (capabilityError is not null)
            throw new SqlCompilationException(capabilityError);
        var pathError = SqlJsonCapabilityRules.PathValidationError(
            function,
            "CORE_JSON_EXTRACT",
            provider);
        if (pathError is not null)
            throw new SqlCompilationException(pathError);

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

        var segments = SqlJsonCapabilityRules.PropertyPathSegments(
            function);
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
        var capabilityError = SqlJsonCapabilityRules.TargetValidationError(
            "CORE_JSON_SET",
            provider);
        if (capabilityError is not null)
            throw new SqlCompilationException(capabilityError);
        var pathError = SqlJsonCapabilityRules.PathValidationError(
            function,
            "CORE_JSON_SET",
            provider);
        if (pathError is not null)
            throw new SqlCompilationException(pathError);

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
            string.Join(
                ',',
                SqlJsonCapabilityRules.PropertyPathSegments(
                    function)) +
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
        var capabilityError = SqlRegexCapabilityRules.TargetValidationError(
            provider,
            targetProfile: null);
        if (capabilityError is not null)
            throw new SqlCompilationException(capabilityError);

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


}
