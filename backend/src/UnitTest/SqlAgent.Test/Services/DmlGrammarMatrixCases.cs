using HsSqlAgent.SqlCore.Enums;

namespace SqlAgent.Test.Services;

internal sealed record DmlGrammarCase(
    string Name,
    SqlAgentToolType Dialect,
    string Sql,
    SqlStatementKind ExpectedKind,
    string RenderedFragments,
    string AllowedTables,
    object? ExpectedParameter);

internal static class DmlGrammarMatrixCases
{
    private sealed record QuotedCase(
        SqlAgentToolType Dialect,
        string Sql,
        string RenderedFragments,
        string AllowedTables);

    private static readonly QuotedCase[] QuotedCases =
    [
        new(
            SqlAgentToolType.Postgres,
            "UPDATE \"users\" SET \"name\" = 'Ada' WHERE \"id\" = 7",
            "UPDATE;\"users\";\"name\";\"id\"",
            "users"),
        new(
            SqlAgentToolType.MySQL,
            "UPDATE `users` SET `name` = 'Ada' WHERE `id` = 7",
            "UPDATE;`users`;`name`;`id`",
            "users"),
        new(
            SqlAgentToolType.MsSqlServer,
            "UPDATE [users] SET [name] = 'Ada' WHERE [id] = 7",
            "UPDATE;[users];[name];[id]",
            "users"),
        new(
            SqlAgentToolType.Sqlite,
            "UPDATE \"users\" SET \"name\" = 'Ada' WHERE \"id\" = 7",
            "UPDATE;\"users\";\"name\";\"id\"",
            "users"),
        new(
            SqlAgentToolType.Oracle,
            "UPDATE \"USERS\" SET \"NAME\" = 'Ada' WHERE \"ID\" = 7",
            "UPDATE;\"USERS\";\"NAME\";\"ID\"",
            "USERS"),
        new(
            SqlAgentToolType.Firebird,
            "UPDATE \"USERS\" SET \"NAME\" = 'Ada' WHERE \"ID\" = 7",
            "UPDATE;\"USERS\";\"NAME\";\"ID\"",
            "USERS")
    ];

    public static int ExpectedCaseCount =>
        checked(Enum.GetValues<SqlAgentToolType>().Length * 7);

    public static IEnumerable<DmlGrammarCase> All()
    {
        foreach (var dialect in Enum.GetValues<SqlAgentToolType>())
        {
            var prefix = dialect.ToString();

            yield return new DmlGrammarCase(
                $"{prefix}__insert-explicit-values",
                dialect,
                "INSERT INTO users (id, name) VALUES (1, 'Alice')",
                SqlStatementKind.Insert,
                "INSERT INTO;users",
                "users",
                "Alice");

            yield return new DmlGrammarCase(
                $"{prefix}__insert-implicit-values",
                dialect,
                "INSERT INTO users VALUES (1, 'Alice')",
                SqlStatementKind.Insert,
                "INSERT INTO;users",
                "users",
                "Alice");

            yield return new DmlGrammarCase(
                $"{prefix}__insert-select",
                dialect,
                "INSERT INTO users (id, name) SELECT id, name FROM staged_users",
                SqlStatementKind.Insert,
                "INSERT INTO;SELECT;staged_users",
                "users,staged_users",
                null);

            yield return new DmlGrammarCase(
                $"{prefix}__update-basic",
                dialect,
                "UPDATE users SET name = 'Alice' WHERE id = 1",
                SqlStatementKind.Update,
                "UPDATE;SET;WHERE;users",
                "users",
                "Alice");

            yield return new DmlGrammarCase(
                $"{prefix}__update-expression",
                dialect,
                "UPDATE users SET score = score + 1 WHERE id = 2",
                SqlStatementKind.Update,
                "UPDATE;SET;WHERE;users",
                "users",
                1);

            yield return new DmlGrammarCase(
                $"{prefix}__delete-basic",
                dialect,
                "DELETE FROM users WHERE id = 3",
                SqlStatementKind.Delete,
                "DELETE FROM;WHERE;users",
                "users",
                3);

            var quoted = QuotedCases.Single(item => item.Dialect == dialect);
            yield return new DmlGrammarCase(
                $"{prefix}__quoted-identifiers",
                dialect,
                quoted.Sql,
                SqlStatementKind.Update,
                quoted.RenderedFragments,
                quoted.AllowedTables,
                "Ada");
        }
    }
}
