using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreRecursiveCteCapabilityTests
{
    private const string RecursiveSql =
        "WITH RECURSIVE x(n) AS (" +
        "SELECT 1 UNION ALL SELECT n + 1 FROM x WHERE n < 3" +
        ") SELECT n FROM x";

    [Fact]
    public void Parse_WithRecursiveWithoutSelfReference_IsPreservedStructurally()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "WITH RECURSIVE x AS (SELECT 1) SELECT * FROM x",
            SqlAgentToolType.Postgres);
        var select = Assert.IsType<SelectStatement>(parsed.Statement);
        var cte = Assert.Single(select.Ctes);

        Assert.True(cte.RecursiveScope);
    }

    [Fact]
    public void Compile_RecursiveUnionAllSelfReference_EmitsNativePostgresSemantics()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            RecursiveSql,
            SqlAgentToolType.Postgres);

        var command = Compile(parsed, SqlAgentToolType.Postgres);

        Assert.Contains("WITH RECURSIVE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UNION ALL", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_RecursiveAnchorCannotReferenceItself()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "WITH RECURSIVE x AS (SELECT id FROM x) SELECT * FROM x",
            SqlAgentToolType.Postgres);

        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile(parsed, SqlAgentToolType.Postgres));

        Assert.Contains("select.recursive_cte", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("anchor", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_RecursiveTermRequiresExactlyOneDirectSelfReference()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "WITH RECURSIVE x(n) AS (" +
            "SELECT 1 UNION ALL SELECT a.n + b.n FROM x a JOIN x b ON TRUE" +
            ") SELECT n FROM x",
            SqlAgentToolType.Postgres);

        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile(parsed, SqlAgentToolType.Postgres));

        Assert.Contains("select.recursive_cte", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exactly one direct self-reference", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_Postgres83RecursiveCte_FailsAtSourceBoundary()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                "WITH RECURSIVE x AS (SELECT 1) SELECT * FROM x",
                SqlAgentToolType.Postgres,
                Profile(SqlAgentToolType.Postgres, 8, 3)));

        Assert.Contains("select.recursive_cte", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("8.4", error.Message, StringComparison.Ordinal);
        Assert.Contains("source", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_Postgres83RecursiveTarget_FailsBeforeRender()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "WITH RECURSIVE x AS (SELECT 1) SELECT * FROM x",
            SqlAgentToolType.Postgres);

        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile(
                parsed,
                SqlAgentToolType.Postgres,
                Profile(SqlAgentToolType.Postgres, 8, 3)));

        Assert.Contains("select.recursive_cte", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("8.4", error.Message, StringComparison.Ordinal);
        Assert.Contains("target", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_PostgresRecursiveCteToMySql_FailsAtTargetProof()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "WITH RECURSIVE x AS (SELECT 1) SELECT * FROM x",
            SqlAgentToolType.Postgres);

        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile(parsed, SqlAgentToolType.MySQL));

        Assert.Contains("select.recursive_cte", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MySQL", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Matrix_RecursiveCte_TracksPostgresVersionAndProvider()
    {
        Assert.Equal(
            SqlCapabilityStatus.Supported,
            Capability(SqlAgentToolType.Postgres, null).Status);
        Assert.Equal(
            SqlCapabilityStatus.Rejected,
            Capability(
                SqlAgentToolType.Postgres,
                Profile(SqlAgentToolType.Postgres, 8, 3)).Status);
        Assert.Equal(
            SqlCapabilityStatus.Supported,
            Capability(
                SqlAgentToolType.Postgres,
                Profile(SqlAgentToolType.Postgres, 8, 4)).Status);
        Assert.Equal(
            SqlCapabilityStatus.Rejected,
            Capability(SqlAgentToolType.MySQL, null).Status);
    }

    private static SqlCapability Capability(
        SqlAgentToolType provider,
        SqlProviderCapabilityProfile? profile) =>
        Assert.Single(
            SqlCapabilityMatrix.ForProvider(provider, profile).Capabilities,
            item => item.Id == "select.recursive_cte");

    private static CompiledSqlCommand Compile(
        ParsedStatement parsed,
        SqlAgentToolType targetProvider,
        SqlProviderCapabilityProfile? targetProfile = null) =>
        CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            targetProvider,
            new SqlPlanValidationContext("recursive-cte-v1"),
            new SqlExecutionPlanPolicy(),
            targetProfile);

    private static SqlProviderCapabilityProfile Profile(
        SqlAgentToolType provider,
        int major,
        int minor) =>
        new(provider, ServerVersion: new Version(major, minor));
}
