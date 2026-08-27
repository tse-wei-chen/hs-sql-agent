using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreSqliteStringAggregateOrderingTests
{
    private static readonly Version SupportedVersion = new(3, 44);
    private static readonly Version OldVersion = new(3, 43);

    [Fact]
    public void Parse_SqliteGroupConcatOrdering_RecordsInlineSourceSyntax()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT GROUP_CONCAT(name ORDER BY created_at) FROM users",
            SqlAgentToolType.Sqlite);
        var function = Assert.IsType<FunctionCallExpr>(
            Assert.Single(Assert.IsType<SelectStatement>(parsed.Statement).Select).Expression);

        Assert.Equal(AggregateOrderSyntaxKind.Inline, function.AggregateOrderSyntax);
        Assert.Single(function.AggregateOrderBy);
    }

    [Fact]
    public void Compile_Sqlite344GroupConcatOrdering_IsEnabledByBothProfiles()
    {
        var command = CompileRaw(
            "SELECT GROUP_CONCAT(name, '|' ORDER BY created_at DESC, name ASC NULLS LAST) FROM users",
            SupportedVersion,
            SupportedVersion);

        Assert.Contains("GROUP_CONCAT(", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DESC", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NULLS LAST", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_Sqlite344DefaultSeparatorOrdering_IsCanonicalized()
    {
        var command = CompileRaw(
            "SELECT GROUP_CONCAT(name ORDER BY created_at) FROM users",
            SupportedVersion,
            SupportedVersion);

        Assert.Contains("GROUP_CONCAT(", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("','", command.Sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SqliteOrdering_MissingSourceVersion_FailsClosed()
    {
        var error = Assert.Throws<SqlCompilationException>(() => CompileRaw(
            "SELECT GROUP_CONCAT(name ORDER BY created_at) FROM users",
            sourceVersion: null,
            targetVersion: SupportedVersion));

        Assert.Contains("source capability profile", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3.44", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_SqliteOrdering_OldSourceVersion_FailsClosed()
    {
        var error = Assert.Throws<SqlCompilationException>(() => CompileRaw(
            "SELECT GROUP_CONCAT(name ORDER BY created_at) FROM users",
            OldVersion,
            SupportedVersion));

        Assert.Contains("source ServerVersion", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3.43", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_SqliteOrdering_MissingTargetVersion_FailsClosed()
    {
        var error = Assert.Throws<SqlCompilationException>(() => CompileRaw(
            "SELECT GROUP_CONCAT(name ORDER BY created_at) FROM users",
            SupportedVersion,
            targetVersion: null));

        Assert.Contains("target capability profile", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3.44", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_SqliteOrdering_OldTargetVersion_FailsClosed()
    {
        var error = Assert.Throws<SqlCompilationException>(() => CompileRaw(
            "SELECT GROUP_CONCAT(name ORDER BY created_at) FROM users",
            SupportedVersion,
            OldVersion));

        Assert.Contains("target ServerVersion", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3.43", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_StructuredOrdering_RequiresOnlySqliteTargetVersion()
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
            SqlAgentToolType.Sqlite,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy(),
            TargetProfile(SupportedVersion));

        Assert.Contains("GROUP_CONCAT(", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Matrix_SqliteOrdering_IsVersionGated()
    {
        var missing = Assert.Single(
            SqlCapabilityMatrix.ForProvider(SqlAgentToolType.Sqlite).Capabilities,
            capability => capability.Id == "aggregate.string.ordering");
        var old = Assert.Single(
            SqlCapabilityMatrix.ForProvider(
                SqlAgentToolType.Sqlite,
                TargetProfile(OldVersion)).Capabilities,
            capability => capability.Id == "aggregate.string.ordering");
        var supported = Assert.Single(
            SqlCapabilityMatrix.ForProvider(
                SqlAgentToolType.Sqlite,
                TargetProfile(SupportedVersion)).Capabilities,
            capability => capability.Id == "aggregate.string.ordering");

        Assert.Equal(SqlCapabilityStatus.Rejected, missing.Status);
        Assert.Equal(SqlCapabilityStatus.Rejected, old.Status);
        Assert.Equal(SqlCapabilityStatus.Supported, supported.Status);
        Assert.Contains("3.44", supported.Detail, StringComparison.Ordinal);
    }

    private static CompiledSqlCommand CompileRaw(
        string sql,
        Version? sourceVersion,
        Version? targetVersion)
    {
        var sourceProfile = sourceVersion is null ? null : SourceProfile(sourceVersion);
        var targetProfile = targetVersion is null ? null : TargetProfile(targetVersion);
        return CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Sqlite, sourceProfile),
            SqlAgentToolType.Sqlite,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy(),
            targetProfile);
    }

    private static SqlProviderCapabilityProfile SourceProfile(Version version) =>
        new(SqlAgentToolType.Sqlite, ServerVersion: version);

    private static SqlProviderCapabilityProfile TargetProfile(Version version) =>
        new(SqlAgentToolType.Sqlite, ServerVersion: version);
}
