using System.Collections.Immutable;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreDmlInsertSelectConflictAssuranceTests
{
    [Fact]
    public void Compile_PostgresInsertSelectConflictUpdate_WithExactSourceUniquenessAssurance_IsSupported()
    {
        var parsed = BuildInsertSelectConflictUpdate();
        var assurance = new DmlConflictTargetAssurance(ImmutableArray<string>.Empty)
            .WithSourceRowsUniqueByInsertColumns(["id"]);

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("policy-v1"),
            conflictTargetAssurance: assurance);

        Assert.Contains("SELECT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ON CONFLICT (\"id\") DO UPDATE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"name\" = EXCLUDED.\"name\"", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(command.PlanFingerprint));
    }

    [Fact]
    public void Compile_PostgresInsertSelectConflictUpdate_WithMismatchedSourceUniquenessAssurance_FailsClosed()
    {
        var parsed = BuildInsertSelectConflictUpdate();
        var assurance = new DmlConflictTargetAssurance(ImmutableArray<string>.Empty)
            .WithSourceRowsUniqueByInsertColumns(["name"]);

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("policy-v1"),
                conflictTargetAssurance: assurance));

        Assert.Contains("match", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("conflict target", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ParsedStatement BuildInsertSelectConflictUpdate()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "INSERT INTO users (id, name) SELECT id, name FROM staged_users",
            SqlAgentToolType.Postgres);
        var insert = Assert.IsType<InsertStatement>(parsed.Statement);
        var conflict = new InsertConflictClause(
            ImmutableArray.Create(Id("id")),
            InsertConflictActionKind.UpdateProposedValues,
            ImmutableArray.Create(
                new InsertConflictAssignment(Id("name"), Id("name"), SourceSpan.Unknown)),
            SourceSpan.Unknown);
        insert.Conflict = conflict;
        parsed.Statement = insert;
        return parsed;
    }

    private static SqlIdentifier Id(string value) =>
        SqlIdentifier.Unquoted(value, SourceSpan.Unknown);
}
