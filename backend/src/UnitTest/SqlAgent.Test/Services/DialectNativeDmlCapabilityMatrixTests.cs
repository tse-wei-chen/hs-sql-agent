using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Models;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class DialectNativeDmlCapabilityMatrixTests
{
    public static int NativeDmlCaseCount => Cases.Length;

    private enum AssuranceKind
    {
        None,
        PrimaryKeyId,
        SoleUniqueId
    }

    private sealed record NativeDmlCase(
        string Name,
        string Sql,
        SqlAgentToolType SourceDialect,
        SqlAgentToolType TargetDialect,
        Version? SourceVersion,
        Version? TargetVersion,
        AssuranceKind Assurance,
        SqlStatementKind ExpectedKind,
        bool ReturnsRows,
        string RenderedFragments);

    private static readonly NativeDmlCase[] Cases =
    [
        new(
            "postgres-insert-returning",
            "INSERT INTO users (id, name) VALUES (1, 'Alice') RETURNING id",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            null,
            null,
            AssuranceKind.None,
            SqlStatementKind.Insert,
            true,
            "INSERT INTO;RETURNING"),
        new(
            "postgres-update-returning",
            "UPDATE users SET name = 'Alice' WHERE id = 1 RETURNING id",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            null,
            null,
            AssuranceKind.None,
            SqlStatementKind.Update,
            true,
            "UPDATE;RETURNING"),
        new(
            "postgres-delete-returning",
            "DELETE FROM users WHERE id = 1 RETURNING id",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            null,
            null,
            AssuranceKind.None,
            SqlStatementKind.Delete,
            true,
            "DELETE FROM;RETURNING"),
        new(
            "postgres-update-from",
            "UPDATE users SET name = profiles.name FROM profiles WHERE users.id = profiles.user_id",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            null,
            null,
            AssuranceKind.None,
            SqlStatementKind.Update,
            false,
            "UPDATE; FROM ;profiles"),
        new(
            "postgres-delete-using",
            "DELETE FROM users USING profiles WHERE users.id = profiles.user_id",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            null,
            null,
            AssuranceKind.None,
            SqlStatementKind.Delete,
            false,
            "DELETE FROM;USING;profiles"),
        new(
            "postgres-conflict-do-nothing",
            "INSERT INTO users (id, name) VALUES (1, 'Alice') ON CONFLICT (id) DO NOTHING",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            null,
            null,
            AssuranceKind.None,
            SqlStatementKind.Insert,
            false,
            "ON CONFLICT;DO NOTHING"),
        new(
            "postgres-conflict-update",
            "INSERT INTO users (id, name) VALUES (1, 'Alice') ON CONFLICT (id) DO UPDATE SET name = excluded.name",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            null,
            null,
            AssuranceKind.None,
            SqlStatementKind.Insert,
            false,
            "ON CONFLICT;DO UPDATE;EXCLUDED"),
        new(
            "sqlite-insert-returning-335",
            "INSERT INTO users (id, name) VALUES (1, 'Alice') RETURNING id",
            SqlAgentToolType.Sqlite,
            SqlAgentToolType.Sqlite,
            new Version(3, 35),
            new Version(3, 35),
            AssuranceKind.None,
            SqlStatementKind.Insert,
            true,
            "INSERT INTO;RETURNING"),
        new(
            "sqlite-update-returning-335",
            "UPDATE users SET name = 'Alice' WHERE id = 1 RETURNING id",
            SqlAgentToolType.Sqlite,
            SqlAgentToolType.Sqlite,
            new Version(3, 35),
            new Version(3, 35),
            AssuranceKind.None,
            SqlStatementKind.Update,
            true,
            "UPDATE;RETURNING"),
        new(
            "sqlite-delete-returning-335",
            "DELETE FROM users WHERE id = 1 RETURNING id",
            SqlAgentToolType.Sqlite,
            SqlAgentToolType.Sqlite,
            new Version(3, 35),
            new Version(3, 35),
            AssuranceKind.None,
            SqlStatementKind.Delete,
            true,
            "DELETE FROM;RETURNING"),
        new(
            "sqlite-conflict-update-335",
            "INSERT INTO users (id, name) VALUES (1, 'Alice') ON CONFLICT (id) DO UPDATE SET name = excluded.name",
            SqlAgentToolType.Sqlite,
            SqlAgentToolType.Sqlite,
            new Version(3, 35),
            new Version(3, 35),
            AssuranceKind.None,
            SqlStatementKind.Insert,
            false,
            "ON CONFLICT;DO UPDATE;excluded"),
        new(
            "sqlite-update-from-333",
            "UPDATE users SET name = profiles.name FROM profiles WHERE users.id = profiles.user_id",
            SqlAgentToolType.Sqlite,
            SqlAgentToolType.Sqlite,
            new Version(3, 33),
            new Version(3, 33),
            AssuranceKind.None,
            SqlStatementKind.Update,
            false,
            "UPDATE; FROM ;profiles"),
        new(
            "sqlite-update-returning-expression-335",
            "UPDATE users SET score = score + 1 WHERE id = 1 RETURNING score + 2 AS next_score",
            SqlAgentToolType.Sqlite,
            SqlAgentToolType.Sqlite,
            new Version(3, 35),
            new Version(3, 35),
            AssuranceKind.None,
            SqlStatementKind.Update,
            true,
            "UPDATE;RETURNING;next_score"),
        new(
            "firebird-insert-returning-5",
            "INSERT INTO users (id, name) VALUES (1, 'Alice') RETURNING id",
            SqlAgentToolType.Firebird,
            SqlAgentToolType.Firebird,
            new Version(5, 0),
            new Version(5, 0),
            AssuranceKind.None,
            SqlStatementKind.Insert,
            true,
            "INSERT INTO;RETURNING"),
        new(
            "firebird-update-returning-5",
            "UPDATE users SET name = 'Alice' WHERE id = 1 RETURNING id",
            SqlAgentToolType.Firebird,
            SqlAgentToolType.Firebird,
            new Version(5, 0),
            new Version(5, 0),
            AssuranceKind.None,
            SqlStatementKind.Update,
            true,
            "UPDATE;RETURNING"),
        new(
            "firebird-update-returning-expression-5",
            "UPDATE users SET score = score + 1 WHERE id = 1 RETURNING score + 2 AS next_score",
            SqlAgentToolType.Firebird,
            SqlAgentToolType.Firebird,
            new Version(5, 0),
            new Version(5, 0),
            AssuranceKind.None,
            SqlStatementKind.Update,
            true,
            "UPDATE;RETURNING;next_score"),
        new(
            "firebird-delete-returning-5",
            "DELETE FROM users WHERE id = 1 RETURNING id",
            SqlAgentToolType.Firebird,
            SqlAgentToolType.Firebird,
            new Version(5, 0),
            new Version(5, 0),
            AssuranceKind.None,
            SqlStatementKind.Delete,
            true,
            "DELETE FROM;RETURNING"),
        new(
            "firebird-update-or-insert-returning-5",
            "UPDATE OR INSERT INTO users (id, name) VALUES (1, 'Alice') MATCHING (id) RETURNING id",
            SqlAgentToolType.Firebird,
            SqlAgentToolType.Firebird,
            new Version(5, 0),
            new Version(5, 0),
            AssuranceKind.PrimaryKeyId,
            SqlStatementKind.Insert,
            true,
            "UPDATE OR INSERT INTO;MATCHING;RETURNING"),
        new(
            "firebird-update-target-alias",
            "UPDATE users AS u SET name = 'Alice' WHERE u.id = 1",
            SqlAgentToolType.Firebird,
            SqlAgentToolType.Firebird,
            null,
            null,
            AssuranceKind.None,
            SqlStatementKind.Update,
            false,
            "UPDATE; AS ;u;WHERE"),
        new(
            "firebird-delete-target-alias",
            "DELETE FROM users AS u WHERE u.id = 1",
            SqlAgentToolType.Firebird,
            SqlAgentToolType.Firebird,
            null,
            null,
            AssuranceKind.None,
            SqlStatementKind.Delete,
            false,
            "DELETE FROM; AS ;u;WHERE"),
        new(
            "sqlserver-update-from",
            "UPDATE users SET name = profiles.name FROM profiles WHERE users.id = profiles.user_id",
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.MsSqlServer,
            null,
            null,
            AssuranceKind.None,
            SqlStatementKind.Update,
            false,
            "UPDATE; FROM ;profiles"),
        new(
            "postgres-delete-using-to-sqlserver",
            "DELETE FROM users USING profiles WHERE users.id = profiles.user_id",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MsSqlServer,
            null,
            null,
            AssuranceKind.None,
            SqlStatementKind.Delete,
            false,
            "DELETE FROM; FROM ;profiles;WHERE"),
        new(
            "mysql-assured-upsert-8019",
            "INSERT INTO users (id, name) VALUES (1, 'Alice') ON CONFLICT (id) DO UPDATE SET name = excluded.name",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MySQL,
            null,
            new Version(8, 0, 19),
            AssuranceKind.SoleUniqueId,
            SqlStatementKind.Insert,
            false,
            "ON DUPLICATE KEY UPDATE;__core_proposed")
    ];

    public static IEnumerable<object[]> NativeDmlMatrix() =>
        Cases.Select(item => new object[]
        {
            item.Name,
            item.Sql,
            item.SourceDialect,
            item.TargetDialect,
            item.SourceVersion,
            item.TargetVersion,
            item.Assurance.ToString(),
            item.ExpectedKind,
            item.ReturnsRows,
            item.RenderedFragments
        });

    [Fact]
    public void NativeDmlMatrix_HasStableCapabilityCoverage()
    {
        Assert.Equal(23, Cases.Length);
        Assert.Equal(
            23,
            Cases.Select(item => item.Name)
                .Distinct(StringComparer.Ordinal)
                .Count());

        Assert.Equal(7, Cases.Count(item => item.TargetDialect == SqlAgentToolType.Postgres));
        Assert.Equal(6, Cases.Count(item => item.TargetDialect == SqlAgentToolType.Sqlite));
        Assert.Equal(7, Cases.Count(item => item.TargetDialect == SqlAgentToolType.Firebird));
        Assert.Equal(2, Cases.Count(item => item.TargetDialect == SqlAgentToolType.MsSqlServer));
        Assert.Single(Cases, item => item.TargetDialect == SqlAgentToolType.MySQL);
        Assert.DoesNotContain(
            Cases,
            item => item.TargetDialect == SqlAgentToolType.Oracle);
    }

    [Theory]
    [MemberData(nameof(NativeDmlMatrix))]
    public void NativeDmlMatrix_UsesExplicitCapabilityProofsAndRenders(
        string name,
        string sql,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetDialect,
        Version? sourceVersion,
        Version? targetVersion,
        string assuranceName,
        SqlStatementKind expectedKind,
        bool returnsRows,
        string renderedFragments)
    {
        var sourceProfile = sourceVersion is null
            ? null
            : new SqlProviderCapabilityProfile(
                sourceDialect,
                ServerVersion: sourceVersion);
        var targetProfile = targetVersion is null
            ? null
            : new SqlProviderCapabilityProfile(
                targetDialect,
                ServerVersion: targetVersion);
        var parsed = CoreSqlTextParser.ParseDml(
            sql,
            sourceDialect,
            sourceProfile);
        var assurance = Assurance(assuranceName);

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            targetDialect,
            new SqlPlanValidationContext(
                "dialect-native-dml-capability-matrix-v1"),
            targetProfile: targetProfile,
            conflictTargetAssurance: assurance);

        Assert.Equal(expectedKind, command.Kind);
        Assert.Equal(returnsRows, command.ReturnsRows);
        Assert.False(string.IsNullOrWhiteSpace(command.PlanFingerprint), name);

        foreach (var fragment in renderedFragments.Split(
                     ';',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Assert.Contains(
                fragment,
                command.Sql,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    private static DmlConflictTargetAssurance? Assurance(string name) =>
        Enum.Parse<AssuranceKind>(name) switch
        {
            AssuranceKind.None => null,
            AssuranceKind.PrimaryKeyId =>
                DmlConflictTargetAssurance.FromPrimaryKey(["id"]),
            AssuranceKind.SoleUniqueId =>
                DmlConflictTargetAssurance.FromUniqueKey(
                    ["id"],
                    "PRIMARY",
                    isPrimaryKey: true,
                    enforcedUniqueKeyCount: 1,
                    hasUnsupportedEnforcedUniqueKeys: false),
            _ => throw new ArgumentOutOfRangeException(
                nameof(name),
                name,
                "Unknown DML conflict assurance kind.")
        };
}
