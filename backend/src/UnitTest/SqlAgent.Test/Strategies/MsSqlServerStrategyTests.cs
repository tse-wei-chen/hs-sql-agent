using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Moq;
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
            ('Bob', 25, 1, '2023-02-01');
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
    protected override string TestSchemaName => "dbo";

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
        Operation = "insert",
        TableName = TestTableName,
        Values = [
            new NameValuePair { Name = "Name", Value = "David" },
            new NameValuePair { Name = "Age", Value = 40 },
            new NameValuePair { Name = "Active", Value = true }
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
    public async Task ExecuteQueryAsync_ShouldTrigger102Hint_WhenSyntaxIsInvalid()
    {
        var res = await Strategy.ExecuteQueryAsync(Fixture.ConnectionString, TestTableName,
            selectColumns: [new SelectCondition { Arithmetic = new SelectArithmeticCondition { FieldName = "Name", Operator = "!!!", Constant = 1 } }],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("code=102", res);
        Assert.Contains("Incorrect syntax near", res);
    }
}
