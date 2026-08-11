using Microsoft.Extensions.Configuration;
using Moq;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
using SqlAgent.Service.Services;
using SqlAgent.Service.SqlParsing;
using SqlAgent.Service.Strategies;
using Xunit;

namespace SqlAgent.Test.Strategies;

public class SqlCapabilityCompilerTests
{
    private readonly IReadOnlyList<BaseSqlStrategy> _strategies;

    public SqlCapabilityCompilerTests()
    {
        var config = new Mock<IConfiguration>();
        config.Setup(x => x["McpKeySettings:HmacSecretKey"])
            .Returns("TestSecretKey12345678901234567890");
        var parser = new QueryValueParserService();
        _strategies =
        [
            new SqliteStrategy(parser, config.Object),
            new PostgresStrategy(parser, config.Object),
            new MySqlStrategy(parser, config.Object),
            new MsSqlServerStrategy(parser, config.Object),
            new OracleStrategy(parser, config.Object),
            new FirebirdStrategy(parser, config.Object)
        ];
    }

    [Fact]
    public void Cast_CompilesForAllProvidersWithoutDroppingType()
    {
        var definition = SqlDefinitionParser.ParseQuery(
            "SELECT CAST(amount AS DECIMAL(12,2)) AS normalized_amount FROM orders");

        foreach (var strategy in _strategies)
        {
            var sql = strategy.CompileQuerySql(definition);
            Assert.Contains("CAST(", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("DECIMAL(12,2)", sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void WindowFrame_CompilesForAllProvidersWithoutDroppingBounds()
    {
        var definition = SqlDefinitionParser.ParseQuery(
            "SELECT SUM(amount) OVER (ORDER BY id ROWS BETWEEN 2 PRECEDING AND CURRENT ROW) FROM orders");

        foreach (var strategy in _strategies)
        {
            var sql = strategy.CompileQuerySql(definition);
            Assert.Contains("ROWS BETWEEN 2 PRECEDING AND CURRENT ROW", sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void NullOrdering_CompilesOnlyForDeclaredProviders()
    {
        var definition = SqlDefinitionParser.ParseQuery(
            "SELECT amount FROM orders ORDER BY amount DESC NULLS LAST");

        foreach (var strategy in _strategies)
        {
            if (strategy.DbType is SqlAgentToolType.MySQL or SqlAgentToolType.MsSqlServer)
            {
                var error = Assert.Throws<InvalidOperationException>(() => strategy.CompileQuerySql(definition));
                Assert.Contains("NULLS FIRST/LAST", error.Message);
            }
            else
            {
                var sql = strategy.CompileQuerySql(definition);
                Assert.Contains("DESC NULLS LAST", sql, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void Interval_CompilesOnlyForPostgres()
    {
        var definition = SqlDefinitionParser.ParseQuery(
            "SELECT created_at + INTERVAL '1 day' FROM events");

        foreach (var strategy in _strategies)
        {
            if (strategy.DbType == SqlAgentToolType.Postgres)
            {
                var sql = strategy.CompileQuerySql(definition);
                Assert.Contains("INTERVAL '1 day'", sql, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                var error = Assert.Throws<InvalidOperationException>(() => strategy.CompileQuerySql(definition));
                Assert.Contains("INTERVAL expressions", error.Message);
            }
        }
    }

    [Fact]
    public void CapabilityMatrix_MatchesProviderSpecificCompilerBoundaries()
    {
        foreach (var strategy in _strategies)
        {
            var matrix = SqlCapabilityMatrix.ForProvider(strategy.DbType);
            Assert.Equal(SqlCapabilityMatrix.Version, matrix.MatrixVersion);
            Assert.Equal(matrix.Capabilities.Count, matrix.Capabilities.Select(x => x.Id).Distinct().Count());

            var interval = Assert.Single(matrix.Capabilities, x => x.Id == "expression.interval");
            Assert.Equal(
                strategy.DbType == SqlAgentToolType.Postgres ? SqlCapabilityStatus.Supported : SqlCapabilityStatus.Rejected,
                interval.Status);

            var nulls = Assert.Single(matrix.Capabilities, x => x.Id == "ordering.nulls");
            Assert.Equal(
                strategy.DbType is SqlAgentToolType.MySQL or SqlAgentToolType.MsSqlServer
                    ? SqlCapabilityStatus.Rejected
                    : SqlCapabilityStatus.Supported,
                nulls.Status);
        }
    }
}
