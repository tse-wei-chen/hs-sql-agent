using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class SqlCoreFSharpFirebirdLiteralInteropTests
{
    [Fact]
    public void Facade_TextQuery_FirebirdString8191_MatchesLegacy()
    {
        var value = new string('x', 8191);
        var sql = $"SELECT '{value}'";
        var validation = new SqlPlanValidationContext("fsharp-firebird-string-8191-v1");
        var policy = new SqlExecutionPlanPolicy();
        var parsed = CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Postgres);

        var legacy = CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Firebird,
            validation,
            policy);
        var migrated = SqlCoreFacade.CompileQuery(
            sql,
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Firebird,
            validation,
            policy);

        Assert.Equal(legacy.Sql, migrated.Sql);
        Assert.Equal(legacy.Parameters.ToArray(), migrated.Parameters.ToArray());
        Assert.Equal(legacy.PlanFingerprint, migrated.PlanFingerprint);
        Assert.Contains("VARCHAR(8191)", migrated.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Facade_TextQuery_FirebirdString8192_RemainsFailClosedLikeLegacy()
    {
        var value = new string('x', 8192);
        var sql = $"SELECT '{value}'";
        var validation = new SqlPlanValidationContext("fsharp-firebird-string-8192-v1");
        var policy = new SqlExecutionPlanPolicy();
        var parsed = CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Postgres);

        var legacy = Assert.ThrowsAny<Exception>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Firebird,
                validation,
                policy));
        var migrated = Assert.ThrowsAny<Exception>(() =>
            SqlCoreFacade.CompileQuery(
                sql,
                SqlAgentToolType.Postgres,
                SqlAgentToolType.Firebird,
                validation,
                policy));

        Assert.Equal(legacy.GetType(), migrated.GetType());
        Assert.Equal(legacy.Message, migrated.Message);
    }

    [Fact]
    public void Facade_TextDml_FirebirdString8192_RemainsFailClosedLikeLegacy()
    {
        var value = new string('x', 8192);
        var sql = $"UPDATE users SET name = '{value}' WHERE id = 1";
        var validation = new SqlPlanValidationContext(
            "fsharp-firebird-string-dml-8192-v1",
            new HashSet<string>(new[] { "users" }, StringComparer.OrdinalIgnoreCase));
        var parsed = CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.Postgres);

        var legacy = Assert.ThrowsAny<Exception>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Firebird,
                validation));
        var migrated = Assert.ThrowsAny<Exception>(() =>
            SqlCoreFacade.CompileDml(
                sql,
                SqlAgentToolType.Postgres,
                SqlAgentToolType.Firebird,
                validation));

        Assert.Equal(legacy.GetType(), migrated.GetType());
        Assert.Equal(legacy.Message, migrated.Message);
    }
}
