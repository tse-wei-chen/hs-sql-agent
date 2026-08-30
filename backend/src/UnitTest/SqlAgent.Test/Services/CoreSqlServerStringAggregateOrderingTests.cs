using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreSqlServerStringAggregateOrderingTests
{
    private static readonly Version SupportedVersion = new(14, 0);
    private static readonly Version OldVersion = new(13, 0);
    private const int SupportedCompatibilityLevel = 110;
    private const int OldCompatibilityLevel = 100;

    [Fact]
    public void Parse_SqlServerWithinGroupOrdering_IsStructuredAndTagged()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT STRING_AGG(name, ',') WITHIN GROUP (ORDER BY created_at DESC, name ASC) FROM users",
            SqlAgentToolType.MsSqlServer);
        var function = Assert.IsType<FunctionCallExpr>(
            Assert.Single(Assert.IsType<SelectStatement>(parsed.Statement).Select).Expression);

        Assert.Equal(2, function.Arguments.Length);
        Assert.Equal(2, function.AggregateOrderBy.Length);
        Assert.Equal(AggregateOrderSyntaxKind.WithinGroup, function.AggregateOrderSyntax);
        Assert.True(function.AggregateOrderBy[0].Descending);
        Assert.False(function.AggregateOrderBy[1].Descending);
    }

    [Fact]
    public void Compile_SqlServerWithinGroupOrdering_UsesNativeTargetSyntax()
    {
        var command = CompileRaw(
            "SELECT STRING_AGG(name, '|') WITHIN GROUP (ORDER BY created_at DESC, name ASC) FROM users",
            Profile(SupportedVersion, SupportedCompatibilityLevel),
            Profile(SupportedVersion, SupportedCompatibilityLevel));

        Assert.Contains("STRING_AGG(", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WITHIN GROUP (ORDER BY", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DESC", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ASC", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SqlServerOrdering_BindsNestedOrderExpressionParameters()
    {
        var command = CompileRaw(
            "SELECT STRING_AGG(name, ',') WITHIN GROUP (ORDER BY COALESCE(sort_key, 'fallback') DESC) FROM users",
            Profile(SupportedVersion, SupportedCompatibilityLevel),
            Profile(SupportedVersion, SupportedCompatibilityLevel));

        Assert.Contains("WITHIN GROUP", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COALESCE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, "fallback"));
    }

    [Fact]
    public void Compile_SqlServerOrdering_MissingSourceVersion_FailsClosed()
    {
        var error = Assert.Throws<SqlCompilationException>(() => CompileRaw(
            "SELECT STRING_AGG(name, ',') WITHIN GROUP (ORDER BY created_at) FROM users",
            Profile(serverVersion: null, compatibilityLevel: SupportedCompatibilityLevel),
            Profile(SupportedVersion, SupportedCompatibilityLevel)));

        Assert.Contains("source capability profile", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ServerVersion", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("14.0", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_SqlServerOrdering_OldSourceVersion_FailsClosed()
    {
        var error = Assert.Throws<SqlCompilationException>(() => CompileRaw(
            "SELECT STRING_AGG(name, ',') WITHIN GROUP (ORDER BY created_at) FROM users",
            Profile(OldVersion, SupportedCompatibilityLevel),
            Profile(SupportedVersion, SupportedCompatibilityLevel)));

        Assert.Contains("source ServerVersion", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("13.0", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_SqlServerOrdering_MissingSourceCompatibilityLevel_FailsClosed()
    {
        var error = Assert.Throws<SqlCompilationException>(() => CompileRaw(
            "SELECT STRING_AGG(name, ',') WITHIN GROUP (ORDER BY created_at) FROM users",
            Profile(SupportedVersion, compatibilityLevel: null),
            Profile(SupportedVersion, SupportedCompatibilityLevel)));

        Assert.Contains("source capability profile", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CompatibilityLevel", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("110", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_SqlServerOrdering_OldSourceCompatibilityLevel_FailsClosed()
    {
        var error = Assert.Throws<SqlCompilationException>(() => CompileRaw(
            "SELECT STRING_AGG(name, ',') WITHIN GROUP (ORDER BY created_at) FROM users",
            Profile(SupportedVersion, OldCompatibilityLevel),
            Profile(SupportedVersion, SupportedCompatibilityLevel)));

        Assert.Contains("source CompatibilityLevel", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("100", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_SqlServerOrdering_MissingTargetVersion_FailsClosed()
    {
        var error = Assert.Throws<SqlCompilationException>(() => CompileRaw(
            "SELECT STRING_AGG(name, ',') WITHIN GROUP (ORDER BY created_at) FROM users",
            Profile(SupportedVersion, SupportedCompatibilityLevel),
            Profile(serverVersion: null, compatibilityLevel: SupportedCompatibilityLevel)));

        Assert.Contains("target capability profile", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ServerVersion", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("14.0", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_SqlServerOrdering_OldTargetVersion_FailsClosed()
    {
        var error = Assert.Throws<SqlCompilationException>(() => CompileRaw(
            "SELECT STRING_AGG(name, ',') WITHIN GROUP (ORDER BY created_at) FROM users",
            Profile(SupportedVersion, SupportedCompatibilityLevel),
            Profile(OldVersion, SupportedCompatibilityLevel)));

        Assert.Contains("target ServerVersion", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("13.0", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_SqlServerOrdering_MissingTargetCompatibilityLevel_FailsClosed()
    {
        var error = Assert.Throws<SqlCompilationException>(() => CompileRaw(
            "SELECT STRING_AGG(name, ',') WITHIN GROUP (ORDER BY created_at) FROM users",
            Profile(SupportedVersion, SupportedCompatibilityLevel),
            Profile(SupportedVersion, compatibilityLevel: null)));

        Assert.Contains("target capability profile", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CompatibilityLevel", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("110", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_SqlServerOrdering_OldTargetCompatibilityLevel_FailsClosed()
    {
        var error = Assert.Throws<SqlCompilationException>(() => CompileRaw(
            "SELECT STRING_AGG(name, ',') WITHIN GROUP (ORDER BY created_at) FROM users",
            Profile(SupportedVersion, SupportedCompatibilityLevel),
            Profile(SupportedVersion, OldCompatibilityLevel)));

        Assert.Contains("target CompatibilityLevel", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("100", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_SqlServerRawInlineOrderingSpelling_FailsClosed()
    {
        var error = Assert.Throws<SqlCompilationException>(() => CompileRaw(
            "SELECT STRING_AGG(name, ',' ORDER BY created_at) FROM users",
            Profile(SupportedVersion, SupportedCompatibilityLevel),
            Profile(SupportedVersion, SupportedCompatibilityLevel)));

        Assert.Contains("WITHIN GROUP", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SqlServerWithinGroupConstantOrdering_FailsClosed()
    {
        var error = Assert.Throws<SqlCompilationException>(() => CompileRaw(
            "SELECT STRING_AGG(name, ',') WITHIN GROUP (ORDER BY 1) FROM users",
            Profile(SupportedVersion, SupportedCompatibilityLevel),
            Profile(SupportedVersion, SupportedCompatibilityLevel)));

        Assert.Contains("non-constant", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("column", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_StructuredOrdering_RequiresOnlySqlServerTargetRuntimeContract()
    {
        var parsedSource = CoreSqlTextParser.ParseQuery(
            "SELECT STRING_AGG(name, ',' ORDER BY created_at) FROM users",
            SqlAgentToolType.Postgres);
        var parsed = new ParsedStatement(
            parsedSource.Statement,
            SqlAgentToolType.MySQL,
            false);

        var command = CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.MsSqlServer,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy(),
            Profile(SupportedVersion, SupportedCompatibilityLevel));

        Assert.Contains("STRING_AGG(", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WITHIN GROUP (ORDER BY", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Matrix_SqlServerOrdering_IsVersionAndCompatibilityGated()
    {
        var missing = Assert.Single(
            SqlCapabilityMatrix.ForProvider(SqlAgentToolType.MsSqlServer).Capabilities,
            capability => capability.Id == "aggregate.string.ordering");
        var oldVersion = Assert.Single(
            SqlCapabilityMatrix.ForProvider(
                SqlAgentToolType.MsSqlServer,
                Profile(OldVersion, SupportedCompatibilityLevel)).Capabilities,
            capability => capability.Id == "aggregate.string.ordering");
        var oldCompatibility = Assert.Single(
            SqlCapabilityMatrix.ForProvider(
                SqlAgentToolType.MsSqlServer,
                Profile(SupportedVersion, OldCompatibilityLevel)).Capabilities,
            capability => capability.Id == "aggregate.string.ordering");
        var supported = Assert.Single(
            SqlCapabilityMatrix.ForProvider(
                SqlAgentToolType.MsSqlServer,
                Profile(SupportedVersion, SupportedCompatibilityLevel)).Capabilities,
            capability => capability.Id == "aggregate.string.ordering");

        Assert.Equal(SqlCapabilityStatus.Rejected, missing.Status);
        Assert.Equal(SqlCapabilityStatus.Rejected, oldVersion.Status);
        Assert.Equal(SqlCapabilityStatus.Rejected, oldCompatibility.Status);
        Assert.Equal(SqlCapabilityStatus.Supported, supported.Status);
        Assert.Contains("14.0", supported.Detail, StringComparison.Ordinal);
        Assert.Contains("110", supported.Detail, StringComparison.Ordinal);
    }

    private static CompiledSqlCommand CompileRaw(
        string sql,
        SqlProviderCapabilityProfile sourceProfile,
        SqlProviderCapabilityProfile targetProfile) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.MsSqlServer, sourceProfile),
            SqlAgentToolType.MsSqlServer,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy(),
            targetProfile);

    private static SqlProviderCapabilityProfile Profile(
        Version? serverVersion,
        int? compatibilityLevel) =>
        new(
            SqlAgentToolType.MsSqlServer,
            serverVersion,
            compatibilityLevel,
            null,
            null);
}
