using Admin.Service.Data;
using Admin.Service.Data.Entites;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Admin.Service.Services;
using Common.Interfaces;
using Common.Services;
using HsSqlAgent.Approvals;
using HsSqlAgent.Provider.Abstractions;
using HsSqlAgent.Provider.Sqlite;
using HsSqlAgent.Server.Services;
using HsSqlAgent.Server.Tools;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using SqlAgent.Service.Factories;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

public sealed partial class DurableDmlApprovalLifecycleTests
{
    private static async Task<DmlApprovalRequest> PersistPendingInsertAsync(
        Fixture fixture, TypedDmlRuntime? runtimeOverride = null)
    {
        const string sql = "INSERT INTO items (id, name) VALUES (1, 'Alice')";
        var runtime = runtimeOverride ?? new TypedDmlRuntime();
        var policyState = CreatePolicyState();
        var policy = policyState.GetCurrent();
        var approvalContext = new DmlApprovalExecutionContext(
            $"mcp-key:{AccessKeyId}",
            $"db-management:{DbManagementId}",
            SqlAgentToolType.Sqlite,
            fixture.TargetDatabasePath);

        var parsed = await runtime.ParseDmlBatchWithVerifiedRuntimeProfileAsync(
            fixture.SqlProvider,
            fixture.TargetConnectionString,
            sql,
            SqlAgentToolType.Sqlite,
            TestContext.Current.CancellationToken);
        var session = await runtime.PreviewTransactionAsync(
            fixture.SqlProvider,
            fixture.TargetConnectionString,
            parsed,
            policy,
            allowedTables: null,
            approvalContext,
            TestContext.Current.CancellationToken);
        var request = DmlApprovalRequestFactory.Create("Insert test row", approvalContext, session);

        await using var scope = fixture.Services.CreateAsyncScope();
        var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider, runtime, policyState);
        await lifecycle.PersistPendingAsync(
            request,
            DmlApprovalResult.Pending(request, "EXT-1001"),
            DmlApprovalRequestFactory.ComputeEvidenceFingerprint(session),
            new DmlApprovalResumeContext(
                sql,
                RequiredTool,
                AccessKeyId,
                DbManagementId,
                SqlAgentToolType.Sqlite),
            fixture.TargetConnectionString,
            TestContext.Current.CancellationToken);

        return request;
    }

    private static SecurityPolicyRuntimeState CreatePolicyState()
    {
        var state = new SecurityPolicyRuntimeState();
        state.SetCurrent(new SecurityPolicyModel
        {
            DmlMaxAffectedRows = 100,
            MaxConcurrentSql = 16,
            RequireWhereForUpdate = true,
            RequireWhereForDelete = true
        });
        return state;
    }

    /// <summary>
    /// Inject Admin Store persistence failures only after the target DB has
    /// finished committing. The first Executing claim must always persist.
    /// </summary>
    private sealed class TerminalApprovalSaveFailureInterceptor(
        bool failUnknownSave,
        bool failTerminalSaveAfterSuccess)
        : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var statuses = eventData.Context?.ChangeTracker
                .Entries<DmlApprovalRequestState>()
                .Where(x => x.State == EntityState.Modified)
                .Select(x => x.Entity.Status)
                .ToArray() ?? [];

            if ((!failTerminalSaveAfterSuccess
                    && statuses.Contains("Executed", StringComparer.Ordinal))
                || (failUnknownSave && statuses.Contains("ApprovalStateUnknown", StringComparer.Ordinal)))
                throw new IOException("Injected Admin Store terminal SaveChanges failure.");

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (failTerminalSaveAfterSuccess
                && eventData.Context?.ChangeTracker
                    .Entries<DmlApprovalRequestState>()
                    .Any(x => x.Entity.Status == "Executed") == true)
            {
                // EF has persisted the status; simulate losing its reply.
                throw new IOException("Injected lost Admin Store SaveChanges acknowledgement.");
            }

            return base.SavedChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            string adminDatabasePath,
            string targetDatabasePath,
            ServiceProvider services,
            SqliteProvider sqlProvider,
            Mock<ISqlProviderFactory> providerFactory,
            Mock<ISqlConnectionStringFactory> connectionStringFactory)
        {
            AdminDatabasePath = adminDatabasePath;
            TargetDatabasePath = targetDatabasePath;
            Services = services;
            SqlProvider = sqlProvider;
            ProviderFactory = providerFactory;
            ConnectionStringFactory = connectionStringFactory;
            TargetConnectionString = sqlProvider.BuildConnectionString(new BuildDbConnectionModelBase
            {
                Database = targetDatabasePath
            });
        }

        public string AdminDatabasePath { get; }
        public string TargetDatabasePath { get; }
        public string TargetConnectionString { get; }
        public ServiceProvider Services { get; }
        public SqliteProvider SqlProvider { get; }
        private Mock<ISqlProviderFactory> ProviderFactory { get; }
        private Mock<ISqlConnectionStringFactory> ConnectionStringFactory { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var adminPath = Path.Combine(Path.GetTempPath(), $"hsqlagent-durable-admin-{Guid.NewGuid():N}.db");
            var targetPath = Path.Combine(Path.GetTempPath(), $"hsqlagent-durable-target-{Guid.NewGuid():N}.db");
            var sqlProvider = new SqliteProvider();
            var targetConnectionString = sqlProvider.BuildConnectionString(new BuildDbConnectionModelBase
            {
                Database = targetPath
            });

            var providerFactory = new Mock<ISqlProviderFactory>(MockBehavior.Strict);
            providerFactory.Setup(x => x.GetProvider(SqlAgentToolType.Sqlite)).Returns(sqlProvider);

            var connectionStringFactory = new Mock<ISqlConnectionStringFactory>(MockBehavior.Strict);
            connectionStringFactory
                .Setup(x => x.BuildConnectionString(
                    SqlAgentToolType.Sqlite,
                    It.IsAny<BuildDbConnectionModelBase>()))
                .Returns(targetConnectionString);

            var services = BuildServices(adminPath);

            await using (var scope = services.CreateAsyncScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<AdminContext>();
                await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
                context.DbManagement.Add(new DbManagement
                {
                    Id = DbManagementId,
                    Name = "durable-target",
                    SqlProvider = SqlAgentToolType.Sqlite.ToString(),
                    Database = targetPath,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
                context.McpAccessKeys.Add(new McpAccessKey
                {
                    Id = AccessKeyId,
                    Name = "durable-test-key",
                    KeyPrefix = "durable",
                    KeyHash = "test-key-hash",
                    IsActive = true,
                    AllowedTools = RequiredTool,
                    DbManagementId = DbManagementId,
                    CreatedAt = DateTime.UtcNow
                });
                await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            await using (var target = new SqliteConnection(targetConnectionString))
            {
                await target.OpenAsync(TestContext.Current.CancellationToken);
                await using var command = target.CreateCommand();
                command.CommandText = "CREATE TABLE items (id INTEGER PRIMARY KEY, name TEXT NOT NULL);";
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            return new Fixture(
                adminPath,
                targetPath,
                services,
                sqlProvider,
                providerFactory,
                connectionStringFactory);
        }

        public ServiceProvider CreateRestartedServices(
            bool failAudit = false,
            bool failTerminalSave = false,
            bool failUnknownSave = false,
            bool failTerminalSaveAfterSuccess = false) =>
            BuildServices(
                AdminDatabasePath, failAudit, failTerminalSave,
                failUnknownSave, failTerminalSaveAfterSuccess);

        private static ServiceProvider BuildServices(
            string adminDatabasePath,
            bool failAudit = false,
            bool failTerminalSave = false,
            bool failUnknownSave = false,
            bool failTerminalSaveAfterSuccess = false)
        {
            var adminConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = adminDatabasePath
            }.ToString();
            var serviceCollection = new ServiceCollection();
            var terminalFailure = failTerminalSave
                ? new TerminalApprovalSaveFailureInterceptor(
                    failUnknownSave, failTerminalSaveAfterSuccess)
                : null;
            serviceCollection.AddDbContext<AdminContext>(options =>
            {
                options.UseSqlite(adminConnectionString);
                if (terminalFailure is not null)
                    options.AddInterceptors(terminalFailure);
            });
            serviceCollection.AddScoped<IAdminContext>(sp => sp.GetRequiredService<AdminContext>());
            if (failAudit)
            {
                var audit = new Mock<IAuditService>(MockBehavior.Strict);
                audit.Setup(x => x.WriteEventAsync(
                        It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                        It.IsAny<AuditEventContext>(), It.IsAny<string>(),
                        It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new IOException("Audit database is unavailable after target COMMIT."));
                serviceCollection.AddSingleton(audit.Object);
            }
            serviceCollection.AddSingleton<ICryptoService, CryptoService>();
        serviceCollection.AddSingleton(Common.Services.TestSecrets.Create());
            serviceCollection.Configure<McpKeySettings>(options => options.HmacSecretKey = HmacSecret);
            return serviceCollection.BuildServiceProvider();
        }

        public DurableDmlWitnessVerificationService CreateWitnessInspection(
            IServiceProvider scopedServices, IDmlTargetCommitWitness? witness) =>
            new(
                scopedServices.GetRequiredService<AdminContext>(),
                scopedServices.GetRequiredService<ICryptoService>(),
                scopedServices.GetRequiredService<Common.Services.SecretProtectionSettings>(),
                ProviderFactory.Object,
                ConnectionStringFactory.Object,
                witness);

        public DurableDmlApprovalLifecycle CreateLifecycle(
            IServiceProvider scopedServices,
            TypedDmlRuntime runtime,
            SecurityPolicyRuntimeState? policyState = null)
        {
            var currentPolicyState = policyState ?? CreatePolicyState();
            return new DurableDmlApprovalLifecycle(
                scopedServices,
                runtime,
                currentPolicyState,
                new SqlExecutionConcurrencyLimiter(currentPolicyState),
                ProviderFactory.Object,
                ConnectionStringFactory.Object);
        }

        public async Task<long> CountTargetRowsAsync()
        {
            await using var connection = new SqliteConnection(TargetConnectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM items;";
            return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (File.Exists(AdminDatabasePath)) File.Delete(AdminDatabasePath);
            if (File.Exists(TargetDatabasePath)) File.Delete(TargetDatabasePath);
        }
    }
}
