using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreImplicitInsertColumnsCapabilityTests
{
    public static IEnumerable<object[]> NativeProviders()
    {
        yield return [SqlAgentToolType.Postgres];
        yield return [SqlAgentToolType.MySQL];
        yield return [SqlAgentToolType.Sqlite];
        yield return [SqlAgentToolType.MsSqlServer];
        yield return [SqlAgentToolType.Oracle];
        yield return [SqlAgentToolType.Firebird];
    }

    [Theory]
    [MemberData(nameof(NativeProviders))]
    public void Parse_ImplicitColumnValues_IsStructuredWithoutInventedColumns(
        SqlAgentToolType provider)
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "INSERT INTO users VALUES (1, 'Alice')",
            provider);
        var insert = Assert.IsType<InsertStatement>(parsed.Statement);

        Assert.Empty(insert.Columns);
        var values = Assert.IsType<InsertValuesSource>(insert.Source);
        Assert.Single(values.Rows);
        Assert.Equal(2, values.Rows[0].Length);
    }

    [Theory]
    [MemberData(nameof(NativeProviders))]
    public void Compile_ImplicitColumnValues_SameProviderPreservesNativeShape(
        SqlAgentToolType provider)
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "INSERT INTO users VALUES (1, 'Alice')",
            provider);

        var command = Compile(parsed, provider);

        Assert.Contains("INSERT INTO", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("VALUES", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("users (", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_ImplicitColumnValuesAcrossProviders_FailsInsteadOfGuessingColumnOrder()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "INSERT INTO users VALUES (1, 'Alice')",
            SqlAgentToolType.Postgres);

        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile(parsed, SqlAgentToolType.MySQL));

        Assert.Contains("dml.insert_implicit_columns", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("native-only", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Postgres", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MySQL", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_ImplicitColumnMultiRowValues_RequiresUniformWidth()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseDml(
                "INSERT INTO users VALUES (1, 'Alice'), (2)",
                SqlAgentToolType.Postgres));

        Assert.Contains("same width", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(NativeProviders))]
    public void Compile_ImplicitColumnInsertSelect_SameProviderPreservesNativeShape(
        SqlAgentToolType provider)
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "INSERT INTO users SELECT id, name FROM staged_users",
            provider);
        var insert = Assert.IsType<InsertStatement>(parsed.Statement);

        Assert.Empty(insert.Columns);
        Assert.IsType<InsertQuerySource>(insert.Source);

        var command = Compile(parsed, provider);
        Assert.Contains("INSERT INTO", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SELECT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("users (", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_ImplicitColumnInsertSelectAcrossProviders_FailsInsteadOfGuessingColumnOrder()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "INSERT INTO users SELECT id, name FROM staged_users",
            SqlAgentToolType.Postgres);

        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile(parsed, SqlAgentToolType.MySQL));

        Assert.Contains("dml.insert_implicit_columns", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("native-only", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_ImplicitColumnInsertSelectWildcard_SameProviderRemainsNative()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "INSERT INTO users SELECT * FROM staged_users",
            SqlAgentToolType.Postgres);

        var command = Compile(parsed, SqlAgentToolType.Postgres);

        Assert.Contains("SELECT *", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_ImplicitColumnsWithConflictHandling_RemainsFailClosed()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "INSERT INTO users VALUES (1, 'Alice') ON CONFLICT DO NOTHING",
            SqlAgentToolType.Postgres);

        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile(parsed, SqlAgentToolType.Postgres));

        Assert.Contains("conflict handling", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("explicit target columns", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Matrix_ImplicitColumnInsert_IsDeclaredNativeOnlyForEveryProvider()
    {
        foreach (var provider in new[]
        {
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MySQL,
            SqlAgentToolType.Sqlite,
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.Oracle,
            SqlAgentToolType.Firebird
        })
        {
            var capability = Assert.Single(
                SqlCapabilityMatrix.ForProvider(provider).Capabilities,
                item => item.Id == "dml.insert_implicit_columns");
            Assert.Equal(SqlCapabilityStatus.Supported, capability.Status);
            Assert.Contains("same-provider", capability.Detail, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static CompiledSqlCommand Compile(
        ParsedStatement parsed,
        SqlAgentToolType targetProvider) =>
        CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            targetProvider,
            new SqlPlanValidationContext("implicit-insert-columns-v1"));
}
