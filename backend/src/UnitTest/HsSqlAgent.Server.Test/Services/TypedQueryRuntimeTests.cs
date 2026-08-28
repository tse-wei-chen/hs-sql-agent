using System.Data;
using System.Data.Common;
using Admin.Service.Models;
using HsSqlAgent.Server.Services;
using Moq;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

public class TypedQueryRuntimeTests
{
    [Fact]
    public void SqlStrategyContract_DoesNotExposeExecutionMethods()
    {
        var methodNames = typeof(ISqlStrategy).GetMethods().Select(method => method.Name).ToArray();

        Assert.DoesNotContain("ExecuteQueryAsync", methodNames);
        Assert.DoesNotContain("ExecuteDmlAsync", methodNames);
    }

    [Fact]
    public void Compile_AppliesCoreMaxRowsAndKeepsValuesParameterized()
    {
        var runtime = new TypedQueryRuntime();
        var provider = CreateProvider(SqlAgentToolType.Postgres);
        var definition = new QueryDefinition
        {
            TableName = "public.users",
            SelectColumns = [new FieldSelectCondition { FieldName = "id" }],
            WhereColumnsAndValues =
            [
                new BasicWhereCondition
                {
                    FieldName = "status",
                    Operator = "=",
                    Value = "active"
                }
            ]
        };
        var policy = CreatePolicy(maxRows: 25);

        var command = runtime.Compile(
            provider.Object,
            Map(definition, SqlAgentToolType.Postgres),
            policy,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "public.users" });

        Assert.Equal(SqlStatementKind.Select, command.Kind);
        Assert.Contains("LIMIT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("active", command.Sql, StringComparison.Ordinal);
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, "active"));
        Assert.False(string.IsNullOrWhiteSpace(command.PlanFingerprint));
    }

    [Fact]
    public void Compile_RejectsUnauthorizedTableBeforeExecution()
    {
        var runtime = new TypedQueryRuntime();
        var provider = CreateProvider(SqlAgentToolType.Postgres);
        var definition = new QueryDefinition
        {
            TableName = "public.secrets",
            SelectColumns = [new FieldSelectCondition { FieldName = "id" }]
        };

        Assert.Throws<UnauthorizedAccessException>(() => runtime.Compile(
            provider.Object,
            Map(definition, SqlAgentToolType.Postgres),
            CreatePolicy(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "public.users" }));
    }

    [Fact]
    public void Compile_PolicyOrAuthorizationChange_ChangesPlanFingerprint()
    {
        var runtime = new TypedQueryRuntime();
        var provider = CreateProvider(SqlAgentToolType.Postgres);
        var definition = new QueryDefinition
        {
            TableName = "public.users",
            SelectColumns = [new FieldSelectCondition { FieldName = "id" }]
        };
        var parsed = Map(definition, SqlAgentToolType.Postgres);

        var first = runtime.Compile(
            provider.Object,
            parsed,
            CreatePolicy(maxRows: 10),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "public.users" });
        var second = runtime.Compile(
            provider.Object,
            parsed,
            CreatePolicy(maxRows: 20),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "public.users" });

        Assert.NotEqual(first.PlanFingerprint, second.PlanFingerprint);
    }

    [Fact]
    public void CreateVerifiedTargetProfile_UsesOpenConnectionServerVersion()
    {
        var connection = new Mock<DbConnection>();
        connection.SetupGet(x => x.State).Returns(ConnectionState.Open);
        connection.SetupGet(x => x.ServerVersion).Returns("17.5 (Debian 17.5-1)");

        var profile = TypedQueryRuntime.CreateVerifiedTargetProfile(
            SqlAgentToolType.Postgres,
            connection.Object);

        Assert.Equal(SqlAgentToolType.Postgres, profile.Provider);
        Assert.Equal(new Version(17, 5), profile.ServerVersion);
    }

    [Fact]
    public void CreateVerifiedTargetProfile_RequiresOpenConnection()
    {
        var connection = new Mock<DbConnection>();
        connection.SetupGet(x => x.State).Returns(ConnectionState.Closed);

        Assert.Throws<InvalidOperationException>(() =>
            TypedQueryRuntime.CreateVerifiedTargetProfile(
                SqlAgentToolType.Postgres,
                connection.Object));
    }

    private static ParsedStatement Map(QueryDefinition definition, SqlAgentToolType sourceDialect) =>
        new(QueryDefinitionCoreMapper.Map(definition), sourceDialect);

    private static Mock<ISqlProvider> CreateProvider(SqlAgentToolType type)
    {
        var provider = new Mock<ISqlProvider>();
        provider.SetupGet(x => x.Type).Returns(type);
        return provider;
    }

    private static SecurityPolicyModel CreatePolicy(int maxRows = 100) => new()
    {
        QueryMaxRows = maxRows,
        QueryTimeoutSeconds = 30,
        RequireWhereForUpdate = true,
        RequireWhereForDelete = true,
        AllowFullTableUpdate = false,
        AllowFullTableDelete = false,
        DmlMaxAffectedRows = 100
    };
}
