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
            [new KeyValuePair<string, IReadOnlyList<string>>("public", ["orders"])],
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
    public async Task ResolveAsync_Strict_ResolvesUniqueUnqualifiedTable()
    {
        var metadata = new StubMetadataReader(
            [
                new KeyValuePair<string, IReadOnlyList<string>>("audit", ["events"]),
                new KeyValuePair<string, IReadOnlyList<string>>("public", ["users", "orders"])
            ],
            [new DatabaseColumnMetadata("public", "users", "id", "int", true, 1)]);
        var resolver = new DmlRowIdentityResolver(metadata);

        var result = await resolver.ResolveAsync(
            "connection",
            "users",
            DmlRowIdentityAssurance.Strict,
            TestContext.Current.CancellationToken);

        Assert.Equal(["id"], result);
        Assert.NotNull(metadata.LastColumnsRequest);
        Assert.Equal("public", metadata.LastColumnsRequest.Value.Schema);
        Assert.Equal("users", metadata.LastColumnsRequest.Value.Table);
    }

    [Fact]
    public async Task ResolveAsync_Strict_RejectsAmbiguousUnqualifiedTable()
    {
        var metadata = new StubMetadataReader(
            [
                new KeyValuePair<string, IReadOnlyList<string>>("archive", ["users"]),
                new KeyValuePair<string, IReadOnlyList<string>>("public", ["users"])
            ],
            []);
        var resolver = new DmlRowIdentityResolver(metadata);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(
            "connection",
            "users",
            DmlRowIdentityAssurance.Strict,
            TestContext.Current.CancellationToken));

        Assert.Contains("ambiguous", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_Strict_RejectsUnknownUnqualifiedTable()
    {
        var metadata = new StubMetadataReader(
            [new KeyValuePair<string, IReadOnlyList<string>>("public", ["orders"])],
            []);
        var resolver = new DmlRowIdentityResolver(metadata);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(
            "connection",
            "users",
            DmlRowIdentityAssurance.Strict,
            TestContext.Current.CancellationToken));

        Assert.Contains("could not be resolved", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_Strict_RejectsTableWithoutPrimaryKey()
    {
        var resolver = new DmlRowIdentityResolver(new StubMetadataReader(
            [new KeyValuePair<string, IReadOnlyList<string>>("public", ["events"])],
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
        var resolver = new DmlRowIdentityResolver(new StubMetadataReader(
            [new KeyValuePair<string, IReadOnlyList<string>>("public", ["events"])],
            []));

        var result = await resolver.ResolveAsync(
            "connection",
            "public.events",
            DmlRowIdentityAssurance.CountOnly,
            TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }

    [Fact]
    public async Task ResolveAsync_RejectsThreePartTarget()
    {
        var resolver = new DmlRowIdentityResolver(new StubMetadataReader([], []));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(
            "connection",
            "catalog.public.orders",
            DmlRowIdentityAssurance.Strict,
            TestContext.Current.CancellationToken));

        Assert.Contains("<table> or <schema>.<table>", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StubMetadataReader(
        IReadOnlyList<KeyValuePair<string, IReadOnlyList<string>>> tablesBySchema,
        IReadOnlyList<DatabaseColumnMetadata> columns) : IProviderMetadataReader
    {
        public (string Schema, string Table)? LastColumnsRequest { get; private set; }

        public Task<IReadOnlyList<string>> GetSchemasAsync(
            string connectionString,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(tablesBySchema.Select(x => x.Key).ToArray());

        public Task<IReadOnlyList<string>> GetTablesAsync(
            string connectionString,
            string schema,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                tablesBySchema.FirstOrDefault(x =>
                    string.Equals(x.Key, schema, StringComparison.OrdinalIgnoreCase)).Value
                ?? (IReadOnlyList<string>)[]);

        public Task<IReadOnlyList<DatabaseColumnMetadata>> GetColumnsAsync(
            string connectionString,
            string schema,
            string table,
            CancellationToken cancellationToken = default)
        {
            LastColumnsRequest = (schema, table);
            return Task.FromResult(columns);
        }

        public Task<IReadOnlyList<DatabaseUniqueKeyMetadata>> GetUniqueKeysAsync(
            string connectionString,
            string schema,
            string table,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DatabaseUniqueKeyMetadata>>([]);
    }
}
