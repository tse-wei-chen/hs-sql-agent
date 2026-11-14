using Admin.Service.Data;
using Admin.Service.Data.Entites;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Admin.Service.Services;
using HsSqlAgent.Approvals;
using HsSqlAgent.Provider.Abstractions;
using HsSqlAgent.Server.Services;
using HsSqlAgent.Server.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using SqlAgent.Service.Interfaces;

namespace HsSqlAgent.Server.Test.Tools;

/// <summary>
/// Identical test-only MCP/SQLite Audit fixtures shared by the five physical
/// Custom DML load suites. Each provider retains its own native target DDL,
/// identity, version-specific readiness, connection pooling and teardown.
/// No database connection or approval state is shared between test cases.
/// </summary>
internal static class PhysicalLoadMcpFixtureSupport
{
    internal static async Task<AdminContext> CreateAuditContextAsync(
        string auditPath, CancellationToken cancellationToken)
    {
        var context = new AdminContext(
            new DbContextOptionsBuilder<AdminContext>()
                .UseSqlite(new SqliteConnectionStringBuilder
                {
                    DataSource = auditPath
                }.ToString()).Options);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        return context;
    }

    internal static IHttpContextAccessor CreateAccessor(
        string providerName,
        string connectionString,
        string? databaseName,
        string allowedTable,
        string traceIdentifier)
    {
        var context = new DefaultHttpContext { TraceIdentifier = traceIdentifier };
        context.Items[Common.Models.McpContextItemKeys.SqlProvider] = providerName;
        context.Items[Common.Models.McpContextItemKeys.SqlConnectionString] = connectionString;
        context.Items[Common.Models.McpContextItemKeys.AccessKeyId] = 7;
        context.Items[Common.Models.McpContextItemKeys.DbManagementId] = 42;
        context.Items[Common.Models.McpContextItemKeys.DatabaseName] = databaseName;
        context.Items[Common.Models.McpContextItemKeys.AllowedTools] = "archive_order";
        context.Items[Common.Models.McpContextItemKeys.TableWhitelist] = allowedTable;

        var accessor = new Mock<IHttpContextAccessor>();
        accessor.SetupGet(x => x.HttpContext).Returns(context);
        return accessor.Object;
    }

    internal static CustomToolProxy CreateProxy(
        string sql,
        SqlAgentToolType providerType,
        ISqlProvider targetProvider,
        IHttpContextAccessor accessor,
        AdminContext auditContext,
        IDmlApprovalProvider approvalProvider,
        IAuditService? auditService,
        ISqlExecutionConcurrencyLimiter? concurrencyLimiter)
    {
        var tools = new Mock<ICustomSqlToolService>();
        tools.Setup(x => x.GetPublishedToolByNameAsync(
                "archive_order", 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CustomSqlTool
            {
                Name = "archive_order",
                Type = "DML",
                SqlTemplate = sql
            });

        var factory = new Mock<ISqlProviderFactory>();
        factory.Setup(x => x.GetProvider(providerType)).Returns(targetProvider);
        var state = new SecurityPolicyRuntimeState();
        state.SetCurrent(new SecurityPolicyModel
        {
            DmlMaxAffectedRows = 16,
            MaxConcurrentSql = 1,
            RequireWhereForUpdate = true,
            RequireWhereForDelete = true
        });

        return new CustomToolProxy(
            "archive_order", tools.Object, accessor, factory.Object,
            auditService ?? new AuditService(auditContext, accessor),
            Mock.Of<IQueryValueParserService>(),
            state,
            concurrencyLimiter ?? new SqlExecutionConcurrencyLimiter(state),
            dmlApprovalProvider: approvalProvider);
    }
}
