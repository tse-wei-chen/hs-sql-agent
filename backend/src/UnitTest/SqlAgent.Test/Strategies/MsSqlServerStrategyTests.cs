using System.Data;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.SqlParsing;
using SqlAgent.Service.Core.Execution;
using Microsoft.Data.SqlClient;
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

        var strategy = new MsSqlServerStrategy();
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
    protected override bool SupportsFormattedDateParsing => false;
    protected override MsSqlServerStrategy CreateStrategy() => new();

    protected override string TestTableName => "Users";
    protected override string TestOrdersTableName => "Orders";
    protected override string TestOrderDetailsTableName => "OrderDetails";
    protected override string TestOrderDetailsUnitPriceColumn => "UnitPrice";
    protected override string TestOrderDetailsQuantityColumn => "Quantity";
    protected override string TestOrderDetailsDiscountColumn => "Discount";
    protected override string TestSchemaName => "dbo";
    protected override string TestOrdersUserIdColumn => "UserId";
    protected override string TestOrdersIdColumn => "Id";
    protected override string TestOrderDateColumn => "OrderDate";

    protected override string TableNotFoundErrorCode => "208";
    protected override string ColumnNotFoundErrorCode => "207";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeSchemaFreshness_SqlServerTransactionHeldTableLockBlocksDdl(bool withRow)
    {
        // The strict native mode holds a table S lock until commit/rollback,
        // which conflicts with ALTER's Sch-M even when the table is empty.
        var token = TestContext.Current.CancellationToken;
        var table = "HSLOCK_" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var full = $"[dbo].[{table}]";
        await using var guarded = new SqlConnection(Fixture.ConnectionString);
        await using var ddl = new SqlConnection(Fixture.ConnectionString);
        await guarded.OpenAsync(token);
        await ddl.OpenAsync(token);

        async Task ExecuteAsync(SqlConnection connection, string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(token);
        }

        await ExecuteAsync(ddl, $"CREATE TABLE {full} (ID INT PRIMARY KEY, NAME NVARCHAR(50))");
        try
        {
            if (withRow)
                await ExecuteAsync(ddl, $"INSERT INTO {full} (ID, NAME) VALUES (1, 'Alice')");

            var parsed = CoreSqlTextParser.ParseDml(
                $"INSERT INTO {full} (ID, NAME) VALUES (2, 'Bob')", SqlAgentToolType.MsSqlServer);
            var native = new NativeDmlSchemaSnapshotService();
            var snapshot = await native.CaptureAsync(
                guarded, SqlAgentToolType.MsSqlServer, parsed, token);
            Assert.NotNull(snapshot);

            await using (var transaction = await guarded.BeginTransactionAsync(
                IsolationLevel.Serializable, token))
            {
                Assert.Equal(snapshot.Sha256, await native.ReadCurrentSchemaDigestAsync(
                    guarded, transaction, SqlAgentToolType.MsSqlServer, snapshot, token));

                await ExecuteAsync(ddl, "SET LOCK_TIMEOUT 1000");
                await using var blocked = ddl.CreateCommand();
                blocked.CommandText = $"ALTER TABLE {full} ADD FENCED INT";
                var error = await Assert.ThrowsAsync<SqlException>(() =>
                    blocked.ExecuteNonQueryAsync(token));
                Assert.Equal(1222, error.Number);
                await transaction.RollbackAsync(token);
            }

            await ExecuteAsync(ddl, $"ALTER TABLE {full} ADD FENCED INT");
            await using var changedTransaction = await guarded.BeginTransactionAsync(
                IsolationLevel.Serializable, token);
            var changed = await native.ReadCurrentSchemaDigestAsync(
                guarded, changedTransaction, SqlAgentToolType.MsSqlServer, snapshot, token);
            Assert.NotEqual(snapshot.Sha256, changed);
            await changedTransaction.RollbackAsync(token);
        }
        finally
        {
            await ExecuteAsync(ddl, $"DROP TABLE {full}");
        }
    }


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
        var ex = await Assert.ThrowsAsync<ProviderExecutionException>(() => Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                TableName = TestTableName,
                SelectColumns = [new OperationSelectCondition { Left = new FieldSelectCondition { FieldName = "Name" }, Operator = ArithmeticOperator.Subtract, Right = new ConstantSelectCondition { Constant = 1 } }]
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(SqlAgentToolType.MsSqlServer, ex.ProviderType);
        Assert.Equal("query", ex.Operation);
        Assert.Equal("245", ex.Code);
        Assert.Contains("conversion", ex.ProviderMessage, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public async Task ExecuteRawQueryAsync_CteSqlServerNativeSyntax_Executes()
    {
        var json = await Strategy.ExecuteRawQueryAsync(
            "WITH recent AS (" +
            "SELECT TOP 2 [Id] AS item_id FROM [Users]" +
            ") SELECT item_id FROM recent ORDER BY item_id",
            SqlAgentToolType.MsSqlServer,
            Fixture.ConnectionString,
            TestContext.Current.CancellationToken);

        Assert.NotEqual("[]", json);
    }

}
