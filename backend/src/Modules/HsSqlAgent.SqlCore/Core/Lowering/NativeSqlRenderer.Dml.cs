using System.Collections.Immutable;
using System.Text;

namespace HsSqlAgent.SqlCore.Core.Lowering;

/// <summary>
/// Native INSERT/UPDATE/DELETE rendering. Kept separate from query rendering so DML provider
/// behavior can evolve without growing the main query renderer into a single backend god class.
/// </summary>
public sealed partial class NativeSqlRenderer
{
    private NativeSqlFragment RenderInsert(InsertStatement insert)
    {
        if (insert.Columns.IsDefaultOrEmpty)
            throw new SqlCompilationException("INSERT requires at least one target column.");

        return insert.Source switch
        {
            InsertValuesSource values => RenderInsertValues(insert, values),
            InsertQuerySource query => RenderInsertQuery(insert, query),
            _ => throw new SqlCompilationException(
                "Unsupported INSERT source during native lowering: " +
                insert.Source.GetType().Name)
        };
    }

    private NativeSqlFragment RenderInsertValues(
        InsertStatement insert,
        InsertValuesSource values)
    {
        if (values.Rows.IsDefaultOrEmpty)
            throw new SqlCompilationException("INSERT VALUES requires at least one row.");

        var table = CoreIdentifierSqlRenderer.Render(
            insert.Target.Name,
            Provider,
            allowWildcard: false);
        var columns = insert.Columns
            .Select(column => CoreIdentifierSqlRenderer.Render(
                column,
                Provider,
                allowWildcard: false))
            .ToArray();
        var columnSql = string.Join(", ", columns);

        var rows = new List<NativeSqlFragment>(values.Rows.Length);
        for (var rowIndex = 0; rowIndex < values.Rows.Length; rowIndex++)
        {
            var row = values.Rows[rowIndex];
            if (row.Length != columns.Length)
            {
                throw new SqlCompilationException(
                    "INSERT row " + (rowIndex + 1) + " has " +
                    row.Length + " values but " + columns.Length +
                    " columns were declared.");
            }

            var valuesSql = new List<string>(row.Length);
            var bindings = ImmutableArray.CreateBuilder<object?>();
            foreach (var expression in row)
            {
                var rendered = RenderExpression(expression, dmlContext: true);
                valuesSql.Add(rendered.Sql);
                bindings.AddRange(rendered.Bindings);
            }

            rows.Add(new NativeSqlFragment(
                string.Join(", ", valuesSql),
                bindings.ToImmutable()));
        }

        var allBindings = rows
            .SelectMany(row => row.Bindings)
            .ToImmutableArray();

        if (Provider == SqlAgentToolType.Oracle && rows.Count > 1)
        {
            var sql = new StringBuilder("INSERT ALL");
            foreach (var row in rows)
            {
                sql.Append(" INTO ")
                    .Append(table)
                    .Append(" (")
                    .Append(columnSql)
                    .Append(") VALUES (")
                    .Append(row.Sql)
                    .Append(')');
            }

            sql.Append(" SELECT 1 FROM DUAL");
            return new NativeSqlFragment(sql.ToString(), allBindings);
        }

        if (Provider == SqlAgentToolType.Firebird && rows.Count > 1)
        {
            var sql = "INSERT INTO " + table + " (" + columnSql + ") " +
                string.Join(
                    " UNION ALL ",
                    rows.Select(row =>
                        "SELECT " + row.Sql + " FROM RDB$DATABASE"));
            return new NativeSqlFragment(sql, allBindings);
        }

        return new NativeSqlFragment(
            "INSERT INTO " + table + " (" + columnSql + ") VALUES " +
            string.Join(", ", rows.Select(row => "(" + row.Sql + ")")),
            allBindings);
    }

    private NativeSqlFragment RenderInsertQuery(
        InsertStatement insert,
        InsertQuerySource source)
    {
        var table = CoreIdentifierSqlRenderer.Render(
            insert.Target.Name,
            Provider,
            allowWildcard: false);
        var columns = string.Join(
            ", ",
            insert.Columns.Select(column => CoreIdentifierSqlRenderer.Render(
                column,
                Provider,
                allowWildcard: false)));
        var insertPrefix = "INSERT INTO " + table + " (" + columns + ")";
        var ctes = RootCtes(source.Query);

        if (ctes.IsDefaultOrEmpty)
        {
            var query = RenderStatement(
                source.Query,
                QueryPosition.InsertSelectSource);
            return query with { Sql = insertPrefix + " " + query.Sql };
        }

        var withClause = RenderCtes(ctes);
        var sourceWithoutRootCtes = RemoveRootCtes(source.Query);
        var querySource = RenderStatement(
            sourceWithoutRootCtes,
            QueryPosition.InsertSelectSource);

        var sql = Provider switch
        {
            SqlAgentToolType.Postgres or
            SqlAgentToolType.MsSqlServer or
            SqlAgentToolType.Sqlite =>
                withClause.Sql + " " + insertPrefix + " " + querySource.Sql,
            SqlAgentToolType.MySQL or
            SqlAgentToolType.Oracle or
            SqlAgentToolType.Firebird =>
                insertPrefix + " " + withClause.Sql + " " + querySource.Sql,
            _ => throw new SqlCompilationException(
                "INSERT ... SELECT CTE placement is not declared for provider " +
                Provider + ".")
        };

        return new NativeSqlFragment(
            sql,
            withClause.Bindings
                .Concat(querySource.Bindings)
                .ToImmutableArray());
    }

    private NativeSqlFragment RenderUpdate(UpdateStatement update)
    {
        if (update.Assignments.IsDefaultOrEmpty)
            throw new SqlCompilationException("UPDATE requires at least one assignment.");

        var table = CoreIdentifierSqlRenderer.Render(
            update.Target.Name,
            Provider,
            allowWildcard: false);
        var assignments = new List<string>(update.Assignments.Length);
        var bindings = ImmutableArray.CreateBuilder<object?>();

        foreach (var assignment in update.Assignments)
        {
            var column = CoreIdentifierSqlRenderer.Render(
                assignment.Column,
                Provider,
                allowWildcard: false);
            var value = RenderExpression(
                assignment.Value,
                dmlContext: true);
            assignments.Add(column + " = " + value.Sql);
            bindings.AddRange(value.Bindings);
        }

        var sql = new StringBuilder("UPDATE ")
            .Append(table)
            .Append(" SET ")
            .Append(string.Join(", ", assignments));

        if (!update.From.IsDefaultOrEmpty)
        {
            var sources = update.From.Select(RenderNamedTableSource).ToArray();
            sql.Append(" FROM ")
                .Append(string.Join(", ", sources.Select(source => source.Sql)));
            foreach (var source in sources)
                bindings.AddRange(source.Bindings);
        }

        if (update.Predicate is not null)
        {
            var predicate = RenderPredicateExpression(
                update.Predicate,
                dmlContext: true);
            sql.Append(" WHERE ").Append(predicate.Sql);
            bindings.AddRange(predicate.Bindings);
        }

        return new NativeSqlFragment(sql.ToString(), bindings.ToImmutable());
    }

    private NativeSqlFragment RenderDelete(DeleteStatement delete)
    {
        var table = CoreIdentifierSqlRenderer.Render(
            delete.Target.Name,
            Provider,
            allowWildcard: false);
        var sql = new StringBuilder("DELETE FROM ").Append(table);
        var bindings = ImmutableArray.CreateBuilder<object?>();

        if (!delete.Using.IsDefaultOrEmpty)
        {
            var sources = delete.Using.Select(RenderNamedTableSource).ToArray();
            sql.Append(" USING ")
                .Append(string.Join(", ", sources.Select(source => source.Sql)));
            foreach (var source in sources)
                bindings.AddRange(source.Bindings);
        }

        if (delete.Predicate is not null)
        {
            var predicate = RenderPredicateExpression(
                delete.Predicate,
                dmlContext: true);
            sql.Append(" WHERE ").Append(predicate.Sql);
            bindings.AddRange(predicate.Bindings);
        }

        return new NativeSqlFragment(sql.ToString(), bindings.ToImmutable());
    }
}
