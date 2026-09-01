using System.Data;
using System.Data.Common;
using Admin.Service.Models;
using Moq;

namespace HsSqlAgent.Server.Test.Services;

internal static class SyntaxBoundaryTestSupport
{
    public static Mock<ISqlProvider> Provider(SqlAgentToolType type)
    {
        var provider = new Mock<ISqlProvider>();
        provider.SetupGet(x => x.Type).Returns(type);
        return provider;
    }

    public static SecurityPolicyModel Policy() => new()
    {
        QueryMaxRows = 100,
        QueryTimeoutSeconds = 30,
        RequireWhereForUpdate = true,
        RequireWhereForDelete = true,
        AllowFullTableUpdate = false,
        AllowFullTableDelete = false,
        DmlMaxAffectedRows = 100
    };

    public static IReadOnlySet<string> AllowedTables(string csv) =>
        csv.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static DmlBoundaryProvider DmlProvider(SqlAgentToolType type)
    {
        var (schema, table, version) = type switch
        {
            SqlAgentToolType.Postgres => ("public", "users", "17.5"),
            SqlAgentToolType.MySQL => ("app", "users", "8.4.0"),
            SqlAgentToolType.MsSqlServer => ("dbo", "users", "16.0"),
            SqlAgentToolType.Sqlite => ("main", "users", "3.46.0"),
            SqlAgentToolType.Oracle => ("APP", "USERS", "23.0"),
            SqlAgentToolType.Firebird => ("APP", "USERS", "5.0"),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown SQL dialect.")
        };

        var metadata = new Mock<IProviderMetadataReader>(MockBehavior.Strict);
        metadata
            .Setup(x => x.GetSchemasAsync(
                "connection",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([schema]);
        metadata
            .Setup(x => x.GetTablesAsync(
                "connection",
                schema,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([table]);
        metadata
            .Setup(x => x.GetColumnsAsync(
                "connection",
                schema,
                table,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new DatabaseColumnMetadata(schema, table, "id", "integer", false),
                new DatabaseColumnMetadata(schema, table, "name", "text", false)
            ]);

        var connections = new BoundaryConnectionFactory(version);
        var provider = new Mock<ISqlProvider>(MockBehavior.Strict);
        provider.SetupGet(x => x.Type).Returns(type);
        provider.SetupGet(x => x.Metadata).Returns(metadata.Object);
        provider.SetupGet(x => x.Connections).Returns(connections);

        return new DmlBoundaryProvider(
            provider,
            metadata,
            connections,
            schema,
            table,
            version);
    }
}

internal sealed record DmlBoundaryProvider(
    Mock<ISqlProvider> Provider,
    Mock<IProviderMetadataReader> Metadata,
    BoundaryConnectionFactory Connections,
    string Schema,
    string Table,
    string ServerVersion)
{
    public string QualifiedTable => Schema + "." + Table;
}

internal sealed class BoundaryConnectionFactory(string serverVersion) : IDbConnectionFactory
{
    private readonly string _serverVersion = serverVersion;

    public int CreateCount { get; private set; }

    public DbConnection Create(string connectionString)
    {
        CreateCount++;
        return new BoundaryConnection(_serverVersion, connectionString);
    }
}

internal sealed class BoundaryConnection(
    string serverVersion,
    string connectionString) : DbConnection
{
    private readonly string _serverVersion = serverVersion;
    private string _connectionString = connectionString;
    private ConnectionState _state = ConnectionState.Closed;

    public override string ConnectionString
    {
        get => _connectionString;
        set => _connectionString = value;
    }

    public override string Database => "syntax-boundary";
    public override string DataSource => "syntax-boundary";
    public override string ServerVersion => _serverVersion;
    public override ConnectionState State => _state;

    public override void ChangeDatabase(string databaseName) { }

    public override void Close() =>
        _state = ConnectionState.Closed;

    public override void Open() =>
        _state = ConnectionState.Open;

    public override Task OpenAsync(CancellationToken cancellationToken)
    {
        _state = ConnectionState.Open;
        return Task.CompletedTask;
    }

    protected override DbTransaction BeginDbTransaction(
        IsolationLevel isolationLevel) =>
        throw new NotSupportedException(
            "Syntax-boundary INSERT preview never opens a mutation transaction.");

    protected override DbCommand CreateDbCommand() =>
        throw new NotSupportedException(
            "Syntax-boundary INSERT preview never executes database commands.");
}
