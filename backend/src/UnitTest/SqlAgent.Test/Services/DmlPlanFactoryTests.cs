using System.Data.Common;
using Moq;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace SqlAgent.Test.Services;

public class DmlPlanFactoryTests
{
    [Fact]
    public async Task CreateAsync_Strict_BuildsMutationAndPrimaryKeyMatchFromSameDefinition()
    {
        var metadata = new StubMetadataReader(
        [
            new DatabaseColumnMetadata("public", "users", "id", "integer", true, 1),
            new DatabaseColumnMetadata("public", "users", "status", "text", false)
        ]);
        var parsed = CoreSqlTextParser.ParseDml(
            "UPDATE public.users SET status = 'disabled' WHERE id = 7",
            SqlAgentToolType.Postgres);

        var plan = await new DmlPlanFactory(metadata).CreateAsync(
            "connection",
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext(
                "policy-v2",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "public.users" }),
            new DmlCompilationPolicy(),
            DmlRowIdentityAssurance.Strict,
            maxAffectedRows: 25,
            approvalTtl: TimeSpan.FromMinutes(2),
            cancellationToken: TestContext.Current.CancellationToken);
        var matchCommand = Assert.IsType<CompiledSqlCommand>(plan.MatchQueryCommand);

        Assert.Equal(["id"], plan.RowIdentityColumns);
        Assert.Equal(DmlRowIdentityAssurance.Strict, plan.RowIdentityAssurance);
        Assert.Equal(SqlStatementKind.Update, plan.MutationCommand.Kind);
        Assert.Equal(SqlStatementKind.Select, matchCommand.Kind);
        Assert.Contains("UPDATE", plan.MutationCommand.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("disabled", plan.MutationCommand.Sql, StringComparison.Ordinal);
        Assert.Contains(plan.MutationCommand.Parameters, parameter => Equals(parameter.Value, "disabled"));
        Assert.Contains(plan.MutationCommand.Parameters, parameter => IsInteger(parameter.Value, 7));
        Assert.Contains("id", matchCommand.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" = 7", matchCommand.Sql, StringComparison.Ordinal);
        Assert.Contains(matchCommand.Parameters, parameter => IsInteger(parameter.Value, 7));
        Assert.Contains(matchCommand.Parameters, parameter => IsInteger(parameter.Value, 26));
        Assert.Equal("policy-v2", plan.PolicyVersion);
        Assert.Equal(TimeSpan.FromMinutes(2), plan.ApprovalTtl);
        Assert.Equal(25, plan.MaxAffectedRows);
        Assert.Equal(
            DmlFingerprintService.ComputePlanFingerprint(plan.MutationCommand, "policy-v2"),
            plan.PlanFingerprint);
    }

    [Fact]
    public async Task CreateAsync_UnqualifiedTarget_UsesResolvedQualifiedTableForBothCommands()
    {
        var metadata = new StubMetadataReader(
        [
            new DatabaseColumnMetadata("public", "users", "id", "integer", true, 1),
            new DatabaseColumnMetadata("public", "users", "status", "text", false)
        ],
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["audit"] = ["events"],
            ["public"] = ["users"]
        });
        var parsed = CoreSqlTextParser.ParseDml(
            "UPDATE users SET status = 'disabled' WHERE id = 7",
            SqlAgentToolType.Postgres);

        var plan = await new DmlPlanFactory(metadata).CreateAsync(
            "connection",
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext(
                "policy-v3",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "public.users" }),
            cancellationToken: TestContext.Current.CancellationToken);
        var matchCommand = Assert.IsType<CompiledSqlCommand>(plan.MatchQueryCommand);

        Assert.Equal("public.users", plan.TableName);
        Assert.Contains("public", plan.MutationCommand.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("users", plan.MutationCommand.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("public", matchCommand.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("users", matchCommand.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_SqlServerOutput_UsesResolvedTargetTriggerMetadataAssurance()
    {
        var metadata = new StubMetadataReader(
        [
            new DatabaseColumnMetadata("dbo", "users", "id", "int", true, 1),
            new DatabaseColumnMetadata("dbo", "users", "status", "nvarchar", false)
        ],
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["dbo"] = ["users"]
        });
        var parsed = CoreSqlTextParser.ParseDml(
            "UPDATE users SET status = 'disabled' OUTPUT INSERTED.id WHERE id = 7",
            SqlAgentToolType.MsSqlServer);

        var plan = await new DmlPlanFactory(metadata).CreateAsync(
            "connection",
            parsed,
            SqlAgentToolType.MsSqlServer,
            new SqlPlanValidationContext(
                "sqlserver-output-metadata-v1",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dbo.users" }),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(plan.MutationCommand.ReturnsRows);
        Assert.Contains("OUTPUT INSERTED.", plan.MutationCommand.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(("dbo", "users", DmlOperation.Update), metadata.LastTriggerRequest);
    }

    [Fact]
    public async Task CreateAsync_SqlServerOutput_WithEnabledTrigger_FailsClosed()
    {
        var metadata = new StubMetadataReader(
        [
            new DatabaseColumnMetadata("dbo", "users", "id", "int", true, 1),
            new DatabaseColumnMetadata("dbo", "users", "status", "nvarchar", false)
        ],
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["dbo"] = ["users"]
        },
        hasEnabledDmlTrigger: true);
        var parsed = CoreSqlTextParser.ParseDml(
            "UPDATE users SET status = 'disabled' OUTPUT INSERTED.id WHERE id = 7",
            SqlAgentToolType.MsSqlServer);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DmlPlanFactory(metadata).CreateAsync(
                "connection",
                parsed,
                SqlAgentToolType.MsSqlServer,
                new SqlPlanValidationContext("sqlserver-output-trigger-v1"),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("OUTPUT", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("enabled UPDATE trigger", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(("dbo", "users", DmlOperation.Update), metadata.LastTriggerRequest);
    }


    [Fact]
    public async Task CreateAsync_InsertValues_ResolvesTargetWithoutLoadingRowIdentityColumns()
    {
        var metadata = new StubMetadataReader([]);
        var parsed = CoreSqlTextParser.ParseDml(
            "INSERT INTO public.users (id, status) VALUES (1, 'active')",
            SqlAgentToolType.Postgres);

        var plan = await new DmlPlanFactory(metadata).CreateAsync(
            "connection",
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext(
                "insert-no-row-identity-metadata-v1",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "public.users" }),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(DmlApprovalMode.InsertValues, plan.ApprovalMode);
        Assert.Equal("public.users", plan.TableName);
        Assert.Equal(0, metadata.StringColumnRequests);
    }

    [Fact]
    public async Task CreateWithMetadataConnectionAsync_UsesSinglePlanningSnapshotForResolutionIdentityAndTrigger()
    {
        var metadata = new StubMetadataReader(
        [
            new DatabaseColumnMetadata("dbo", "users", "id", "int", true, 1),
            new DatabaseColumnMetadata("dbo", "users", "status", "nvarchar", false)
        ],
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["dbo"] = ["users"]
        });
        var parsed = CoreSqlTextParser.ParseDml(
            "UPDATE users SET status = 'disabled' OUTPUT INSERTED.id WHERE id = 7",
            SqlAgentToolType.MsSqlServer);
        var metadataConnection = new Mock<DbConnection>().Object;

        var plan = await new DmlPlanFactory(metadata).CreateWithMetadataConnectionAsync(
            metadataConnection,
            "connection",
            parsed,
            SqlAgentToolType.MsSqlServer,
            new SqlPlanValidationContext(
                "connection-metadata-output-v1",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dbo.users" }),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(plan.MutationCommand.ReturnsRows);
        Assert.Equal(1, metadata.ConnectionPlanningSnapshotRequests);
        Assert.Equal(0, metadata.ConnectionFindTablesRequests);
        Assert.Equal(0, metadata.ConnectionColumnRequests);
        Assert.Equal(0, metadata.ConnectionTriggerRequests);
        Assert.Equal(0, metadata.StringColumnRequests);
        Assert.Equal(0, metadata.StringTriggerRequests);
        Assert.Equal(("dbo", "users", DmlOperation.Update), metadata.LastTriggerRequest);
    }

    [Fact]
    public async Task CreateWithMetadataConnectionAsync_UnqualifiedTarget_UsesSinglePlanningSnapshot()
    {
        var metadata = new StubMetadataReader(
        [
            new DatabaseColumnMetadata("public", "users", "id", "integer", true, 1),
            new DatabaseColumnMetadata("public", "users", "status", "text", false)
        ],
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["public"] = ["users"]
        });
        var parsed = CoreSqlTextParser.ParseDml(
            "UPDATE users SET status = 'disabled' WHERE id = 7",
            SqlAgentToolType.Postgres);
        var metadataConnection = new Mock<DbConnection>().Object;

        var plan = await new DmlPlanFactory(metadata).CreateWithMetadataConnectionAsync(
            metadataConnection,
            "connection",
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext(
                "connection-planning-snapshot-v1",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "public.users" }),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("public.users", plan.TableName);
        Assert.Equal(["id"], plan.RowIdentityColumns);
        Assert.Equal(1, metadata.ConnectionPlanningSnapshotRequests);
        Assert.Equal(0, metadata.ConnectionFindTablesRequests);
        Assert.Equal(0, metadata.ConnectionColumnRequests);
        Assert.Equal(0, metadata.ConnectionTriggerRequests);
    }

    [Fact]
    public async Task CreateWithMetadataConnectionAsync_SqlServerOutput_UnprovenSnapshotTriggerMetadataFailsClosed()
    {
        var metadata = new StubMetadataReader(
        [
            new DatabaseColumnMetadata("dbo", "users", "id", "int", true, 1),
            new DatabaseColumnMetadata("dbo", "users", "status", "nvarchar", false)
        ],
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["dbo"] = ["users"]
        },
        hasCompleteTriggerMetadata: false);
        var parsed = CoreSqlTextParser.ParseDml(
            "UPDATE users SET status = 'disabled' OUTPUT INSERTED.id WHERE id = 7",
            SqlAgentToolType.MsSqlServer);
        var metadataConnection = new Mock<DbConnection>().Object;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DmlPlanFactory(metadata).CreateWithMetadataConnectionAsync(
                metadataConnection,
                "connection",
                parsed,
                SqlAgentToolType.MsSqlServer,
                new SqlPlanValidationContext("snapshot-trigger-visibility-v1"),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("VIEW DEFINITION", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fail-closed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, metadata.ConnectionPlanningSnapshotRequests);
        Assert.Equal(0, metadata.ConnectionTriggerRequests);
    }

    [Fact]
    public async Task CreateAsync_Strict_RejectsMissingPrimaryKey()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "DELETE FROM public.events WHERE status = 'old'",
            SqlAgentToolType.Postgres);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DmlPlanFactory(new StubMetadataReader(
            [
                new DatabaseColumnMetadata("public", "events", "status", "text", false)
            ])).CreateAsync(
                "connection",
                parsed,
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("policy-v1"),
                assurance: DmlRowIdentityAssurance.Strict,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("primary key", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInteger(object? value, long expected) =>
        value switch
        {
            byte x => x == expected,
            sbyte x => x == expected,
            short x => x == expected,
            ushort x => x == expected,
            int x => x == expected,
            uint x => x == expected,
            long x => x == expected,
            ulong x => x == (ulong)expected,
            _ => false
        };

    private sealed class StubMetadataReader(
        IReadOnlyList<DatabaseColumnMetadata> columns,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? tablesBySchema = null,
        bool hasEnabledDmlTrigger = false,
        bool hasCompleteTriggerMetadata = true)
        : IProviderMetadataReader,
          HsSqlAgent.Provider.Abstractions.IProviderDmlResultRowMetadataReader,
          IProviderConnectionMetadataReader,
          HsSqlAgent.Provider.Abstractions.IProviderConnectionDmlResultRowMetadataReader,
          HsSqlAgent.Provider.Abstractions.IProviderConnectionDmlPlanningMetadataReader
    {
        private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _tablesBySchema =
            tablesBySchema ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        public (string Schema, string Table, DmlOperation Operation)? LastTriggerRequest { get; private set; }
        public int StringColumnRequests { get; private set; }
        public int StringTriggerRequests { get; private set; }
        public int ConnectionFindTablesRequests { get; private set; }
        public int ConnectionColumnRequests { get; private set; }
        public int ConnectionTriggerRequests { get; private set; }
        public int ConnectionPlanningSnapshotRequests { get; private set; }

        public Task<IReadOnlyList<string>> GetSchemasAsync(
            string connectionString,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(_tablesBySchema.Keys.ToArray());

        public Task<IReadOnlyList<string>> GetTablesAsync(
            string connectionString,
            string schema,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                _tablesBySchema.TryGetValue(schema, out var tables)
                    ? tables
                    : (IReadOnlyList<string>)[]);

        public Task<IReadOnlyList<DatabaseColumnMetadata>> GetColumnsAsync(
            string connectionString,
            string schema,
            string table,
            CancellationToken cancellationToken = default)
        {
            StringColumnRequests++;
            return Task.FromResult(columns);
        }

        public Task<IReadOnlyList<DatabaseTableMetadata>> FindTablesAsync(
            DbConnection connection,
            string tableName,
            CancellationToken cancellationToken = default)
        {
            ConnectionFindTablesRequests++;
            var matches = _tablesBySchema
                .SelectMany(pair => pair.Value
                    .Where(table => string.Equals(table, tableName, StringComparison.OrdinalIgnoreCase))
                    .Select(table => new DatabaseTableMetadata(pair.Key, table)))
                .ToArray();
            return Task.FromResult<IReadOnlyList<DatabaseTableMetadata>>(matches);
        }

        public Task<IReadOnlyList<DatabaseColumnMetadata>> GetColumnsAsync(
            DbConnection connection,
            string schema,
            string table,
            CancellationToken cancellationToken = default)
        {
            ConnectionColumnRequests++;
            return Task.FromResult(columns);
        }

        public Task<IReadOnlyList<HsSqlAgent.Provider.Abstractions.DatabaseDmlPlanningMetadata>> GetDmlPlanningMetadataAsync(
            DbConnection connection,
            string? schema,
            string table,
            bool includeColumns,
            DmlOperation? triggerOperation = null,
            CancellationToken cancellationToken = default)
        {
            ConnectionPlanningSnapshotRequests++;
            var matches = _tablesBySchema
                .Where(pair => schema is null
                    || string.Equals(pair.Key, schema, StringComparison.OrdinalIgnoreCase))
                .SelectMany(pair => pair.Value
                    .Where(candidate => string.Equals(candidate, table, StringComparison.OrdinalIgnoreCase))
                    .Select(candidate => new HsSqlAgent.Provider.Abstractions.DatabaseDmlPlanningMetadata(
                        pair.Key,
                        candidate,
                        includeColumns ? columns : [],
                        triggerOperation.HasValue && hasCompleteTriggerMetadata
                            ? hasEnabledDmlTrigger
                            : null)))
                .ToArray();

            if (triggerOperation.HasValue && matches.Length == 1)
                LastTriggerRequest = (matches[0].Schema, matches[0].Table, triggerOperation.Value);

            return Task.FromResult<IReadOnlyList<HsSqlAgent.Provider.Abstractions.DatabaseDmlPlanningMetadata>>(matches);
        }

        public Task<IReadOnlyList<DatabaseUniqueKeyMetadata>> GetUniqueKeysAsync(
            string connectionString,
            string schema,
            string table,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DatabaseUniqueKeyMetadata>>([]);

        public Task<bool> HasEnabledDmlTriggerAsync(
            string connectionString,
            string schema,
            string table,
            DmlOperation operation,
            CancellationToken cancellationToken = default)
        {
            StringTriggerRequests++;
            LastTriggerRequest = (schema, table, operation);
            return Task.FromResult(hasEnabledDmlTrigger);
        }

        public Task<bool> HasEnabledDmlTriggerAsync(
            DbConnection connection,
            string schema,
            string table,
            DmlOperation operation,
            CancellationToken cancellationToken = default)
        {
            ConnectionTriggerRequests++;
            LastTriggerRequest = (schema, table, operation);
            return Task.FromResult(hasEnabledDmlTrigger);
        }
    }
}
