using SqlAgent.Service.Core.Execution;
using SqlAgent.Service.Core.Providers;
using Xunit;

namespace SqlAgent.Test.Services;

public class DmlRowIdentityResolverTests
{
    [Fact]
    public async Task ResolveAsync_Strict_OrdersCompositePrimaryKeyByOrdinal()
    {
        var metadata = new StubMetadataReader(
        [
            new DatabaseColumnMetadata("public", "orders", "line_id", "int", true, 2),
            new DatabaseColumnMetadata("public", "orders", "tenant_id", "int", true, 1),
            new DatabaseColumnMetadata("public", "orders", "status", "text", false)
        ]);
        var resolver = new DmlRowIdentityResolver(metadata);

        var result = await resolver.ResolveAsync(
            "connection",
            "public.orders",
            DmlRowIdentityAssurance.Strict,
            TestContext.Current.CancellationToken);

        Assert.Equal(["tenant_id", "line_id"], result);
    }

    [Fact]
    public async Task ResolveAsync_Strict_RejectsTableWithoutPrimaryKey()
    {
        var resolver = new DmlRowIdentityResolver(new StubMetadataReader(
        [
            new DatabaseColumnMetadata("public", "events", "created_at", "timestamp", false)
        ]));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(
            "connection",
            "public.events",
            DmlRowIdentityAssurance.Strict,
            TestContext.Current.CancellationToken));

        Assert.Contains("requires a primary key", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_CountOnly_AllowsMissingPrimaryKeyExplicitly()
    {
        var resolver = new DmlRowIdentityResolver(new StubMetadataReader([]));

        var result = await resolver.ResolveAsync(
            "connection",
            "public.events",
            DmlRowIdentityAssurance.CountOnly,
            TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }

    [Theory]
    [InlineData("orders")]
    [InlineData("catalog.public.orders")]
    public async Task ResolveAsync_RequiresUnambiguousSchemaQualifiedTarget(string tableName)
    {
        var resolver = new DmlRowIdentityResolver(new StubMetadataReader([]));

        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(
            "connection",
            tableName,
            DmlRowIdentityAssurance.Strict,
            TestContext.Current.CancellationToken));
    }

    private sealed class StubMetadataReader(IReadOnlyList<DatabaseColumnMetadata> columns)
        : IProviderMetadataReader
    {
        public Task<IReadOnlyList<string>> GetSchemasAsync(
            string connectionString,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<string>> GetTablesAsync(
            string connectionString,
            string schema,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<DatabaseColumnMetadata>> GetColumnsAsync(
            string connectionString,
            string schema,
            string table,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(columns);
    }
}
