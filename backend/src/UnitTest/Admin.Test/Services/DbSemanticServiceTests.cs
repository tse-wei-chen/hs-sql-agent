using Admin.Service.Data;
using Admin.Service.Data.Entites;
using Admin.Service.Models;
using Admin.Service.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Admin.Test.Services;

public class DbSemanticServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private AdminContext _context = null!;
    private DbSemanticService _service = null!;

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync();
        _context = new AdminContext(new DbContextOptionsBuilder<AdminContext>()
            .UseSqlite(_connection)
            .Options);
        await _context.Database.EnsureCreatedAsync();
        _context.DbManagement.Add(new DbManagement
        {
            Id = 1, Name = "analytics", SqlProvider = "Sqlite",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();
        _service = new DbSemanticService(_context);
    }

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task SemanticModel_ContainsNormalizedSynonymsRelationshipAndNonExecutableMetric()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _service.UpsertSemanticAsync(new DbSemanticRequest
        {
            DbManagementId = 1, SchemaName = "main", TableName = "orders",
            DisplayName = "Orders", Synonyms = ["sales", " Sales ", "purchases"]
        }, cancellationToken);
        await _service.UpsertRelationshipAsync(new DbSemanticRelationshipModel
        {
            DbManagementId = 1, Name = "orders_customer",
            SourceSchema = "main", SourceTable = "orders", SourceColumn = "customer_id",
            TargetSchema = "main", TargetTable = "customers", TargetColumn = "id",
            Cardinality = "many-to-one", Direction = "source-to-target"
        }, cancellationToken);
        await _service.UpsertMetricAsync(new DbSemanticMetricModel
        {
            DbManagementId = 1, SchemaName = "main", TableName = "orders",
            Name = "gross_revenue", Formula = "orders.amount",
            Aggregation = "sum", Grain = "order", Synonyms = ["revenue", "sales amount"]
        }, cancellationToken);

        var model = await _service.GetSemanticModelAsync(1, cancellationToken);

        Assert.Equal(["sales", "purchases"], Assert.Single(model.Entities).Synonyms);
        Assert.Equal("many-to-one", Assert.Single(model.Relationships).Cardinality);
        var metric = Assert.Single(model.Metrics);
        Assert.False(metric.Executable);
        Assert.Equal(["revenue", "sales amount"], metric.Synonyms);
    }

    [Theory]
    [InlineData("invalid", "source-to-target")]
    [InlineData("many-to-one", "sideways")]
    public async Task Relationship_RejectsUnknownGovernanceEnums(string cardinality, string direction)
    {
        var model = new DbSemanticRelationshipModel
        {
            DbManagementId = 1, Name = "invalid_relationship",
            SourceTable = "orders", SourceColumn = "customer_id",
            TargetTable = "customers", TargetColumn = "id",
            Cardinality = cardinality, Direction = direction
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.UpsertRelationshipAsync(model, TestContext.Current.CancellationToken));
    }
}
