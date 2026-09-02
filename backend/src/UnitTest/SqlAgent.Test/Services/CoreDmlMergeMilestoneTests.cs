using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreDmlMergeMilestoneTests
{
    private const string UpdateAndInsert =
        "MERGE INTO inventory AS t " +
        "USING (VALUES (1, 3)) AS s (id, quantity) " +
        "ON t.id = s.id " +
        "WHEN MATCHED THEN UPDATE SET quantity = s.quantity " +
        "WHEN NOT MATCHED THEN INSERT (id, quantity) VALUES (s.id, s.quantity);";

    public static TheoryData<string> PositiveGrammarCases => new()
    {
        "MERGE INTO inventory AS t USING (VALUES (1, 3)) AS s (id, quantity) ON t.id = s.id WHEN MATCHED THEN UPDATE SET quantity = s.quantity;",
        "MERGE INTO inventory AS t USING (VALUES (1, 3)) AS s (id, quantity) ON t.id = s.id WHEN MATCHED THEN DELETE;",
        "MERGE INTO inventory AS t USING (VALUES (1, 3)) AS s (id, quantity) ON t.id = s.id WHEN NOT MATCHED THEN INSERT (id, quantity) VALUES (s.id, s.quantity);",
        UpdateAndInsert,
        "MERGE INTO inventory AS t USING (VALUES (1, 2, 3)) AS s (tenant_id, id, quantity) ON t.tenant_id = s.tenant_id AND t.id = s.id WHEN MATCHED THEN UPDATE SET quantity = s.quantity WHEN NOT MATCHED THEN INSERT (tenant_id, id, quantity) VALUES (s.tenant_id, s.id, s.quantity);"
    };

    [Theory]
    [MemberData(nameof(PositiveGrammarCases))]
    public void Parse_SqlServerCanonicalMerge_ModelsClosedActions(string sql)
    {
        var parsed = CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.MsSqlServer);
        var merge = Assert.IsType<MergeStatement>(parsed.Statement);

        Assert.False(string.IsNullOrWhiteSpace(IdentifierText(merge.Target.Name)));
        Assert.False(merge.SourceColumns.IsDefaultOrEmpty);
        Assert.Equal(merge.SourceColumns.Length, merge.SourceValues.Length);
        Assert.True(merge.Matched is not null || merge.NotMatched is not null);
    }

    [Fact]
    public void Compile_SqlServerSingleRowMerge_WithPrimaryKeyAssurance_EmitsNativeParameterizedMerge()
    {
        var parsed = CoreSqlTextParser.ParseDml(UpdateAndInsert, SqlAgentToolType.MsSqlServer);
        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.MsSqlServer,
            new SqlPlanValidationContext("merge-v1"),
            conflictTargetAssurance: DmlConflictTargetAssurance.FromPrimaryKey(["id"]));

        Assert.Equal(SqlStatementKind.Merge, command.Kind);
        Assert.Contains("MERGE INTO", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("USING (VALUES (", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WHEN MATCHED THEN UPDATE SET", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WHEN NOT MATCHED THEN INSERT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(";", command.Sql, StringComparison.Ordinal);
        Assert.Equal(2, command.Parameters.Length);
        Assert.DoesNotContain("VALUES (1, 3)", command.Sql, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(command.PlanFingerprint));
    }

    [Fact]
    public void Compile_SqlServerMerge_WithoutTargetKeyAssurance_FailsClosed()
    {
        var parsed = CoreSqlTextParser.ParseDml(UpdateAndInsert, SqlAgentToolType.MsSqlServer);

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.MsSqlServer,
                new SqlPlanValidationContext("merge-v1")));

        Assert.Contains("metadata-backed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("key", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SqlServerMerge_MatchMustCoverCompleteAssuredKey()
    {
        var parsed = CoreSqlTextParser.ParseDml(UpdateAndInsert, SqlAgentToolType.MsSqlServer);

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.MsSqlServer,
                new SqlPlanValidationContext("merge-v1"),
                conflictTargetAssurance: DmlConflictTargetAssurance.FromPrimaryKey(["tenant_id", "id"])));

        Assert.Contains("complete", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("key", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SqlServerMerge_RichMatchedExpression_RemainsFailClosed()
    {
        const string sql =
            "MERGE INTO inventory AS t USING (VALUES (1, 3)) AS s (id, quantity) " +
            "ON t.id = s.id WHEN MATCHED THEN UPDATE SET quantity = s.quantity + 1;";

        var parsed = CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.MsSqlServer);
        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.MsSqlServer,
                new SqlPlanValidationContext("merge-v1"),
                conflictTargetAssurance: DmlConflictTargetAssurance.FromPrimaryKey(["id"])));

        Assert.Contains("direct columns", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fail-closed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SqlServerMerge_ToOracle_RemainsNativeOnly()
    {
        var parsed = CoreSqlTextParser.ParseDml(UpdateAndInsert, SqlAgentToolType.MsSqlServer);

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Oracle,
                new SqlPlanValidationContext("merge-v1"),
                conflictTargetAssurance: DmlConflictTargetAssurance.FromPrimaryKey(["id"])));

        Assert.Contains("MERGE", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SQL Server", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fail-closed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<string, string> NegativeGrammarCases => new()
    {
        {
            "MERGE INTO inventory AS t USING (VALUES (1, 3)) AS s (id, quantity) ON t.id = s.id WHEN MATCHED THEN DELETE",
            "semicolon"
        },
        {
            "MERGE INTO inventory AS t USING (VALUES (1, 3), (2, 4)) AS s (id, quantity) ON t.id = s.id WHEN MATCHED THEN DELETE;",
            "Expected"
        },
        {
            "MERGE INTO inventory AS t USING (VALUES (1, 3)) AS s (id, quantity) ON t.id = s.id;",
            "WHEN"
        },
        {
            "MERGE INTO inventory AS t USING (VALUES (1, 3)) AS s (id, quantity) ON t.id = s.id WHEN MATCHED THEN DELETE WHEN MATCHED THEN DELETE;",
            "at most one"
        }
    };

    [Theory]
    [MemberData(nameof(NegativeGrammarCases))]
    public void Parse_UnsupportedMergeGrammar_FailsClosed(string sql, string expected)
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.MsSqlServer));

        Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_MergeFromOracleSource_RemainsFailClosed()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseDml(UpdateAndInsert, SqlAgentToolType.Oracle));

        Assert.Contains("SQL Server", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static string IdentifierText(SqlIdentifier identifier) =>
        string.Join('.', identifier.Parts.Select(part => part.Value));
}
