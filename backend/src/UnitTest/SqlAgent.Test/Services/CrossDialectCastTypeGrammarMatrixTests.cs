using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CrossDialectCastTypeGrammarMatrixTests
{
    private sealed record TypeVariant(
        string Name,
        SqlAgentToolType Provider,
        string TypeSql,
        SqlProviderCapabilityProfile? Profile = null);

    private sealed record ContextVariant(
        string Name,
        Func<string, string> Build);

    private sealed record CrossTargetVariant(
        string Name,
        SqlAgentToolType Source,
        SqlAgentToolType Target,
        string SourceType,
        string ExpectedTargetType,
        SqlProviderCapabilityProfile? SourceProfile = null,
        SqlProviderCapabilityProfile? TargetProfile = null);

    private sealed record NegativeVariant(
        string Name,
        SqlAgentToolType Source,
        SqlAgentToolType Target,
        string Sql,
        string MessageFragment,
        bool SourceFailure = false);

    private static readonly TypeVariant[] Types =
    [
        new("postgres-boolean", SqlAgentToolType.Postgres, "BOOLEAN"),
        new("postgres-numeric", SqlAgentToolType.Postgres, "NUMERIC(18,4)"),
        new("postgres-varchar", SqlAgentToolType.Postgres, "VARCHAR(64)"),
        new("postgres-timestamptz", SqlAgentToolType.Postgres, "TIMESTAMP(6) WITH TIME ZONE"),
        new("postgres-uuid", SqlAgentToolType.Postgres, "UUID"),

        new("mysql-signed", SqlAgentToolType.MySQL, "SIGNED"),
        new("mysql-unsigned", SqlAgentToolType.MySQL, "UNSIGNED"),
        new("mysql-decimal", SqlAgentToolType.MySQL, "DECIMAL(18,4)"),
        new("mysql-char", SqlAgentToolType.MySQL, "CHAR(64)"),
        new("mysql-datetime", SqlAgentToolType.MySQL, "DATETIME(6)"),
        new("mysql-json", SqlAgentToolType.MySQL, "JSON"),

        new("sqlserver-bit", SqlAgentToolType.MsSqlServer, "BIT"),
        new("sqlserver-decimal", SqlAgentToolType.MsSqlServer, "DECIMAL(18,4)"),
        new("sqlserver-nvarchar-max", SqlAgentToolType.MsSqlServer, "NVARCHAR(MAX)"),
        new("sqlserver-datetime2", SqlAgentToolType.MsSqlServer, "DATETIME2(7)"),
        new("sqlserver-uniqueidentifier", SqlAgentToolType.MsSqlServer, "UNIQUEIDENTIFIER"),
        new("sqlserver-varbinary-max", SqlAgentToolType.MsSqlServer, "VARBINARY(MAX)"),

        new("sqlite-integer", SqlAgentToolType.Sqlite, "INTEGER"),
        new("sqlite-real", SqlAgentToolType.Sqlite, "REAL"),
        new("sqlite-text", SqlAgentToolType.Sqlite, "TEXT"),
        new("sqlite-blob", SqlAgentToolType.Sqlite, "BLOB"),
        new("sqlite-numeric", SqlAgentToolType.Sqlite, "NUMERIC"),

        new("oracle-number", SqlAgentToolType.Oracle, "NUMBER(18,4)"),
        new("oracle-varchar2", SqlAgentToolType.Oracle, "VARCHAR2(64)"),
        new("oracle-date", SqlAgentToolType.Oracle, "DATE"),
        new("oracle-timestamptz", SqlAgentToolType.Oracle, "TIMESTAMP(9) WITH TIME ZONE"),
        new("oracle-binary-double", SqlAgentToolType.Oracle, "BINARY_DOUBLE"),

        new("firebird-boolean", SqlAgentToolType.Firebird, "BOOLEAN"),
        new("firebird-decimal", SqlAgentToolType.Firebird, "DECIMAL(18,4)"),
        new("firebird-varchar", SqlAgentToolType.Firebird, "VARCHAR(64)"),
        new("firebird-timestamp", SqlAgentToolType.Firebird, "TIMESTAMP"),
        new(
            "firebird-timestamptz",
            SqlAgentToolType.Firebird,
            "TIMESTAMP WITH TIME ZONE",
            FirebirdProfile(4)),
        new("firebird-double", SqlAgentToolType.Firebird, "DOUBLE PRECISION")
    ];

    private static readonly ContextVariant[] Contexts =
    [
        new(
            "projection",
            expression => $"SELECT {expression} AS converted FROM records"),
        new(
            "predicate",
            expression => $"SELECT id FROM records WHERE {expression} IS NOT NULL"),
        new(
            "cte",
            expression => $"WITH x AS (SELECT {expression} AS converted FROM records) SELECT converted FROM x")
    ];

    private static readonly string[] PostgresPostfixTypes =
    [
        "BOOLEAN",
        "NUMERIC(18,4)",
        "VARCHAR(64)",
        "TIMESTAMP(6) WITH TIME ZONE",
        "UUID"
    ];

    private static readonly CrossTargetVariant[] CrossTargets =
    [
        new("boolean-postgres-mysql", SqlAgentToolType.Postgres, SqlAgentToolType.MySQL, "BOOLEAN", "SIGNED"),
        new("numeric-postgres-oracle", SqlAgentToolType.Postgres, SqlAgentToolType.Oracle, "NUMERIC(18,4)", "NUMBER(18,4)"),
        new("timestamp-postgres-sqlserver", SqlAgentToolType.Postgres, SqlAgentToolType.MsSqlServer, "TIMESTAMP", "DATETIME2"),
        new("rowversion-sqlserver-postgres", SqlAgentToolType.MsSqlServer, SqlAgentToolType.Postgres, "TIMESTAMP", "BYTEA"),
        new("nvarchar-sqlserver-postgres", SqlAgentToolType.MsSqlServer, SqlAgentToolType.Postgres, "NVARCHAR(64)", "VARCHAR(64)"),
        new("unsigned-mysql-sqlserver", SqlAgentToolType.MySQL, SqlAgentToolType.MsSqlServer, "UNSIGNED", "DECIMAL(20,0)"),
        new("date-oracle-postgres", SqlAgentToolType.Oracle, SqlAgentToolType.Postgres, "DATE", "TIMESTAMP"),
        new(
            "timestamptz-firebird-postgres",
            SqlAgentToolType.Firebird,
            SqlAgentToolType.Postgres,
            "TIMESTAMP WITH TIME ZONE",
            "TIMESTAMP WITH TIME ZONE",
            SourceProfile: FirebirdProfile(4)),
        new("uuid-postgres-sqlserver", SqlAgentToolType.Postgres, SqlAgentToolType.MsSqlServer, "UUID", "UNIQUEIDENTIFIER")
    ];

    private static readonly NegativeVariant[] Negatives =
    [
        new(
            "postgres-zero-varchar",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            "SELECT CAST(value AS VARCHAR(0)) FROM records",
            "length must be positive",
            true),
        new(
            "postgres-scale-over-precision",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            "SELECT CAST(value AS NUMERIC(4,6)) FROM records",
            "scale cannot exceed precision",
            true),
        new(
            "postgres-max-length",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            "SELECT CAST(value AS VARCHAR(MAX)) FROM records",
            "MAX is supported only for SQL Server",
            true),
        new(
            "mysql-zero-decimal",
            SqlAgentToolType.MySQL,
            SqlAgentToolType.MySQL,
            "SELECT CAST(value AS DECIMAL(0,0)) FROM records",
            "precision must be positive",
            true),
        new(
            "sqlserver-temporal-two-args",
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.MsSqlServer,
            "SELECT CAST(value AS DATETIME2(7,2)) FROM records",
            "accepts at most one precision",
            true),
        new(
            "oracle-scale-over-precision",
            SqlAgentToolType.Oracle,
            SqlAgentToolType.Oracle,
            "SELECT CAST(value AS NUMBER(4,6)) FROM records",
            "scale cannot exceed precision",
            true),
        new(
            "firebird-zero-varchar",
            SqlAgentToolType.Firebird,
            SqlAgentToolType.Firebird,
            "SELECT CAST(value AS VARCHAR(0)) FROM records",
            "length must be positive",
            true),
        new(
            "postgres-native-inet-cross-target",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MySQL,
            "SELECT CAST(value AS INET) FROM records",
            "no cross-dialect Core semantic mapping"),
        new(
            "postgres-timezone-to-mysql",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MySQL,
            "SELECT CAST(value AS TIME(6) WITH TIME ZONE) FROM records",
            "no lossless target mapping"),
        new(
            "oracle-precision-to-firebird",
            SqlAgentToolType.Oracle,
            SqlAgentToolType.Firebird,
            "SELECT CAST(value AS TIMESTAMP(9)) FROM records",
            "four fractional-second digits"),
        new(
            "postgres-json-to-sqlserver",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MsSqlServer,
            "SELECT CAST(value AS JSON) FROM records",
            "JSON has no version-independent"),
        new(
            "postgres-unbounded-numeric-to-oracle",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Oracle,
            "SELECT CAST(value AS NUMERIC) FROM records",
            "specify precision and scale")
    ];

    public static IEnumerable<object[]> FirebirdSourceProfileNegativeMatrix()
    {
        yield return
        [
            "firebird-timezone-source-undeclared",
            null,
            "SELECT CAST(value AS TIMESTAMP WITH TIME ZONE) FROM records"
        ];
        yield return
        [
            "firebird-timezone-source-v3",
            FirebirdProfile(3),
            "SELECT CAST(value AS TIME WITH TIME ZONE) FROM records"
        ];
    }

    public static IEnumerable<object[]> PositiveMatrix()
    {
        foreach (var type in Types)
        foreach (var context in Contexts)
        {
            yield return
            [
                SyntaxGrammarMatrix.CaseName(type.Name, context.Name),
                type.Provider,
                type.Profile,
                context.Build($"CAST(value AS {type.TypeSql})"),
                type.TypeSql
            ];
        }
    }

    public static IEnumerable<object[]> PostgresPostfixMatrix()
    {
        foreach (var type in PostgresPostfixTypes)
        foreach (var context in Contexts)
        {
            var normalizedName = type
                .Replace("(", "-", StringComparison.Ordinal)
                .Replace(")", "", StringComparison.Ordinal)
                .Replace(",", "-", StringComparison.Ordinal)
                .Replace(" ", "-", StringComparison.Ordinal)
                .ToLowerInvariant();

            yield return
            [
                SyntaxGrammarMatrix.CaseName("postgres-postfix", normalizedName, context.Name),
                context.Build($"value::{type}"),
                type
            ];
        }
    }

    public static IEnumerable<object[]> CrossProviderMatrix()
    {
        foreach (var item in CrossTargets)
        {
            yield return
            [
                item.Name,
                item.Source,
                item.Target,
                item.SourceProfile,
                item.TargetProfile,
                $"SELECT CAST(value AS {item.SourceType}) FROM records",
                item.ExpectedTargetType
            ];
        }
    }

    public static IEnumerable<object[]> NegativeMatrix()
    {
        foreach (var item in Negatives)
        {
            yield return
            [
                item.Name,
                item.Source,
                item.Target,
                item.Sql,
                item.MessageFragment,
                item.SourceFailure
            ];
        }
    }

    [Fact]
    public void Matrices_HaveStableSixProviderCoverage()
    {
        var positive = PositiveMatrix().ToArray();
        var postfix = PostgresPostfixMatrix().ToArray();
        var cross = CrossProviderMatrix().ToArray();
        var negative = NegativeMatrix().ToArray();

        Assert.Equal(99, positive.Length);
        Assert.Equal(6, positive.Select(item => Assert.IsType<SqlAgentToolType>(item[1])).Distinct().Count());
        Assert.Equal(
            3,
            positive.Count(item => item[2] is SqlProviderCapabilityProfile profile
                && profile.Provider == SqlAgentToolType.Firebird
                && profile.ServerVersion == new Version(4, 0)));
        Assert.Equal(15, postfix.Length);
        Assert.Equal(9, cross.Length);
        Assert.Equal(12, negative.Length);
        Assert.Equal(2, FirebirdSourceProfileNegativeMatrix().Count());
    }

    [Theory]
    [MemberData(nameof(PositiveMatrix))]
    public void PositiveMatrix_ParsesBindsValidatesCompilesAndRenders(
        string name,
        SqlAgentToolType provider,
        SqlProviderCapabilityProfile? profile,
        string sql,
        string expectedType)
    {
        var parsed = CoreSqlTextParser.ParseQuery(sql, provider, profile);
        var facts = SqlCoreInspection.GetQueryFacts(parsed);

        Assert.Contains(
            facts.ReferencedTables,
            table => string.Equals(table, "records", StringComparison.OrdinalIgnoreCase));

        var command = Compile(sql, provider, provider, profile, profile);

        Assert.False(string.IsNullOrWhiteSpace(command.Sql), name);
        Assert.Contains($"AS {expectedType}", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(PostgresPostfixMatrix))]
    public void PostgresPostfixMatrix_CanonicalizesIntoTypedCastAndRenders(
        string name,
        string sql,
        string expectedType)
    {
        var parsed = CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Postgres);
        var command = Compile(sql, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres);

        Assert.False(string.IsNullOrWhiteSpace(command.Sql), name);
        Assert.DoesNotContain("::", command.Sql, StringComparison.Ordinal);
        Assert.Contains($"AS {expectedType}", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(CrossProviderMatrix))]
    public void CrossProviderMatrix_LowersFromCanonicalSqlType(
        string name,
        SqlAgentToolType source,
        SqlAgentToolType target,
        SqlProviderCapabilityProfile? sourceProfile,
        SqlProviderCapabilityProfile? targetProfile,
        string sql,
        string expectedTargetType)
    {
        var command = Compile(sql, source, target, sourceProfile, targetProfile);

        Assert.False(string.IsNullOrWhiteSpace(command.Sql), name);
        Assert.Contains($"AS {expectedTargetType}", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SqlServerDistinctPaging_TreatsCastSynonymsAsTheSameTypedProjection()
    {
        const string sql =
            "SELECT DISTINCT CAST(value AS INT) AS converted " +
            "FROM records " +
            "ORDER BY CAST(value AS INTEGER) " +
            "OFFSET 0 ROWS FETCH NEXT 10 ROWS ONLY";

        var command = Compile(
            sql,
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.MsSqlServer);

        Assert.Contains("CAST([value] AS INT)", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OFFSET", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(FirebirdSourceProfileNegativeMatrix))]
    public void FirebirdTimeZoneCast_SourceProfileFailsAtTypedCapabilityBoundary(
        string name,
        SqlProviderCapabilityProfile? sourceProfile,
        string sql)
    {
        var error = Assert.Throws<SqlCompilationException>(
            () => CoreSqlTextParser.ParseQuery(
                sql,
                SqlAgentToolType.Firebird,
                sourceProfile));

        Assert.Contains("temporal.firebird_time_zone_type", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("4.0", error.Message, StringComparison.OrdinalIgnoreCase);
        var diagnostic = SyntaxGrammarMatrix.RequireTypedDiagnostic(error);
        Assert.Equal("SQL_SOURCE_CAPABILITY_REJECTED", diagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.SourceValidation, diagnostic.Stage);
        Assert.Equal(SqlDiagnosticCategory.Capability, diagnostic.Category);
        Assert.NotNull(diagnostic.Span);
        Assert.True(diagnostic.Span.Length > 0, name);
    }

    [Theory]
    [MemberData(nameof(NegativeMatrix))]
    public void NegativeMatrix_FailsClosedAtTypedBoundary(
        string name,
        SqlAgentToolType source,
        SqlAgentToolType target,
        string sql,
        string messageFragment,
        bool sourceFailure)
    {
        if (sourceFailure)
        {
            var error = Assert.Throws<SqlParseException>(
                () => CoreSqlTextParser.ParseQuery(sql, source));

            Assert.Contains(messageFragment, error.Message, StringComparison.OrdinalIgnoreCase);
            var diagnostic = Assert.IsType<SqlDiagnostic>(error.Diagnostic);
            Assert.Equal("SQL_SOURCE_TYPE_REJECTED", diagnostic.Code);
            Assert.Equal(SqlDiagnosticStage.SourceValidation, diagnostic.Stage);
            Assert.Equal(SqlDiagnosticCategory.DialectSyntax, diagnostic.Category);
            Assert.NotNull(diagnostic.Span);
            Assert.True(diagnostic.Span.Length > 0, name);
            return;
        }

        var targetError = Assert.Throws<SqlCompilationException>(
            () => Compile(sql, source, target));

        Assert.Contains(messageFragment, targetError.Message, StringComparison.OrdinalIgnoreCase);
        var targetDiagnostic = SyntaxGrammarMatrix.RequireTypedDiagnostic(targetError);
        Assert.Equal("SQL_TARGET_CAPABILITY_REJECTED", targetDiagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.TargetCapability, targetDiagnostic.Stage);
        Assert.Equal(SqlDiagnosticCategory.Capability, targetDiagnostic.Category);
        Assert.NotNull(targetDiagnostic.Span);
        Assert.True(targetDiagnostic.Span.Length > 0, name);
    }

    private static CompiledSqlCommand Compile(
        string sql,
        SqlAgentToolType source,
        SqlAgentToolType target) =>
        Compile(sql, source, target, null, null);

    private static CompiledSqlCommand Compile(
        string sql,
        SqlAgentToolType source,
        SqlAgentToolType target,
        SqlProviderCapabilityProfile? sourceProfile,
        SqlProviderCapabilityProfile? targetProfile) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, source, sourceProfile),
            target,
            new SqlPlanValidationContext("cross-dialect-cast-type-grammar-matrix-v1"),
            new SqlExecutionPlanPolicy(),
            targetProfile);

    private static SqlProviderCapabilityProfile FirebirdProfile(int majorVersion) =>
        new(
            SqlAgentToolType.Firebird,
            ServerVersion: new Version(majorVersion, 0));
}
