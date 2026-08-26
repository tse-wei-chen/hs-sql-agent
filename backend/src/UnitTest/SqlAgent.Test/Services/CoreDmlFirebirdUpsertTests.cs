using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreDmlFirebirdUpsertTests
{
    [Fact]
    public void Parse_FirebirdUpdateOrInsert_ModelsExplicitMatchingAndFullProposedRowUpdate()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "UPDATE OR INSERT INTO users (id, name) VALUES (1, 'Alice') MATCHING (id)",
            SqlAgentToolType.Firebird);
        var insert = Assert.IsType<InsertStatement>(parsed.Statement);
        var conflict = Assert.IsType<InsertConflictClause>(insert.Conflict);

        Assert.Equal(InsertConflictActionKind.UpdateProposedValues, conflict.Action);
        Assert.Equal(["id"], conflict.TargetColumns.Select(IdentifierText).ToArray());
        Assert.Equal(["id", "name"], conflict.Assignments.Select(x => IdentifierText(x.Column)).ToArray());
        Assert.Equal(["id", "name"], conflict.Assignments.Select(x => IdentifierText(x.ProposedColumn)).ToArray());
    }

    [Fact]
    public void Compile_FirebirdSourceToPostgres_CanonicalizesNativeUpdateOrInsert()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "UPDATE OR INSERT INTO users (id, name) VALUES (1, 'Alice') MATCHING (id)",
            SqlAgentToolType.Firebird);

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("policy-v1"));

        Assert.Contains("ON CONFLICT (\"id\") DO UPDATE SET", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"id\" = EXCLUDED.\"id\"", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"name\" = EXCLUDED.\"name\"", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_FirebirdTarget_WithPrimaryKeyAssurance_UsesNativeUpdateOrInsertMatching()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "INSERT INTO users (id, name) VALUES (1, 'Alice') " +
            "ON CONFLICT (id) DO UPDATE SET id = excluded.id, name = excluded.name",
            SqlAgentToolType.Postgres);

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Firebird,
            new SqlPlanValidationContext("policy-v1"),
            conflictTargetAssurance: DmlConflictTargetAssurance.FromPrimaryKey(["id"]));

        Assert.StartsWith("UPDATE OR INSERT INTO", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MATCHING (\"id\")", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ON CONFLICT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(command.PlanFingerprint));
    }

    [Fact]
    public void Compile_FirebirdTarget_WithoutPrimaryKeyAssurance_FailsClosed()
    {
        var parsed = FullUpdateParsed();

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Firebird,
                new SqlPlanValidationContext("policy-v1")));

        Assert.Contains("primary key", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("assurance", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_FirebirdTarget_ConflictTargetMustEqualCompletePrimaryKey()
    {
        var parsed = FullUpdateParsed();

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Firebird,
                new SqlPlanValidationContext("policy-v1"),
                conflictTargetAssurance: DmlConflictTargetAssurance.FromPrimaryKey(["tenant_id", "id"])));

        Assert.Contains("complete resolved primary key", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_FirebirdTarget_PartialConflictUpdate_FailsClosed()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "INSERT INTO users (id, name) VALUES (1, 'Alice') " +
            "ON CONFLICT (id) DO UPDATE SET name = excluded.name",
            SqlAgentToolType.Postgres);

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Firebird,
                new SqlPlanValidationContext("policy-v1"),
                conflictTargetAssurance: DmlConflictTargetAssurance.FromPrimaryKey(["id"])));

        Assert.Contains("every supplied INSERT column", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_FirebirdTarget_DoNothing_RemainsFailClosed()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "INSERT INTO users (id, name) VALUES (1, 'Alice') ON CONFLICT (id) DO NOTHING",
            SqlAgentToolType.Postgres);

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Firebird,
                new SqlPlanValidationContext("policy-v1"),
                conflictTargetAssurance: DmlConflictTargetAssurance.FromPrimaryKey(["id"])));

        Assert.Contains("DO NOTHING", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_FirebirdUpdateOrInsertWithReturning_PreservesClauseOrder()
    {
        var profile = new SqlProviderCapabilityProfile(
            SqlAgentToolType.Firebird,
            ServerVersion: new Version(5, 0));
        var parsed = CoreSqlTextParser.ParseDml(
            "UPDATE OR INSERT INTO users (id, name) VALUES (1, 'Alice') MATCHING (id) RETURNING id",
            SqlAgentToolType.Firebird,
            profile);

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Firebird,
            new SqlPlanValidationContext("policy-v1"),
            targetProfile: profile,
            conflictTargetAssurance: DmlConflictTargetAssurance.FromPrimaryKey(["id"]));

        var matchingIndex = command.Sql.IndexOf("MATCHING", StringComparison.OrdinalIgnoreCase);
        var returningIndex = command.Sql.IndexOf("RETURNING", StringComparison.OrdinalIgnoreCase);
        Assert.True(matchingIndex > 0);
        Assert.True(returningIndex > matchingIndex);
        Assert.True(command.ReturnsRows);
    }

    [Fact]
    public void Parse_FirebirdUpdateOrInsert_RequiresExplicitMatching()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseDml(
                "UPDATE OR INSERT INTO users (id, name) VALUES (1, 'Alice')",
                SqlAgentToolType.Firebird));

        Assert.Contains("explicit MATCHING", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ParsedStatement FullUpdateParsed() => CoreSqlTextParser.ParseDml(
        "INSERT INTO users (id, name) VALUES (1, 'Alice') " +
        "ON CONFLICT (id) DO UPDATE SET id = excluded.id, name = excluded.name",
        SqlAgentToolType.Postgres);

    private static string IdentifierText(SqlIdentifier identifier) =>
        string.Join('.', identifier.Parts.Select(part => part.Value));
}
