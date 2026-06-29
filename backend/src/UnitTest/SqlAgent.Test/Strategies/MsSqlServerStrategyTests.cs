using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Moq;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Interfaces;
using SqlAgent.Service.Models;
using SqlAgent.Service.Services;
using SqlAgent.Service.Strategies;
using Testcontainers.MsSql;
using Xunit;

namespace SqlAgent.Test.Strategies;

public class MsSqlFixture : IDbFixture
{
    public MsSqlContainer Container { get; }
    public string ConnectionString => Container.GetConnectionString();

    public MsSqlFixture()
    {
        Container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04")
            .WithPassword("StrongPassword123!")
            .WithEnvironment("ACCEPT_EULA", "Y")
            .Build();
    }

    public async ValueTask InitializeAsync()
    {
        await Container.StartAsync();

        var strategy = new MsSqlServerStrategy(new QueryValueParserService(), new Mock<IConfiguration>().Object);
        using var conn = strategy.CreateConnection(ConnectionString);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE Users (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                Name NVARCHAR(100),
                Age INT,
                Active BIT,
                CreatedDate DATETIME
            );
            INSERT INTO Users (Name, Age, Active, CreatedDate) VALUES
            ('Alice', 30, 1, '2023-01-01'),
            ('Bob', 25, 1, '2023-02-01'),
            ('Charlie', 35, 0, '2023-03-01');

            CREATE TABLE Orders (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                UserId INT,
                Amount DECIMAL(10,2),
                OrderDate DATE
            );
            INSERT INTO Orders (UserId, Amount, OrderDate) VALUES
            (1, 150.0, '2023-01-10'),
            (1, 200.0, '2023-02-15'),
            (2, 50.0, '2023-03-20');

            CREATE TABLE OrderDetails (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                UnitPrice FLOAT,
                Quantity INT,
                Discount FLOAT
            );
            INSERT INTO OrderDetails (UnitPrice, Quantity, Discount) VALUES
            (10.123, 2, 0.1),
            (20.456, 1, 0.05);
        ";
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync() => await Container.DisposeAsync();
}


public class MsSqlServerStrategyTests(MsSqlFixture fixture) : BaseStrategyTests<MsSqlServerStrategy, MsSqlFixture>(fixture)
{
    protected override MsSqlServerStrategy CreateStrategy(IQueryValueParserService parser, IConfiguration configuration)
        => new(parser, configuration);

    protected override string TestTableName => "Users";
    protected override string TestOrdersTableName => "Orders";
    protected override string TestOrderDetailsTableName => "OrderDetails";
    protected override string TestOrderDetailsUnitPriceColumn => "UnitPrice";
    protected override string TestOrderDetailsQuantityColumn => "Quantity";
    protected override string TestOrderDetailsDiscountColumn => "Discount";
    protected override string TestSchemaName => "dbo";
    protected override string TestOrdersUserIdColumn => "UserId";

    protected override string TableNotFoundErrorCode => "208";
    protected override string ColumnNotFoundErrorCode => "207";

    [Fact]
    public override async Task GetColumnsAsync_ShouldReturnColumnTypes()
    {
        await base.GetColumnsAsync_ShouldReturnColumnTypes();
        var columns = await Strategy.GetColumnsAsync(Fixture.ConnectionString, TestSchemaName, TestTableName, TestContext.Current.CancellationToken);
        Assert.Contains(columns, c => c.Column == "Id" && c.Type.Contains("int", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(columns, c => c.Column == "Active" && c.Type.Contains("bit", StringComparison.OrdinalIgnoreCase));
    }

    protected override DmlDefinition CreateInsertDml() => new()
    {
        Operation = DmlOperation.Insert,
        TableName = TestTableName,
        Values = [
            new NameValuePair { FieldName = "Name", Value = "David" },
            new NameValuePair { FieldName = "Age", Value = 40 },
            new NameValuePair { FieldName = "Active", Value = true }
        ]
    };

    [Fact]
    public void BuildConnectionString_ShouldGenerateValidMsSqlFormat()
    {
        var model = new BuildDbConnectionModel
        {
            Provider = "SqlServer",
            Host = "localhost",
            Port = "1433",
            Database = "mydb",
            Username = "user",
            Password = "pw"
        };
        var connStr = Strategy.BuildConnectionString(model);

        var builder = new SqlConnectionStringBuilder(connStr);
        Assert.Equal("localhost,1433", builder.DataSource);
        Assert.Equal("mydb", builder.InitialCatalog);
        Assert.Equal("user", builder.UserID);
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldReturnDbError_WhenConversionIsInvalid()
    {
        var ex = await Assert.ThrowsAsync<Exception>(() => Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                TableName = TestTableName,
                SelectColumns = [new OperationSelectCondition { Left = new FieldSelectCondition { FieldName = "Name" }, Operator = ArithmeticOperator.Subtract, Right = new ConstantSelectCondition { Constant = 1 } }]
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("code=245", ex.Message);
        Assert.Contains("message=", ex.Message);
        Assert.Contains("conversion", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
