using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreOracleStringAggregateOrderingTests
{
    private static readonly Version SupportedVersion = new(11, 2);
    private static readonly Version OldVersion = new(11, 1);

    [Fact]
    public void Parse_OracleWithinGroupOrdering_IsStructuredAndTagged()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT LISTAGG(name, ',') WITHIN GROUP (ORDER BY created_at DESC, name ASC NULLS LAST) FROM users",
            SqlAgentToolType.Oracle);
        var function = Assert.IsType<FunctionCallExpr>(
            Assert.Single(Assert.IsType<SelectStatement>(parsed.Statement).Select).Expression);

        Assert.Equal(2, function.Arguments.Length);
        Assert.Equal(2, function.AggregateOrderBy.Length);
        Assert.Equal(AggregateOrderSyntaxKind.WithinGroup, function.AggregateOrderSyntax);
        Assert.True(function.AggregateOrderBy[0].Descending);
        Assert.Equal(NullOrderingKind.Last, function.AggregateOrderBy[1].NullOrdering);
    }

    [Fact]
    public void Compile_Oracle112WithinGroupOrdering_UsesNativeTargetSyntax()
    {
        var command = CompileRaw(
            "SELECT LISTAGG(name, '|') WITHIN GROUP (ORDER BY created_at DESC, name ASC NULLS LAST) FROM users",
            SourceProfile(SupportedVersion),
            TargetProfile(SupportedVersion));

        Assert.Contains("LISTAGG(", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WITHIN GROUP (ORDER BY", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DESC", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NULLS LAST", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_OracleOneArgumentListAgg_NormalizesToNoDelimiterAcrossProviders()
    {
        var command = CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(
                "SELECT LISTAGG(name) WITHIN GROUP (ORDER BY created_at) FROM users",
                SqlAgentToolType.Oracle,
                SourceProfile(SupportedVersion)),
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy());

        Assert.Contains("STRING_AGG(", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("''", command.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("','", command.Sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_OracleOrderedOneArgumentListAgg_PreservesNoDelimiter()
    {
        var command = CompileRaw(
            "SELECT LISTAGG(name) WITHIN GROUP (ORDER BY created_at) FROM users",
            SourceProfile(SupportedVersion),
            TargetProfile(SupportedVersion));

        Assert.Contains("LISTAGG(", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("''", command.Sql, StringComparison.Ordinal);
        Assert.Contains("WITHIN GROUP (ORDER BY", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_OracleOrdering_MissingSourceVersion_FailsClosed()
    {
        var error = Assert.Throws<SqlCompilationException>(() => CompileRaw(
            "SELECT LISTAGG(name, ',') WITHIN GROUP (ORDER BY created_at) FROM users",
            SourceProfile(serverVersion: null),
            TargetProfile(SupportedVersion)));

        Assert.Contains("source capability profile", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("11.2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_OracleOrdering_OldSourceVersion_FailsClosed()
    {
        var error = Assert.Throws<SqlCompilationException>(() => CompileRaw(
            "SELECT LISTAGG(name, ',') WITHIN GROUP (ORDER BY created_at) FROM users",
            SourceProfile(OldVersion),
            TargetProfile(SupportedVersion)));

        Assert.Contains("source ServerVersion", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("11.1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_OracleOrdering_MissingTargetVersion_FailsClosed()
    {
        var error = Assert.Throws<SqlCompilationException>(() => CompileRaw(
            "SELECT LISTAGG(name, ',') WITHIN GROUP (ORDER BY created_at) FROM users",
            SourceProfile(SupportedVersion),
            TargetProfile(serverVersion: null)));

        Assert.Contains("target capability profile", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("11.2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_OracleOrdering_OldTargetVersion_FailsClosed()
    {
        var error = Assert.Throws<SqlCompilationException>(() => CompileRaw(
            "SELECT LISTAGG(name, ',') WITHIN GROUP (ORDER BY created_at) FROM users",
            SourceProfile(SupportedVersion),
            TargetProfile(OldVersion)));

        Assert.Contains("target ServerVersion", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("11.1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_OracleRawInlineOrderingSpelling_FailsClosed()
    {
        var error = Assert.Throws<SqlCompilationException>(() => CompileRaw(
            "SELECT LISTAGG(name, ',' ORDER BY created_at) FROM users",
            SourceProfile(SupportedVersion),
            TargetProfile(SupportedVersion)));

        Assert.Contains("WITHIN GROUP", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_StructuredOrdering_RequiresOnlyOracleTargetVersion()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT STRING_AGG(name, ',' ORDER BY created_at) FROM users",
            SqlAgentToolType.Postgres) with
        {
            SourceDialect = SqlAgentToolType.MySQL,
            EnforceSourceDialectSyntax = false,
            SourceProfile = null
        };

        var command = CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Oracle,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy(),
            TargetProfile(SupportedVersion));

        Assert.Contains("LISTAGG(", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WITHIN GROUP (ORDER BY", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Matrix_OracleOrdering_IsVersionGated()
    {
        var missing = Assert.Single(
            SqlCapabilityMatrix.ForProvider(SqlAgentToolType.Oracle).Capabilities,
            capability => capability.Id == "aggregate.string.ordering");
        var old = Assert.Single(
            SqlCapabilityMatrix.ForProvider(
                SqlAgentToolType.Oracle,
                TargetProfile(OldVersion)).Capabilities,
            capability => capability.Id == "aggregate.string.ordering");
        var supported = Assert.Single(
            SqlCapabilityMatrix.ForProvider(
                SqlAgentToolType.Oracle,
                TargetProfile(SupportedVersion)).Capabilities,
            capability => capability.Id == "aggregate.string.ordering");

        Assert.Equal(SqlCapabilityStatus.Rejected, missing.Status);
        Assert.Equal(SqlCapabilityStatus.Rejected, old.Status);
        Assert.Equal(SqlCapabilityStatus.Supported, supported.Status);
        Assert.Contains("11.2", supported.Detail, StringComparison.Ordinal);
    }

    private static CompiledSqlCommand CompileRaw(
        string sql,
        SqlProviderCapabilityProfile sourceProfile,
        SqlProviderCapabilityProfile targetProfile) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Oracle, sourceProfile),
            SqlAgentToolType.Oracle,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy(),
            targetProfile);

    private static SqlProviderCapabilityProfile SourceProfile(Version? serverVersion) =>
        new(SqlAgentToolType.Oracle, ServerVersion: serverVersion);

    private static SqlProviderCapabilityProfile TargetProfile(Version? serverVersion) =>
        new(SqlAgentToolType.Oracle, ServerVersion: serverVersion);
}
