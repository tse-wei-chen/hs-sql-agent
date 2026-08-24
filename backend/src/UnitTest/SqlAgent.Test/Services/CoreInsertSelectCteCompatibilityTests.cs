using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;
using SqlAgent.Service.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public class CoreInsertSelectCteCompatibilityTests
{
    [Fact]
    public void Compile_InsertSelectWithRootCte_FailsBeforeSqlKataCanDropDefinition()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "INSERT INTO archived (id) " +
            "WITH active AS (SELECT id FROM users) SELECT id FROM active"));

        Assert.Contains("dml.insert_select.cte_scope", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_InsertSelectWithDerivedLocalCte_FailsBeforeSqlKataCanDropDefinition()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "INSERT INTO archived (id) " +
            "SELECT d.id FROM (WITH active AS (SELECT id FROM users) SELECT id FROM active) AS d"));

        Assert.Contains("select.cte_scope", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("derived-table-local", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_InsertSelectWithoutCte_RemainsSupported()
    {
        var command = Compile(
            "INSERT INTO archived (id) SELECT id FROM users");

        Assert.Equal(SqlStatementKind.Insert, command.Kind);
        Assert.StartsWith("INSERT INTO", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    private static CompiledSqlCommand Compile(string sql) =>
        CoreDmlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.Postgres),
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("policy-v1"));
}
