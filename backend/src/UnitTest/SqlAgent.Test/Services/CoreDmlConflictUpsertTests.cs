using System.Collections.Immutable;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreDmlConflictUpsertTests
{
    [Fact]
    public void Parse_PostgresConflictUpdate_ModelsExplicitTargetAndProposedAssignments()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "INSERT INTO users (id, name, email) VALUES (1, 'Alice', 'a@example.com') " +
            "ON CONFLICT (id) DO UPDATE SET name = excluded.name, email = excluded.email",
            SqlAgentToolType.Postgres);
        var insert = Assert.IsType<InsertStatement>(parsed.Statement);
        var conflict = Assert.IsType<InsertConflictClause>(insert.Conflict);

        Assert.Equal(InsertConflictActionKind.UpdateProposedValues, conflict.Action);
        Assert.Equal(["id"], conflict.TargetColumns.Select(IdentifierText).ToArray());
        Assert.Equal(["name", "email"], conflict.Assignments.Select(x => IdentifierText(x.Column)).ToArray());
        Assert.Equal(["name", "email"], conflict.Assignments.Select(x => IdentifierText(x.ProposedColumn)).ToArray());
    }

    [Fact]
    public void Parse_PostgresConflictUpdate_ModelsDeterministicArithmeticValue()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "INSERT INTO inventory (id, quantity) VALUES (1, 3) " +
            "ON CONFLICT (id) DO UPDATE SET quantity = quantity + excluded.quantity * 2",
            SqlAgentToolType.Postgres);
        var insert = Assert.IsType<InsertStatement>(parsed.Statement);
        var conflict = Assert.IsType<InsertConflictClause>(insert.Conflict);
        var assignment = Assert.Single(conflict.Assignments);

        Assert.Equal("quantity", IdentifierText(assignment.Column));
        Assert.Null(assignment.ProposedColumn);
        Assert.IsType<BinaryExpr>(assignment.Value);
    }

    [Fact]
    public void Compile_PostgresConflictUpdate_DeterministicArithmetic_IsParameterized()
    {
        var command = CompileRaw(
            "INSERT INTO inventory (id, quantity) VALUES (1, 3) " +
            "ON CONFLICT (id) DO UPDATE SET quantity = quantity + excluded.quantity * 2",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres);

        Assert.Contains("EXCLUDED.\"quantity\"", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"quantity\" +", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, command.Parameters.Length);
        Assert.DoesNotContain(" * 2", command.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_SqliteConflictUpdate_DeterministicArithmetic_RequiresNativeVersionProof()
    {
        var profile = new SqlProviderCapabilityProfile(
            SqlAgentToolType.Sqlite,
            ServerVersion: new Version(3, 24));
        var command = CompileRaw(
            "INSERT INTO inventory (id, quantity) VALUES (1, 3) " +
            "ON CONFLICT (id) DO UPDATE SET quantity = quantity + excluded.quantity * 2",
            SqlAgentToolType.Sqlite,
            SqlAgentToolType.Sqlite,
            profile,
            profile);

        Assert.Contains("ON CONFLICT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("excluded", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, command.Parameters.Length);
    }

    [Fact]
    public void Compile_RichConflictUpdate_CrossProvider_RemainsFailClosed()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "INSERT INTO inventory (id, quantity) VALUES (1, 3) " +
            "ON CONFLICT (id) DO UPDATE SET quantity = quantity + excluded.quantity",
            SqlAgentToolType.Postgres);
        var sqlite = new SqlProviderCapabilityProfile(
            SqlAgentToolType.Sqlite,
            ServerVersion: new Version(3, 24));

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Sqlite,
                new SqlPlanValidationContext("policy-v1"),
                targetProfile: sqlite));

        Assert.Contains("native-only", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("conflict-update", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_ConflictUpdateFunction_RemainsFailClosed()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseDml(
                "INSERT INTO users (id, name) VALUES (1, 'Alice') " +
                "ON CONFLICT (id) DO UPDATE SET name = LOWER(excluded.name)",
                SqlAgentToolType.Postgres));

        Assert.Contains("deterministic scalar", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("functions", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_PostgresDoNothing_EmitsExplicitConflictTarget()
    {
        var command = CompileRaw(
            "INSERT INTO users (id, name) VALUES (1, 'Alice') ON CONFLICT (id) DO NOTHING",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres);

        Assert.Equal(SqlStatementKind.Insert, command.Kind);
        Assert.Contains("ON CONFLICT (\"id\") DO NOTHING", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(command.PlanFingerprint));
    }

    [Fact]
    public void Compile_PostgresConflictUpdateWithReturning_PreservesClauseOrder()
    {
        var command = CompileRaw(
            "INSERT INTO users (id, name) VALUES (1, 'Alice') " +
            "ON CONFLICT (id) DO UPDATE SET name = excluded.name RETURNING id, name",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres);

        var conflictIndex = command.Sql.IndexOf("ON CONFLICT", StringComparison.OrdinalIgnoreCase);
        var returningIndex = command.Sql.IndexOf("RETURNING", StringComparison.OrdinalIgnoreCase);
        Assert.True(conflictIndex > 0);
        Assert.True(returningIndex > conflictIndex);
        Assert.Contains("\"name\" = EXCLUDED.\"name\"", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.True(command.ReturnsRows);
    }

    [Fact]
    public void Compile_SqliteUpsert_RequiresExplicitSourceAndTargetVersion324()
    {
        const string sql = "INSERT INTO users (id, name) VALUES (1, 'Alice') " +
                           "ON CONFLICT (id) DO UPDATE SET name = excluded.name";

        var sourceError = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.Sqlite));
        Assert.Contains("3.24", sourceError.Message, StringComparison.OrdinalIgnoreCase);

        var profile = new SqlProviderCapabilityProfile(
            SqlAgentToolType.Sqlite,
            ServerVersion: new Version(3, 24));
        var parsed = CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.Sqlite, profile);
        var targetError = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Sqlite,
                new SqlPlanValidationContext("policy-v1")));
        Assert.Contains("3.24", targetError.Message, StringComparison.OrdinalIgnoreCase);

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Sqlite,
            new SqlPlanValidationContext("policy-v1"),
            targetProfile: profile);
        Assert.Contains("ON CONFLICT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("excluded", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_MySqlNativeOnDuplicateKey_FailsClosedWithExplicitReason()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseDml(
                "INSERT INTO users (id, name) VALUES (1, 'Alice') " +
                "ON DUPLICATE KEY UPDATE name = name",
                SqlAgentToolType.MySQL));

        Assert.Contains("ON DUPLICATE KEY", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("conflict target", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL, "unique")]
    [InlineData(SqlAgentToolType.MsSqlServer, "MERGE")]
    [InlineData(SqlAgentToolType.Oracle, "MERGE")]
    public void Compile_ConflictUpsertToNonEquivalentTarget_FailsClosed(
        SqlAgentToolType targetProvider,
        string expectedMessage)
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "INSERT INTO users (id, name) VALUES (1, 'Alice') " +
            "ON CONFLICT (id) DO UPDATE SET name = excluded.name",
            SqlAgentToolType.Postgres);

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                targetProvider,
                new SqlPlanValidationContext("policy-v1")));

        Assert.Contains(expectedMessage, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_ConflictUpdateArbitraryExpression_FailsClosed()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseDml(
                "INSERT INTO users (id, name) VALUES (1, 'Alice') " +
                "ON CONFLICT (id) DO UPDATE SET name = excluded.name || '!'",
                SqlAgentToolType.Postgres));

        Assert.Contains("conflict clause", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_ConflictTargetMustBeExplicitInsertColumn()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "INSERT INTO users (name) VALUES ('Alice') ON CONFLICT (id) DO NOTHING",
            SqlAgentToolType.Postgres);

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("policy-v1")));

        Assert.Contains("explicitly present", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_ProposedUpdateSourceMustBeExplicitInsertColumn()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "INSERT INTO users (id) VALUES (1) " +
            "ON CONFLICT (id) DO UPDATE SET name = excluded.name",
            SqlAgentToolType.Postgres);

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("policy-v1")));

        Assert.Contains("Proposed-row", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("INSERT column list", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_MultiRowConflictUpdate_RemainsFailClosedWithoutUniqueIndexMetadata()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "INSERT INTO users (id, name) VALUES (1, 'Alice'), (2, 'Bob') " +
            "ON CONFLICT (id) DO UPDATE SET name = excluded.name",
            SqlAgentToolType.Postgres);

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("policy-v1")));

        Assert.Contains("exactly one", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("assurance", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_MultiRowConflictDoNothing_RemainsPortable()
    {
        var command = CompileRaw(
            "INSERT INTO users (id, name) VALUES (1, 'Alice'), (1, 'Bob') " +
            "ON CONFLICT (id) DO NOTHING",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres);

        Assert.Contains("ON CONFLICT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DO NOTHING", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4, command.Parameters.Length);
    }

    [Fact]
    public void Compile_PostgresInsertSelectConflictDoNothing_IsSupported()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "INSERT INTO users (id, name) SELECT id, name FROM staged_users",
            SqlAgentToolType.Postgres);
        var insert = Assert.IsType<InsertStatement>(parsed.Statement);
        var conflict = new InsertConflictClause(
            ImmutableArray.Create(Id("id")),
            InsertConflictActionKind.DoNothing,
            ImmutableArray<InsertConflictAssignment>.Empty,
            SourceSpan.Unknown);
        insert.Conflict = conflict;
        parsed.Statement = insert;
        var structured = parsed;

        var command = CoreDmlCompiler.CreateDefault().Compile(
            structured,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("policy-v1"));

        Assert.Contains("SELECT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ON CONFLICT (\"id\") DO NOTHING", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(command.PlanFingerprint));
    }

    [Fact]
    public void Compile_PostgresInsertSelectConflictUpdate_RemainsFailClosedWithoutCardinalityAssurance()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "INSERT INTO users (id, name) SELECT id, name FROM staged_users",
            SqlAgentToolType.Postgres);
        var insert = Assert.IsType<InsertStatement>(parsed.Statement);
        var conflict = new InsertConflictClause(
            ImmutableArray.Create(Id("id")),
            InsertConflictActionKind.UpdateProposedValues,
            ImmutableArray.Create(new InsertConflictAssignment(Id("name"), Id("name"), SourceSpan.Unknown)),
            SourceSpan.Unknown);
        insert.Conflict = conflict;
        parsed.Statement = insert;
        var structured = parsed;

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                structured,
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("policy-v1")));

        Assert.Contains("cardinality", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fail-closed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static CompiledSqlCommand CompileRaw(
        string sql,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetProvider,
        SqlProviderCapabilityProfile? sourceProfile = null,
        SqlProviderCapabilityProfile? targetProfile = null)
    {
        var parsed = CoreSqlTextParser.ParseDml(sql, sourceDialect, sourceProfile);
        return CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            targetProvider,
            new SqlPlanValidationContext("policy-v1"),
            targetProfile: targetProfile);
    }

    private static SqlIdentifier Id(string value) => SqlIdentifier.Unquoted(value, SourceSpan.Unknown);

    private static string IdentifierText(SqlIdentifier identifier) =>
        string.Join('.', identifier.Parts.Select(part => part.Value));
}
