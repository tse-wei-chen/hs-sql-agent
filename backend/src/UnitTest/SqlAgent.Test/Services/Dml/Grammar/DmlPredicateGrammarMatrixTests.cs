using System.Text.Json;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Ast;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class DmlPredicateGrammarMatrixTests
{
    private sealed record AssignmentVariant(
        string Sql,
        string RenderedMarker,
        string? SensitiveLiteral);

    private sealed record PredicateVariant(
        string Sql,
        string RenderedMarker,
        bool ReferencesAuditTable);

    private sealed record InsertProjectionVariant(
        string Sql,
        string RenderedMarker,
        string? SensitiveLiteral,
        bool ReferencesProfilesTable);

    private static readonly GrammarVariant<SqlAgentToolType>[] Dialects =
        Enum.GetValues<SqlAgentToolType>()
            .Select(dialect =>
                new GrammarVariant<SqlAgentToolType>(
                    dialect.ToString().ToLowerInvariant(),
                    dialect))
            .ToArray();

    private static readonly GrammarVariant<AssignmentVariant>[] Assignments =
    [
        new(
            "literal",
            new AssignmentVariant(
                "name = 'Alice'",
                "name",
                "Alice")),
        new(
            "arithmetic",
            new AssignmentVariant(
                "score = score + 1",
                "+",
                null)),
        new(
            "case",
            new AssignmentVariant(
                "score = CASE WHEN score < 0 THEN 0 ELSE score END",
                "CASE",
                null)),
        new(
            "coalesce",
            new AssignmentVariant(
                "name = COALESCE(name, 'unknown')",
                "COALESCE(",
                "unknown"))
    ];

    private static readonly GrammarVariant<PredicateVariant>[] Predicates =
    [
        new(
            "equality",
            new PredicateVariant(
                "id = 1",
                "=",
                false)),
        new(
            "boolean-is-null",
            new PredicateVariant(
                "(id = 1 OR owner_id = 2) AND deleted_at IS NULL",
                "IS NULL",
                false)),
        new(
            "in-list",
            new PredicateVariant(
                "id IN (1, 2, 3)",
                " IN ",
                false)),
        new(
            "between",
            new PredicateVariant(
                "score BETWEEN 10 AND 20",
                "BETWEEN",
                false)),
        new(
            "exists-subquery",
            new PredicateVariant(
                "EXISTS (SELECT id FROM audit_log a WHERE a.user_id = users.id AND a.active = 1)",
                "EXISTS",
                true)),
        new(
            "in-subquery",
            new PredicateVariant(
                "id IN (SELECT user_id FROM audit_log WHERE active = 1)",
                " IN ",
                true))
    ];

    private static readonly GrammarVariant<InsertProjectionVariant>[] InsertProjections =
    [
        new(
            "plain",
            new InsertProjectionVariant(
                "s.id, s.name",
                "SELECT",
                null,
                false)),
        new(
            "arithmetic",
            new InsertProjectionVariant(
                "s.id + 1 AS id, s.name",
                "+",
                null,
                false)),
        new(
            "coalesce",
            new InsertProjectionVariant(
                "s.id, COALESCE(s.name, 'unknown') AS name",
                "COALESCE(",
                "unknown",
                false)),
        new(
            "case",
            new InsertProjectionVariant(
                "s.id, CASE WHEN s.name IS NULL THEN 'unknown' ELSE s.name END AS name",
                "CASE",
                "unknown",
                false)),
        new(
            "scalar-subquery",
            new InsertProjectionVariant(
                "s.id, (SELECT p.name FROM profiles p WHERE p.id = s.id) AS name",
                "profiles",
                null,
                true))
    ];

    private static readonly GrammarVariant<PredicateVariant>[] InsertSourcePredicates =
    [
        new(
            "none",
            new PredicateVariant(
                "",
                "staged_users",
                false)),
        new(
            "equality",
            new PredicateVariant(
                " WHERE s.active = 1",
                "WHERE",
                false)),
        new(
            "boolean-is-null",
            new PredicateVariant(
                " WHERE (s.active = 1 OR s.active = 2) AND s.deleted_at IS NULL",
                "IS NULL",
                false)),
        new(
            "in-list",
            new PredicateVariant(
                " WHERE s.id IN (1, 2, 3)",
                " IN ",
                false)),
        new(
            "between",
            new PredicateVariant(
                " WHERE s.score BETWEEN 10 AND 20",
                "BETWEEN",
                false)),
        new(
            "exists-subquery",
            new PredicateVariant(
                " WHERE EXISTS (SELECT id FROM audit_log a WHERE a.user_id = s.id AND a.active = 1)",
                "EXISTS",
                true))
    ];

    public static IEnumerable<object?[]> UpdatePredicateGrammarMatrix()
    {
        foreach (var (dialect, assignment, predicate) in
                 SyntaxGrammarMatrix.Product(
                     Dialects,
                     Assignments,
                     Predicates))
        {
            yield return
            [
                SyntaxGrammarMatrix.CaseName(
                    "dml-update",
                    dialect.Name,
                    assignment.Name,
                    predicate.Name),
                dialect.Value,
                $"UPDATE users SET {assignment.Value.Sql} WHERE {predicate.Value.Sql}",
                assignment.Value.RenderedMarker,
                assignment.Value.SensitiveLiteral,
                predicate.Value.RenderedMarker,
                predicate.Value.ReferencesAuditTable
            ];
        }
    }

    public static IEnumerable<object?[]> DeletePredicateGrammarMatrix()
    {
        foreach (var (dialect, predicate) in
                 SyntaxGrammarMatrix.Product(Dialects, Predicates))
        {
            yield return
            [
                SyntaxGrammarMatrix.CaseName(
                    "dml-delete",
                    dialect.Name,
                    predicate.Name),
                dialect.Value,
                $"DELETE FROM users WHERE {predicate.Value.Sql}",
                predicate.Value.RenderedMarker,
                predicate.Value.ReferencesAuditTable
            ];
        }
    }

    public static IEnumerable<object?[]> InsertSelectGrammarMatrix()
    {
        foreach (var (dialect, projection, predicate) in
                 SyntaxGrammarMatrix.Product(
                     Dialects,
                     InsertProjections,
                     InsertSourcePredicates))
        {
            yield return
            [
                SyntaxGrammarMatrix.CaseName(
                    "dml-insert-select",
                    dialect.Name,
                    projection.Name,
                    predicate.Name),
                dialect.Value,
                "INSERT INTO users (id, name) SELECT " +
                projection.Value.Sql +
                " FROM staged_users s" +
                predicate.Value.Sql,
                projection.Value.RenderedMarker,
                projection.Value.SensitiveLiteral,
                projection.Value.ReferencesProfilesTable,
                predicate.Value.RenderedMarker,
                predicate.Value.ReferencesAuditTable
            ];
        }
    }

    [Fact]
    public void DmlPredicateGrammarMatrix_IsCartesianAndCollisionFree()
    {
        var updates = UpdatePredicateGrammarMatrix().ToArray();
        var deletes = DeletePredicateGrammarMatrix().ToArray();
        var inserts = InsertSelectGrammarMatrix().ToArray();

        Assert.Equal(144, updates.Length);
        Assert.Equal(36, deletes.Length);
        Assert.Equal(180, inserts.Length);
        var all = updates.Concat(deletes).Concat(inserts).ToArray();

        Assert.Equal(
            360,
            all.Select(item => Assert.IsType<string>(item[0]))
                .Distinct(StringComparer.Ordinal)
                .Count());
        Assert.Equal(
            360,
            all.Select(item =>
                    $"{Assert.IsType<SqlAgentToolType>(item[1])}|" +
                    Assert.IsType<string>(item[2]))
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    [Theory]
    [MemberData(nameof(UpdatePredicateGrammarMatrix))]
    public void UpdatePredicateGrammarMatrix_ParsesBindsValidatesCompilesAndRenders(
        string name,
        SqlAgentToolType dialect,
        string sql,
        string assignmentRenderedMarker,
        string? sensitiveLiteral,
        string predicateRenderedMarker,
        bool referencesAuditTable)
    {
        var parsed = CoreSqlTextParser.ParseDml(sql, dialect);
        Assert.IsType<UpdateStatement>(parsed.Statement);

        var command = Compile(
            sql,
            dialect);

        Assert.Equal(SqlStatementKind.Update, command.Kind);
        Assert.False(string.IsNullOrWhiteSpace(command.Sql), name);
        Assert.Contains("UPDATE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SET", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WHERE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            assignmentRenderedMarker,
            command.Sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            predicateRenderedMarker,
            command.Sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(command.Parameters);

        if (referencesAuditTable)
            Assert.Contains("audit_log", command.Sql, StringComparison.OrdinalIgnoreCase);

        if (sensitiveLiteral is not null)
        {
            Assert.DoesNotContain(
                sensitiveLiteral,
                command.Sql,
                StringComparison.Ordinal);
            Assert.Contains(
                command.Parameters,
                parameter => Equals(parameter.Value, sensitiveLiteral));
        }
    }

    [Theory]
    [MemberData(nameof(DeletePredicateGrammarMatrix))]
    public void DeletePredicateGrammarMatrix_ParsesBindsValidatesCompilesAndRenders(
        string name,
        SqlAgentToolType dialect,
        string sql,
        string predicateRenderedMarker,
        bool referencesAuditTable)
    {
        var parsed = CoreSqlTextParser.ParseDml(sql, dialect);
        Assert.IsType<DeleteStatement>(parsed.Statement);

        var command = Compile(
            sql,
            dialect);

        Assert.Equal(SqlStatementKind.Delete, command.Kind);
        Assert.False(string.IsNullOrWhiteSpace(command.Sql), name);
        Assert.Contains("DELETE FROM", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WHERE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            predicateRenderedMarker,
            command.Sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(command.Parameters);

        if (referencesAuditTable)
            Assert.Contains("audit_log", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(InsertSelectGrammarMatrix))]
    public void InsertSelectGrammarMatrix_ParsesBindsValidatesCompilesAndRenders(
        string name,
        SqlAgentToolType dialect,
        string sql,
        string projectionRenderedMarker,
        string? sensitiveLiteral,
        bool referencesProfilesTable,
        string predicateRenderedMarker,
        bool referencesAuditTable)
    {
        var parsed = CoreSqlTextParser.ParseDml(sql, dialect);
        var insert = Assert.IsType<InsertStatement>(parsed.Statement);
        Assert.IsType<InsertQuerySource>(insert.Source);

        var command = Compile(
            sql,
            dialect);

        Assert.Equal(SqlStatementKind.Insert, command.Kind);
        Assert.False(string.IsNullOrWhiteSpace(command.Sql), name);
        Assert.Contains("INSERT INTO", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SELECT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("staged_users", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            projectionRenderedMarker,
            command.Sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            predicateRenderedMarker,
            command.Sql,
            StringComparison.OrdinalIgnoreCase);

        if (referencesProfilesTable)
            Assert.Contains("profiles", command.Sql, StringComparison.OrdinalIgnoreCase);

        if (referencesAuditTable)
            Assert.Contains("audit_log", command.Sql, StringComparison.OrdinalIgnoreCase);

        if (sensitiveLiteral is not null)
        {
            Assert.DoesNotContain(
                sensitiveLiteral,
                command.Sql,
                StringComparison.Ordinal);
            Assert.Contains(
                command.Parameters,
                parameter => Equals(parameter.Value, sensitiveLiteral));
        }
    }

    [Fact]
    public void GeneratedDmlPredicateParityCorpus_MatchesCartesianFloor()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "SyntaxCorpus",
            "sql-generated-dml-predicate-compatibility-floor.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var cases = document.RootElement.EnumerateArray().ToArray();

        Assert.Equal(360, cases.Length);
        Assert.Equal(
            360,
            cases.Select(item => item.GetProperty("name").GetString())
                .Distinct(StringComparer.Ordinal)
                .Count());

        foreach (var dialect in Enum.GetValues<SqlAgentToolType>())
        {
            Assert.Equal(
                60,
                cases.Count(item =>
                    string.Equals(
                        item.GetProperty("dialect").GetString(),
                        dialect.ToString(),
                        StringComparison.OrdinalIgnoreCase)));
        }
    }

    private static CompiledSqlCommand Compile(
        string sql,
        SqlAgentToolType dialect) =>
        SqlCoreFacade.CompileDml(
            sql,
            dialect,
            dialect,
            new SqlPlanValidationContext(
                "dml-combinatorial-predicate-matrix-v1",
                new HashSet<string>(
                    ["users", "audit_log", "staged_users", "profiles"],
                    StringComparer.OrdinalIgnoreCase)));
}
