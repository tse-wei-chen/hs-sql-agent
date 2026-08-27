namespace HsSqlAgent.SqlCore.Core.Analysis;

/// <summary>
/// Validates provider-specific source syntax while the bound AST still carries the original
/// function names. This prevents normalization from turning SQL that is not valid for the declared
/// source dialect into valid SQL for a different target provider.
/// </summary>
internal static class CoreSourceDialectValidator
{
    public static void Validate(SqlStatement statement, SqlAgentToolType sourceDialect)
    {
        ArgumentNullException.ThrowIfNull(statement);
        VisitStatement(statement, sourceDialect);
    }

    private static void VisitStatement(SqlStatement statement, SqlAgentToolType sourceDialect)
    {
        switch (statement)
        {
            case SelectStatement select:
                foreach (var cte in select.Ctes)
                    VisitStatement(cte.Query, sourceDialect);
                if (select.From is not null)
                    VisitSource(select.From, sourceDialect);
                foreach (var join in select.Joins)
                {
                    VisitSource(join.Source, sourceDialect);
                    if (join.Predicate is not null) ValidateExpressionTree(join.Predicate, sourceDialect);
                }
                foreach (var item in select.Select) ValidateExpressionTree(item.Expression, sourceDialect);
                if (select.Where is not null) ValidateExpressionTree(select.Where, sourceDialect);
                foreach (var item in select.GroupBy) ValidateExpressionTree(item, sourceDialect);
                if (select.Having is not null) ValidateExpressionTree(select.Having, sourceDialect);
                foreach (var item in select.OrderBy) VisitOrderBy(item, sourceDialect);
                return;

            case QueryStatement query:
                VisitStatement(query.Head, sourceDialect);
                foreach (var operation in query.SetOperations)
                    VisitStatement(operation.Query, sourceDialect);
                foreach (var item in query.OrderBy) VisitOrderBy(item, sourceDialect);
                return;

            case UpdateStatement update:
                foreach (var assignment in update.Assignments)
                    ValidateExpressionTree(assignment.Value, sourceDialect);
                if (update.Predicate is not null) ValidateExpressionTree(update.Predicate, sourceDialect);
                return;

            case DeleteStatement delete:
                if (delete.Predicate is not null) ValidateExpressionTree(delete.Predicate, sourceDialect);
                return;

            case InsertStatement insert:
                switch (insert.Source)
                {
                    case InsertValuesSource values:
                        foreach (var row in values.Rows)
                        foreach (var value in row)
                            ValidateExpressionTree(value, sourceDialect);
                        return;
                    case InsertQuerySource querySource:
                        VisitStatement(querySource.Query, sourceDialect);
                        return;
                    default:
                        throw new SqlCompilationException(
                            $"Unsupported INSERT source during source-dialect validation: {insert.Source.GetType().Name}");
                }

            default:
                throw new SqlCompilationException(
                    $"Unsupported statement during source-dialect validation: {statement.GetType().Name}");
        }
    }

    private static void VisitSource(TableSource source, SqlAgentToolType sourceDialect)
    {
        if (source is DerivedTableSource derived)
            VisitStatement(derived.Query, sourceDialect);
    }

    private static void VisitOrderBy(OrderByItem item, SqlAgentToolType sourceDialect)
    {
        ValidateOrderByModifier(item, sourceDialect);
        ValidateExpressionTree(item.Expression, sourceDialect);
    }

    private static void ValidateExpressionTree(
        SqlExpr expression,
        SqlAgentToolType sourceDialect)
    {
        foreach (var node in CoreSqlAstTraversal.EnumerateExpressions(expression))
            ValidateExpressionNode(node, sourceDialect);
    }

    private static void ValidateExpressionNode(
        SqlExpr expression,
        SqlAgentToolType sourceDialect)
    {
        switch (expression)
        {
            case IntervalExpr:
                var intervalError = SqlIntervalLiteralCapabilityRules.SourceValidationError(sourceDialect);
                if (intervalError is not null)
                    throw new SqlCompilationException(intervalError);
                return;

            case FunctionCallExpr function:
                ValidateFunction(function, sourceDialect);
                ValidateAggregateSeparatorClause(function, sourceDialect);
                foreach (var item in function.AggregateOrderBy)
                    ValidateOrderByModifier(item, sourceDialect);
                return;

            case FilterExpr:
                var filterSourceError = SqlAggregateFilterCapabilityRules.RawSourceSyntaxError(sourceDialect);
                if (filterSourceError is not null)
                    throw new SqlCompilationException(filterSourceError);
                return;

            case WindowedExpr windowed:
                foreach (var item in windowed.Window.OrderBy)
                    ValidateOrderByModifier(item, sourceDialect);
                return;

            case SubqueryExpr subquery:
                ValidateStatementOrderByModifiers(subquery.Query, sourceDialect);
                return;

            case ExistsExpr exists:
                ValidateStatementOrderByModifiers(exists.Query, sourceDialect);
                return;

            default:
                return;
        }
    }

    /// <summary>
    /// The shared expression traversal descends through scalar/EXISTS subqueries, but ORDER BY
    /// modifiers are statement metadata rather than SqlExpr nodes. Keep this small structural walk
    /// only for that metadata so source validation does not reintroduce a parallel expression walker.
    /// Deeper scalar/EXISTS subqueries are reached by CoreSqlAstTraversal and invoke this method from
    /// their own wrapper node.
    /// </summary>
    private static void ValidateStatementOrderByModifiers(
        SqlStatement statement,
        SqlAgentToolType sourceDialect)
    {
        switch (statement)
        {
            case SelectStatement select:
                foreach (var cte in select.Ctes)
                    ValidateStatementOrderByModifiers(cte.Query, sourceDialect);
                if (select.From is DerivedTableSource derived)
                    ValidateStatementOrderByModifiers(derived.Query, sourceDialect);
                foreach (var join in select.Joins)
                {
                    if (join.Source is DerivedTableSource joinedDerived)
                        ValidateStatementOrderByModifiers(joinedDerived.Query, sourceDialect);
                }
                foreach (var item in select.OrderBy)
                    ValidateOrderByModifier(item, sourceDialect);
                return;

            case QueryStatement query:
                ValidateStatementOrderByModifiers(query.Head, sourceDialect);
                foreach (var operation in query.SetOperations)
                    ValidateStatementOrderByModifiers(operation.Query, sourceDialect);
                foreach (var item in query.OrderBy)
                    ValidateOrderByModifier(item, sourceDialect);
                return;

            case InsertStatement { Source: InsertQuerySource querySource }:
                ValidateStatementOrderByModifiers(querySource.Query, sourceDialect);
                return;

            case InsertStatement:
            case UpdateStatement:
            case DeleteStatement:
                return;

            default:
                throw new SqlCompilationException(
                    $"Unsupported statement during source ORDER BY metadata validation: {statement.GetType().Name}");
        }
    }

    private static void ValidateOrderByModifier(
        OrderByItem item,
        SqlAgentToolType sourceDialect)
    {
        var error = SqlNullOrderingCapabilityRules.SourceValidationError(
            sourceDialect,
            item.NullOrdering);
        if (error is not null)
            throw new SqlCompilationException(error);
    }

    private static void ValidateFunction(FunctionCallExpr function, SqlAgentToolType sourceDialect)
    {
        var name = IdentifierText(function.Name).ToUpperInvariant();
        var arity = function.Arguments.Length;

        switch (name)
        {
            case "DATEADD":
                Require(name, sourceDialect, sourceDialect is SqlAgentToolType.MsSqlServer or SqlAgentToolType.Firebird && arity == 3,
                    "DATEADD is modeled as a three-argument SQL Server/Firebird source function.");
                return;

            case "DATEDIFF":
                Require(name, sourceDialect,
                    sourceDialect switch
                    {
                        SqlAgentToolType.MsSqlServer or SqlAgentToolType.Firebird => arity == 3,
                        SqlAgentToolType.MySQL => arity == 2,
                        _ => false
                    },
                    "DATEDIFF is modeled as SQL Server/Firebird (3 arguments) or MySQL (2 arguments) source syntax.");
                return;

            case "DATE_FORMAT":
                RequireProvider(name, sourceDialect, SqlAgentToolType.MySQL);
                return;
            case "FORMAT":
                RequireProvider(name, sourceDialect, SqlAgentToolType.MsSqlServer,
                    "Core models FORMAT as SQL Server date-format syntax; MySQL/SQLite FORMAT functions have different semantics.");
                return;
            case "TO_DATE":
                Require(name, sourceDialect, sourceDialect is SqlAgentToolType.Postgres or SqlAgentToolType.Oracle,
                    "TO_DATE is modeled only for PostgreSQL and Oracle source syntax.");
                return;

            case "CHARINDEX":
                RequireProvider(name, sourceDialect, SqlAgentToolType.MsSqlServer);
                return;
            case "LOCATE":
                RequireProvider(name, sourceDialect, SqlAgentToolType.MySQL);
                return;
            case "STRPOS":
                RequireProvider(name, sourceDialect, SqlAgentToolType.Postgres);
                return;
            case "INSTR":
                Require(name, sourceDialect,
                    sourceDialect is SqlAgentToolType.MySQL or SqlAgentToolType.Sqlite or SqlAgentToolType.Oracle,
                    "INSTR is modeled for MySQL, SQLite, and Oracle source syntax.");
                return;

            case "JSON_EXTRACT":
            case "JSON_SET":
                Require(name, sourceDialect,
                    sourceDialect is SqlAgentToolType.MySQL or SqlAgentToolType.Sqlite,
                    $"{name} is modeled for MySQL and SQLite source syntax.");
                return;

            case "REGEXP_LIKE":
                Require(name, sourceDialect,
                    sourceDialect is SqlAgentToolType.MySQL or SqlAgentToolType.Oracle or SqlAgentToolType.MsSqlServer,
                    "REGEXP_LIKE is modeled for MySQL, Oracle, and SQL Server 2025+ source syntax.");
                return;

            case "GETDATE":
                RequireProvider(name, sourceDialect, SqlAgentToolType.MsSqlServer);
                return;
            case "NOW":
                Require(name, sourceDialect,
                    sourceDialect is SqlAgentToolType.Postgres or SqlAgentToolType.MySQL,
                    "NOW is modeled for PostgreSQL and MySQL source syntax.");
                return;
            case "CURRENT_DATE":
                ValidateCurrentTemporalSource(
                    SqlCurrentTemporalKind.Date,
                    sourceDialect);
                return;
            case "CURRENT_TIME":
                ValidateCurrentTemporalSource(
                    SqlCurrentTemporalKind.Time,
                    sourceDialect);
                return;
            case "CURRENT_TIMESTAMP":
                ValidateCurrentTemporalSource(
                    SqlCurrentTemporalKind.Timestamp,
                    sourceDialect);
                return;

            case "STRING_AGG":
                Require(name, sourceDialect,
                    (sourceDialect is SqlAgentToolType.Postgres or SqlAgentToolType.MsSqlServer) && arity == 2,
                    "STRING_AGG is modeled as a two-argument PostgreSQL/SQL Server source function.");
                return;
            case "GROUP_CONCAT":
                Require(name, sourceDialect,
                    sourceDialect == SqlAgentToolType.MySQL
                    || sourceDialect == SqlAgentToolType.Sqlite && arity is 1 or 2,
                    "GROUP_CONCAT is modeled for MySQL source syntax and SQLite with one or two arguments.");
                return;
            case "LISTAGG":
                Require(name, sourceDialect,
                    sourceDialect == SqlAgentToolType.Oracle && arity is 1 or 2,
                    "LISTAGG is modeled for Oracle source syntax with one or two arguments.");
                return;
            case "LIST":
                Require(name, sourceDialect,
                    sourceDialect == SqlAgentToolType.Firebird && arity is 1 or 2,
                    "LIST is modeled for Firebird source syntax with one or two arguments.");
                return;
        }
    }

    private static void ValidateAggregateSeparatorClause(
        FunctionCallExpr function,
        SqlAgentToolType sourceDialect)
    {
        if (function.AggregateSeparatorClause is null) return;

        var name = IdentifierText(function.Name).ToUpperInvariant();
        if (sourceDialect != SqlAgentToolType.MySQL || name != "GROUP_CONCAT")
        {
            throw new SqlCompilationException(
                "Aggregate SEPARATOR clause is modeled only for MySQL GROUP_CONCAT raw source syntax.");
        }
    }

    private static void ValidateCurrentTemporalSource(
        SqlCurrentTemporalKind kind,
        SqlAgentToolType sourceDialect)
    {
        var error = SqlCurrentTemporalCapabilityRules.SourceValidationError(
            kind,
            sourceDialect);
        if (error is not null)
            throw new SqlCompilationException(error);
    }

    private static void RequireProvider(
        string function,
        SqlAgentToolType actual,
        SqlAgentToolType expected,
        string? detail = null) =>
        Require(function, actual, actual == expected,
            detail ?? $"{function} is modeled as {expected} source syntax.");

    private static void Require(
        string function,
        SqlAgentToolType sourceDialect,
        bool condition,
        string detail)
    {
        if (condition) return;
        throw new SqlCompilationException(
            $"Function '{function}' is not valid for declared source dialect {sourceDialect} in the Core source capability profile. {detail}");
    }

    private static string IdentifierText(SqlIdentifier identifier) =>
        string.Join('.', identifier.Parts.Select(part => part.Value));
}
