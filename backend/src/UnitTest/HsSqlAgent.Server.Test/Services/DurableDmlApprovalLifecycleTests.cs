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
using Moq;
using SqlAgent.Service.Factories;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

public sealed class DurableDmlApprovalLifecycleTests
{
    private const string HmacSecret = "durable-dml-test-hmac-secret-32-bytes-minimum";
    private const int AccessKeyId = 7;
    private const int DbManagementId = 11;
    private const string RequiredTool = "execute_dml_sql";

    [Fact]
    public async Task PendingApproval_NewServiceProviderAndRuntime_ExecutesExactlyOnce()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = await PersistPendingInsertAsync(fixture);

        Assert.Equal(0L, await fixture.CountTargetRowsAsync());

        await using (var restartedServices = fixture.CreateRestartedServices())
        await using (var scope = restartedServices.CreateAsyncScope())
        {
            var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider, new TypedDmlRuntime());
            var completion = await lifecycle.CompleteAsync(
                DmlApprovalCompletion.Approve(
                    request.RequestId,
                    request.ApprovalFingerprint,
                    approverIdentity: "reviewer@example.test",
                    externalReference: "EXT-1001"),
                TestContext.Current.CancellationToken);

            Assert.Equal(DmlApprovalCompletionStatus.Executed, completion.Status);
            Assert.Equal(1, completion.AffectedRows);
        }

        Assert.Equal(1L, await fixture.CountTargetRowsAsync());

        await using (var restartedServices = fixture.CreateRestartedServices())
        await using (var scope = restartedServices.CreateAsyncScope())
        {
            var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider, new TypedDmlRuntime());
            var duplicate = await lifecycle.CompleteAsync(
                DmlApprovalCompletion.Approve(
                    request.RequestId,
                    request.ApprovalFingerprint,
                    approverIdentity: "reviewer@example.test",
                    externalReference: "EXT-1001"),
                TestContext.Current.CancellationToken);

            Assert.Equal(DmlApprovalCompletionStatus.AlreadyCompleted, duplicate.Status);
        }

        Assert.Equal(1L, await fixture.CountTargetRowsAsync());
    }

    [Fact]
    public async Task AuditFailureAfterSuccessfulDatabaseCommit_DoesNotRewriteExecutedAsFailed()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = await PersistPendingInsertAsync(fixture);

        await using (var services = fixture.CreateRestartedServices(failAudit: true))
        await using (var scope = services.CreateAsyncScope())
        {
            var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider, new TypedDmlRuntime());
            var completion = await lifecycle.CompleteAsync(
                DmlApprovalCompletion.Approve(request.RequestId, request.ApprovalFingerprint),
                TestContext.Current.CancellationToken);

            Assert.Equal(DmlApprovalCompletionStatus.Executed, completion.Status);
            Assert.Equal(1, completion.AffectedRows);

            var context = scope.ServiceProvider.GetRequiredService<AdminContext>();
            var persisted = await context.DmlApprovalRequests.AsNoTracking().SingleAsync(
                x => x.RequestId == request.RequestId, TestContext.Current.CancellationToken);
            Assert.Equal("Executed", persisted.Status);
        }

        Assert.Equal(1L, await fixture.CountTargetRowsAsync());

        await using (var services = fixture.CreateRestartedServices())
        await using (var scope = services.CreateAsyncScope())
        {
            var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider, new TypedDmlRuntime());
            var replay = await lifecycle.CompleteAsync(
                DmlApprovalCompletion.Approve(request.RequestId, request.ApprovalFingerprint),
                TestContext.Current.CancellationToken);
            Assert.Equal(DmlApprovalCompletionStatus.AlreadyCompleted, replay.Status);
        }

        Assert.Equal(1L, await fixture.CountTargetRowsAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TargetCommittedButAdminStateSaveFails_NeverReturnsFailedOrReexecutes(
        bool fallbackStateSaveAlsoFails)
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = await PersistPendingInsertAsync(fixture);

        await using (var services = fixture.CreateRestartedServices(
            failTerminalSave: true,
            failUnknownSave: fallbackStateSaveAlsoFails))
        await using (var scope = services.CreateAsyncScope())
        {
            var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider, new TypedDmlRuntime());
            var result = await lifecycle.CompleteAsync(
                DmlApprovalCompletion.Approve(request.RequestId, request.ApprovalFingerprint),
                TestContext.Current.CancellationToken);

            Assert.Equal(DmlApprovalCompletionStatus.OutcomeUnknown, result.Status);
            Assert.Equal("dml.approval_state_unknown", result.ErrorCode);
            Assert.False(result.Retryable);
            Assert.Equal(1, result.AffectedRows);
            Assert.Contains("COMMIT succeeded", result.Message, StringComparison.Ordinal);
            Assert.Contains("do not retry", result.Message, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(1L, await fixture.CountTargetRowsAsync());

        await using (var restarted = fixture.CreateRestartedServices())
        await using (var scope = restarted.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AdminContext>();
            var state = await context.DmlApprovalRequests.AsNoTracking().SingleAsync(
                x => x.RequestId == request.RequestId, TestContext.Current.CancellationToken);
            Assert.Equal(
                fallbackStateSaveAlsoFails ? "Executing" : "ApprovalStateUnknown",
                state.Status);
            Assert.NotEqual("Failed", state.Status);

            var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider, new TypedDmlRuntime());
            var retry = await lifecycle.CompleteAsync(
                DmlApprovalCompletion.Approve(request.RequestId, request.ApprovalFingerprint),
                TestContext.Current.CancellationToken);

            Assert.Equal(
                fallbackStateSaveAlsoFails
                    ? DmlApprovalCompletionStatus.AlreadyProcessing
                    : DmlApprovalCompletionStatus.OutcomeUnknown,
                retry.Status);
            Assert.False(retry.Retryable);
            Assert.Contains("Do not retry", retry.Message, StringComparison.OrdinalIgnoreCase);
            if (!fallbackStateSaveAlsoFails)
                Assert.Equal("dml.approval_state_unknown", retry.ErrorCode);
        }

        // A fresh service instance must not execute the approval again.
        Assert.Equal(1L, await fixture.CountTargetRowsAsync());
    }

    [Fact]
    public async Task AdminTerminalSaveAcknowledgedByDatabaseButResponseLost_RemainsNonReplayable()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = await PersistPendingInsertAsync(fixture);

        await using (var services = fixture.CreateRestartedServices(
            failTerminalSave: true,
            failTerminalSaveAfterSuccess: true))
        await using (var scope = services.CreateAsyncScope())
        {
            var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider, new TypedDmlRuntime());
            var result = await lifecycle.CompleteAsync(
                DmlApprovalCompletion.Approve(request.RequestId, request.ApprovalFingerprint),
                TestContext.Current.CancellationToken);

            // A missing Admin Store acknowledgement does not change the known
            // outcome of the separate target SQL transaction.
            Assert.Equal(DmlApprovalCompletionStatus.OutcomeUnknown, result.Status);
            Assert.Equal("dml.approval_state_unknown", result.ErrorCode);
            Assert.False(result.Retryable);
            Assert.Contains("COMMIT succeeded", result.Message, StringComparison.Ordinal);
        }

        Assert.Equal(1L, await fixture.CountTargetRowsAsync());
        await using (var restarted = fixture.CreateRestartedServices())
        await using (var scope = restarted.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AdminContext>();
            var stored = await context.DmlApprovalRequests.AsNoTracking().SingleAsync(
                x => x.RequestId == request.RequestId, TestContext.Current.CancellationToken);
            // The first terminal write completed. The fallback might have
            // conservatively replaced it with OutcomeUnknown; neither permits
            // a second execution.
            Assert.Contains(stored.Status, new[] { "Executed", "ApprovalStateUnknown" });

            var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider, new TypedDmlRuntime());
            var repeat = await lifecycle.CompleteAsync(
                DmlApprovalCompletion.Approve(request.RequestId, request.ApprovalFingerprint),
                TestContext.Current.CancellationToken);
            Assert.Contains(repeat.Status, new[]
            {
                DmlApprovalCompletionStatus.AlreadyCompleted,
                DmlApprovalCompletionStatus.OutcomeUnknown
            });
            if (stored.Status == "ApprovalStateUnknown")
                Assert.Equal("dml.approval_state_unknown", repeat.ErrorCode);
        }

        Assert.Equal(1L, await fixture.CountTargetRowsAsync());
    }

    [Fact]
    public async Task LegacyApprovalStateUnknown_RemainsClassifiedWithoutReExecuting()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = await PersistPendingInsertAsync(fixture);

        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AdminContext>();
            var state = await context.DmlApprovalRequests.SingleAsync(
                x => x.RequestId == request.RequestId,
                TestContext.Current.CancellationToken);
            state.Status = "OutcomeUnknown";
            state.Reason =
                "Target database COMMIT succeeded, but the Admin Store completion status could not be confirmed. " +
                "The approval is already consumed. Reconcile database and approval state; do not retry this approval.";
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var restarted = fixture.CreateRestartedServices())
        await using (var scope = restarted.CreateAsyncScope())
        {
            var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider, new TypedDmlRuntime());
            var repeat = await lifecycle.CompleteAsync(
                DmlApprovalCompletion.Approve(request.RequestId, request.ApprovalFingerprint),
                TestContext.Current.CancellationToken);
            Assert.Equal(DmlApprovalCompletionStatus.OutcomeUnknown, repeat.Status);
            Assert.Equal("dml.approval_state_unknown", repeat.ErrorCode);
            Assert.False(repeat.Retryable);
        }

        Assert.Equal(0L, await fixture.CountTargetRowsAsync());
    }

    [Fact]
    public async Task PreviouslyUnknownCommitOutcome_RemainsTerminalWithoutReExecutingApproval()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = await PersistPendingInsertAsync(fixture);

        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AdminContext>();
            var state = await context.DmlApprovalRequests.SingleAsync(
                x => x.RequestId == request.RequestId,
                TestContext.Current.CancellationToken);
            state.Status = "OutcomeUnknown";
            state.Reason = "COMMIT acknowledgement lost; physical state requires reconciliation.";
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var restarted = fixture.CreateRestartedServices())
        await using (var scope = restarted.CreateAsyncScope())
        {
            var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider, new TypedDmlRuntime());
            var retry = await lifecycle.CompleteAsync(
                DmlApprovalCompletion.Approve(request.RequestId, request.ApprovalFingerprint),
                TestContext.Current.CancellationToken);
            Assert.Equal(DmlApprovalCompletionStatus.OutcomeUnknown, retry.Status);
            Assert.Equal("dml.commit_outcome_unknown", retry.ErrorCode);
            Assert.False(retry.Retryable);
            Assert.Contains("Do not retry", retry.Message, StringComparison.OrdinalIgnoreCase);

            var context = scope.ServiceProvider.GetRequiredService<AdminContext>();
            var state = await context.DmlApprovalRequests.AsNoTracking().SingleAsync(
                x => x.RequestId == request.RequestId,
                TestContext.Current.CancellationToken);
            Assert.Equal("OutcomeUnknown", state.Status);
        }

        Assert.Equal(0L, await fixture.CountTargetRowsAsync());
    }

    [Fact]
    public async Task PendingApproval_AccessKeyRevokedBeforeCompletion_BecomesStaleWithoutCommit()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = await PersistPendingInsertAsync(fixture);

        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AdminContext>();
            var key = await context.McpAccessKeys.SingleAsync(
                x => x.Id == AccessKeyId,
                TestContext.Current.CancellationToken);
            key.IsActive = false;
            key.RevokedAt = DateTime.UtcNow;
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var restartedServices = fixture.CreateRestartedServices())
        await using (var scope = restartedServices.CreateAsyncScope())
        {
            var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider, new TypedDmlRuntime());
            var completion = await lifecycle.CompleteAsync(
                DmlApprovalCompletion.Approve(
                    request.RequestId,
                    request.ApprovalFingerprint,
                    approverIdentity: "reviewer@example.test"),
                TestContext.Current.CancellationToken);

            Assert.Equal(DmlApprovalCompletionStatus.Stale, completion.Status);
            Assert.Contains("access key", completion.Message, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(0L, await fixture.CountTargetRowsAsync());
    }

    [Fact]
    public async Task PendingApproval_WrongApprovalFingerprint_IsRejectedWithoutClaimOrCommit()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = await PersistPendingInsertAsync(fixture);

        await using (var restartedServices = fixture.CreateRestartedServices())
        await using (var scope = restartedServices.CreateAsyncScope())
        {
            var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider, new TypedDmlRuntime());
            var completion = await lifecycle.CompleteAsync(
                DmlApprovalCompletion.Approve(request.RequestId, new string('0', 64)),
                TestContext.Current.CancellationToken);

            Assert.Equal(DmlApprovalCompletionStatus.InvalidApproval, completion.Status);

            var context = scope.ServiceProvider.GetRequiredService<AdminContext>();
            var state = await context.DmlApprovalRequests.AsNoTracking().SingleAsync(
                x => x.RequestId == request.RequestId,
                TestContext.Current.CancellationToken);
            Assert.Equal("Pending", state.Status);
        }

        Assert.Equal(0L, await fixture.CountTargetRowsAsync());
    }

    private static async Task<DmlApprovalRequest> PersistPendingInsertAsync(Fixture fixture)
    {
        const string sql = "INSERT INTO items (id, name) VALUES (1, 'Alice')";
        var runtime = new TypedDmlRuntime();
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
                || (failUnknownSave && statuses.Contains("OutcomeUnknown", StringComparer.Ordinal)))
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
            serviceCollection.Configure<McpKeySettings>(options => options.HmacSecretKey = HmacSecret);
            return serviceCollection.BuildServiceProvider();
        }

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
