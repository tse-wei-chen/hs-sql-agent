using Admin.Service.Data;
using Admin.Service.Data.Entites;
using Admin.Service.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Admin.Test.Services;

public class CustomSqlToolLifecycleTests
{
    [Fact]
    public async Task DraftChanges_ShouldNotAffectPublishedSnapshot_AndDbScope()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<AdminContext>().UseSqlite(connection).Options;
        await using var context = new AdminContext(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var now = DateTime.UtcNow;
        context.DbManagement.AddRange(
            new DbManagement { Id = 1, Name = "one", CreatedAt = now, UpdatedAt = now },
            new DbManagement { Id = 2, Name = "two", CreatedAt = now, UpdatedAt = now });
        context.McpAccessKeys.AddRange(
            new McpAccessKey { Id = 1, Name = "all-on-one", KeyPrefix = "one", KeyHash = "hash-1", DbManagementId = 1, CreatedAt = now },
            new McpAccessKey { Id = 2, Name = "other-only", KeyPrefix = "two", KeyHash = "hash-2", DbManagementId = 1, AllowedTools = "other", CreatedAt = now },
            new McpAccessKey { Id = 3, Name = "v2-on-two", KeyPrefix = "three", KeyHash = "hash-3", DbManagementId = 2, AllowedTools = "find_user_v2", CreatedAt = now });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = new CustomSqlToolService(context, new ConfigurationBuilder().Build());

        var tool = await service.CreateToolAsync(new CustomSqlTool
        {
            Name = "find_user",
            Description = "Find a user",
            Type = "Query",
            SqlTemplate = "SELECT name FROM users WHERE id = {{id}}",
            ParametersJson = """[{"name":"id","type":"number"}]""",
            DbManagementId = 1
        });
        Assert.Equal(CustomSqlToolStatuses.Draft, tool.Status);
        await service.PublishAsync(tool.Id, "author-1", TestContext.Current.CancellationToken);

        var publishedOnOne = Assert.Single(await service.GetPublishedToolsForDbAsync(1, TestContext.Current.CancellationToken));
        Assert.Equal("SELECT name FROM users WHERE id = {{id}}", publishedOnOne.SqlTemplate);
        Assert.Empty(await service.GetPublishedToolsForDbAsync(2, TestContext.Current.CancellationToken));

        await service.UpdateToolAsync(new CustomSqlTool
        {
            Id = tool.Id,
            Name = "find_user_v2",
            Description = "Draft v2",
            Type = "Query",
            SqlTemplate = "SELECT email FROM users WHERE id = {{id}}",
            ParametersJson = """[{"name":"id","type":"number"}]""",
            DbManagementId = 2
        });

        var impact = await service.GetImpactAsync(tool.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(impact);
        Assert.Equal("all-on-one", Assert.Single(impact.CurrentlyExposedToKeys).Name);
        Assert.Equal("v2-on-two", Assert.Single(impact.WouldExposeToKeys).Name);
        Assert.Contains("Bound database changed.", impact.BreakingChanges);
        Assert.Contains("MCP tool name changed.", impact.BreakingChanges);
        Assert.True(impact.SqlChanged);

        publishedOnOne = Assert.Single(await service.GetPublishedToolsForDbAsync(1, TestContext.Current.CancellationToken));
        Assert.Equal("find_user", publishedOnOne.Name);
        Assert.Equal("SELECT name FROM users WHERE id = {{id}}", publishedOnOne.SqlTemplate);
        Assert.Empty(await service.GetPublishedToolsForDbAsync(2, TestContext.Current.CancellationToken));

        var conflictingDraft = await service.CreateToolAsync(new CustomSqlTool
        {
            Name = "find_user",
            Description = "Conflicting published identity",
            Type = "Query",
            SqlTemplate = "SELECT id FROM users",
            DbManagementId = 1
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PublishAsync(
            conflictingDraft.Id,
            "author-2",
            TestContext.Current.CancellationToken));

        await service.PublishAsync(tool.Id, "author-2", TestContext.Current.CancellationToken);
        Assert.Empty(await service.GetPublishedToolsForDbAsync(1, TestContext.Current.CancellationToken));
        Assert.Equal("find_user_v2", Assert.Single(await service.GetPublishedToolsForDbAsync(2, TestContext.Current.CancellationToken)).Name);
        var history = await service.GetRevisionsAsync(tool.Id, TestContext.Current.CancellationToken);
        Assert.Equal(2, history.Count);

        await service.DisableAsync(tool.Id, TestContext.Current.CancellationToken);
        Assert.Empty(await service.GetPublishedToolsForDbAsync(2, TestContext.Current.CancellationToken));

        var firstRevision = history.Single(x => x.RevisionNumber == 1);
        await service.RollbackAsync(tool.Id, firstRevision.Id, "author-3", TestContext.Current.CancellationToken);
        var rolledBack = Assert.Single(await service.GetPublishedToolsForDbAsync(1, TestContext.Current.CancellationToken));
        Assert.Equal("find_user", rolledBack.Name);
        Assert.Equal(3, (await service.GetRevisionsAsync(tool.Id, TestContext.Current.CancellationToken)).Count);

        Assert.True(await service.DeleteToolAsync(tool.Id));
        Assert.Empty(await service.GetRevisionsAsync(tool.Id, TestContext.Current.CancellationToken));
    }
}
