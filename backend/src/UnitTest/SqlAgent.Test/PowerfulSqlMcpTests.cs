using System.Collections.Generic;
using Moq;
using SqlAgent.Service.Interfaces;
using SqlAgent.Service.Models;
using SqlAgent.Service.Strategies;
using SqlKata;
using SqlKata.Compilers;
using Xunit;
using System.Data.Common;
using SqlAgent.Service.Enums;

namespace SqlAgent.Test.Strategies;

public class TestSqlStrategy : BaseSqlStrategy
{
    public TestSqlStrategy(IQueryValueParserService valueParser) : base(valueParser) { }

    public override SqlAgentToolType DbType => SqlAgentToolType.Postgres;

    protected override DbConnection CreateConnection(string? connectionString) => new Mock<DbConnection>().Object;

    protected override Compiler CreateCompiler() => new SqlServerCompiler();

    public Compiler GetCompiler() => CreateCompiler();

    // Public wrapper for testing protected methods if needed, or just test through ExecuteQueryAsync
    public Query BuildQuery(QueryDefinition definition) => typeof(BaseSqlStrategy)
        .GetMethod("BuildQueryFromDefinition", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
        .Invoke(this, new object[] { definition }) as Query;

    public override Task<List<string>> GetSchemasAsync(string connectionString, CancellationToken cancellationToken = default) => throw new System.NotImplementedException();
    public override Task<List<string>> GetTablesAsync(string connectionString, string schemaName, CancellationToken cancellationToken = default) => throw new System.NotImplementedException();
    public override Task<List<string>> GetColumnsAsync(string connectionString, string tableName, CancellationToken cancellationToken = default) => throw new System.NotImplementedException();
    public override Task<string> GetTableReferenceAsync(string connectionString, string tableName, CancellationToken cancellationToken = default) => throw new System.NotImplementedException();
}

public class PowerfulSqlMcpTests
{
    private readonly Mock<IQueryValueParserService> _parserMock;
    private readonly TestSqlStrategy _strategy;

    public PowerfulSqlMcpTests()
    {
        _parserMock = new Mock<IQueryValueParserService>();
        _strategy = new TestSqlStrategy(_parserMock.Object);
    }

    [Fact]
    public void BuildQuery_ShouldHandleNestedConditions()
    {
        // Arrange
        var definition = new QueryDefinition
        {
            TableName = "Users",
            WhereColumnsAndValues = new List<WhereCondition>
            {
                new WhereCondition
                {
                    IsOr = false,
                    Groups = new List<WhereCondition>
                    {
                        new WhereCondition { Field = "Age", Operator = ">", Value = 18 },
                        new WhereCondition { Field = "Status", Operator = "=", Value = "Active", IsOr = true }
                    }
                },
                new WhereCondition { Field = "Country", Operator = "=", Value = "Taiwan" }
            }
        };

        // Act
        var query = _strategy.BuildQuery(definition);
        var sql = _strategy.GetCompiler().Compile(query).Sql;

        // Assert
        Assert.Contains("WHERE", sql);
        Assert.Contains("Age", sql);
        Assert.Contains("Status", sql);
        Assert.Contains("Country", sql);
    }

    [Fact]
    public void BuildQuery_ShouldHandleSubQuerySource()
    {
        // Arrange
        var subQuery = new QueryDefinition
        {
            TableName = "Orders",
            SelectColumns = new List<SelectCondition> { new SelectCondition { Field = "UserId" } },
            WhereColumnsAndValues = new List<WhereCondition> { new WhereCondition { Field = "Amount", Operator = ">", Value = 100 } }
        };

        var definition = new QueryDefinition
        {
            FromQuery = subQuery,
            Alias = "LargeOrders",
            SelectColumns = new List<SelectCondition> { new SelectCondition { Field = "*" } }
        };

        // Act
        var query = _strategy.BuildQuery(definition);
        var sql = _strategy.GetCompiler().Compile(query).Sql;

        // Assert
        Assert.Contains("FROM", sql);
        Assert.Contains("Orders", sql);
        Assert.Contains("LargeOrders", sql);
    }

    [Fact]
    public void BuildQuery_ShouldHandleCaseWhen()
    {
        // Arrange
        var definition = new QueryDefinition
        {
            TableName = "Products",
            SelectColumns = new List<SelectCondition>
            {
                new SelectCondition { Field = "Name" },
                new SelectCondition
                {
                    Alias = "PriceCategory",
                    CaseWhen = new List<SqlAgent.Service.Models.CaseWhenClause>
                    {
                        new SqlAgent.Service.Models.CaseWhenClause
                        {
                            Condition = new WhereCondition { Field = "Price", Operator = ">", Value = 1000 },
                            Value = "Expensive"
                        },
                        new SqlAgent.Service.Models.CaseWhenClause
                        {
                            Condition = new WhereCondition { Field = "Price", Operator = ">", Value = 500 },
                            Value = "Medium"
                        }
                    },
                    ElseValue = "Cheap"
                }
            }
        };

        // Act
        var query = _strategy.BuildQuery(definition);
        var sql = _strategy.GetCompiler().Compile(query).Sql;

        // Assert
        Assert.Contains("CASE", sql);
        Assert.Contains("WHEN", sql);
        Assert.Contains("Price", sql);
        Assert.Contains("THEN", sql);
    }

    [Fact]
    public void BuildQuery_ShouldHandleComplexDateConditions()
    {
        // Arrange
        IEnumerable<object> inValues = new List<object> { "2023-01-01", "2023-01-02" };
        object? lowValue = "2023-01-01";
        object? highValue = "2023-12-31";

        _parserMock.Setup(x => x.TryGetInValues(It.IsAny<object>(), out inValues)).Returns(true);
        _parserMock.Setup(x => x.TryGetRangeValues(It.IsAny<object>(), out lowValue, out highValue)).Returns(true);

        var definition = new QueryDefinition
        {
            TableName = "Orders",
            WhereColumnsAndValues = new List<WhereCondition>
            {
                new WhereCondition { Field = "OrderDate", Operator = "in", Value = inValues, IsDate = true },
                new WhereCondition { Field = "ShipDate", Operator = "between", Value = "range", IsDate = true, IsOr = true }
            },
            HavingConditions = new List<HavingCondition>
            {
                new HavingCondition { Field = "DeliveryDate", Operator = "between", Value = "range", IsDate = true, Aggregation = "MIN" }
            }
        };

        // Act
        var query = _strategy.BuildQuery(definition);
        var sql = _strategy.GetCompiler().Compile(query).Sql;

        // Assert
        Assert.Contains("DATE([OrderDate]) IN (@p0, @p1)", sql);
        Assert.Contains("OR DATE([ShipDate]) BETWEEN @p2 AND @p3", sql);
        Assert.Contains("DATE(MIN([DeliveryDate])) BETWEEN @p4 AND @p5", sql);
    }
}
