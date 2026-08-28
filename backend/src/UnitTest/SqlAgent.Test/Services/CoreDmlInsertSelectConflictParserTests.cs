using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreDmlInsertSelectConflictParserTests
{
    [Fact]
    public void Parse_PostgresInsertSelectConflictDoNothing_ModelsConflict()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "INSERT INTO users (id, name) SELECT id, name FROM staged_users ON CONFLICT (id) DO NOTHING",
            SqlAgentToolType.Postgres);

        var insert = Assert.IsType<InsertStatement>(parsed.Statement);
        Assert.IsType<InsertQuerySource>(insert.Source);
        var conflict = Assert.IsType<InsertConflictClause>(insert.Conflict);
        Assert.Equal(InsertConflictActionKind.DoNothing, conflict.Action);
        Assert.Equal(["id"], conflict.TargetColumns.Select(IdentifierText).ToArray());
    }

    [Fact]
    public void Parse_PostgresInsertSelectJoinOn_DoesNotConfuseJoinPredicateWithConflictClause()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "INSERT INTO users (id, name) SELECT s.id, s.name FROM staged_users s JOIN tenants t ON t.id = s.tenant_id ON CONFLICT (id) DO NOTHING",
            SqlAgentToolType.Postgres);

        var insert = Assert.IsType<InsertStatement>(parsed.Statement);
        Assert.IsType<InsertQuerySource>(insert.Source);
        var conflict = Assert.IsType<InsertConflictClause>(insert.Conflict);
        Assert.Equal(InsertConflictActionKind.DoNothing, conflict.Action);
    }

    [Fact]
    public void Compile_PostgresRawInsertSelectConflictUpdate_WithSourceUniquenessAssurance_IsSupported()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "INSERT INTO users (id, name) SELECT id, name FROM staged_users ON CONFLICT (id) DO UPDATE SET name = excluded.name",
            SqlAgentToolType.Postgres);
        var assurance = new DmlConflictTargetAssurance(System.Collections.Immutable.ImmutableArray<string>.Empty)
            .WithSourceRowsUniqueByInsertColumns(["id"]);

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("policy-v1"),
            conflictTargetAssurance: assurance);

        Assert.Contains("ON CONFLICT (\"id\") DO UPDATE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"name\" = EXCLUDED.\"name\"", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    private static string IdentifierText(SqlIdentifier identifier) =>
        string.Join('.', identifier.Parts.Select(part => part.Value));
}
