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

    public static IEnumerable<object[]> UpdatePredicateGrammarMatrix()
    {
        foreach (var (dialect, assignment, predicate, _) in
                 SyntaxGrammarMatrix.Product(
                     Dialects,
                     Assignments,
                     Predicates,
                     SingleTail))
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

    public static IEnumerable<object[]> DeletePredicateGrammarMatrix()
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

    [Fact]
    public void DmlPredicateGrammarMatrix_IsCartesianAndCollisionFree()
    {
        var updates = UpdatePredicateGrammarMatrix().ToArray();
        var deletes = DeletePredicateGrammarMatrix().ToArray();

        Assert.Equal(144, updates.Length);
        Assert.Equal(36, deletes.Length);
        Assert.Equal(
            180,
            updates
                .Concat(deletes)
                .Select(item => Assert.IsType<string>(item[0]))
                .Distinct(StringComparer.Ordinal)
                .Count());
        Assert.Equal(
            180,
            updates
                .Concat(deletes)
                .Select(item => Assert.IsType<string>(item[2]))
                .Select((sql, index) =>
                    Assert.IsType<SqlAgentToolType>(
                        updates.Concat(deletes).ElementAt(index)[1]) +
                    "|" +
                    sql)
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

    [Fact]
    public void GeneratedDmlPredicateParityCorpus_MatchesCartesianFloor()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "SyntaxCorpus",
            "sql-generated-dml-predicate-compatibility-floor.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var cases = document.RootElement.EnumerateArray().ToArray();

        Assert.Equal(180, cases.Length);
        Assert.Equal(
            180,
            cases.Select(item => item.GetProperty("name").GetString())
                .Distinct(StringComparer.Ordinal)
                .Count());

        foreach (var dialect in Enum.GetValues<SqlAgentToolType>())
        {
            Assert.Equal(
                30,
                cases.Count(item =>
                    string.Equals(
                        item.GetProperty("dialect").GetString(),
                        dialect.ToString(),
                        StringComparison.OrdinalIgnoreCase)));
        }
    }

    private static readonly GrammarVariant<int>[] SingleTail =
    [
        new("base", 0)
    ];

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
                    ["users", "audit_log"],
                    StringComparer.OrdinalIgnoreCase)));
}
