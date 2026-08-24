using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Core.Binding;
using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Enums;

namespace SqlAgent.Service.Core.Analysis;

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
                    if (join.Predicate is not null) VisitExpr(join.Predicate, sourceDialect);
                }
                foreach (var item in select.Select) VisitExpr(item.Expression, sourceDialect);
                if (select.Where is not null) VisitExpr(select.Where, sourceDialect);
                foreach (var item in select.GroupBy) VisitExpr(item, sourceDialect);
                if (select.Having is not null) VisitExpr(select.Having, sourceDialect);
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
                    VisitExpr(assignment.Value, sourceDialect);
                if (update.Predicate is not null) VisitExpr(update.Predicate, sourceDialect);
                return;

            case DeleteStatement delete:
                if (delete.Predicate is not null) VisitExpr(delete.Predicate, sourceDialect);
                return;

            case InsertStatement insert:
                switch (insert.Source)
                {
                    case InsertValuesSource values:
                        foreach (var row in values.Rows)
                        foreach (var value in row)
                            VisitExpr(value, sourceDialect);
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
        if (item.NullOrdering != NullOrderingKind.Default
            && sourceDialect is SqlAgentToolType.MySQL or SqlAgentToolType.MsSqlServer)
        {
            var modifier = item.NullOrdering == NullOrderingKind.First ? "NULLS FIRST" : "NULLS LAST";
            throw new SqlCompilationException(
                $"ORDER BY modifier '{modifier}' is not valid for declared source dialect {sourceDialect} in the Core source capability profile.");
        }

        VisitExpr(item.Expression, sourceDialect);
    }

    private static void VisitExpr(SqlExpr expression, SqlAgentToolType sourceDialect)
    {
        switch (expression)
        {
            case LiteralExpr:
            case ColumnExpr:
            case BoundColumnExpr:
                return;

            case IntervalExpr:
                if (sourceDialect != SqlAgentToolType.Postgres)
                {
                    throw new SqlCompilationException(
                        $"INTERVAL 'literal' is not valid for declared source dialect {sourceDialect} in the Core source capability profile. " +
                        "Core models this interval-literal shape as PostgreSQL source syntax; other dialect interval forms require their own structured translation contract.");
                }
                return;

            case UnaryExpr unary:
                VisitExpr(unary.Operand, sourceDialect);
                return;

            case BinaryExpr binary:
                VisitExpr(binary.Left, sourceDialect);
                VisitExpr(binary.Right, sourceDialect);
                return;

            case FunctionCallExpr function:
                ValidateFunction(function, sourceDialect);
                foreach (var argument in function.Arguments)
                    VisitExpr(argument, sourceDialect);
                return;

            case FilterExpr filter:
                VisitExpr(filter.Expression, sourceDialect);
                VisitExpr(filter.Predicate, sourceDialect);
                return;

            case WindowedExpr windowed:
                VisitExpr(windowed.Expression, sourceDialect);
                foreach (var item in windowed.Window.PartitionBy)
                    VisitExpr(item, sourceDialect);
                foreach (var item in windowed.Window.OrderBy)
                    VisitOrderBy(item, sourceDialect);
                return;

            case CastExpr cast:
                VisitExpr(cast.Expression, sourceDialect);
                return;

            case CaseExpr @case:
                foreach (var branch in @case.Branches)
                {
                    VisitExpr(branch.Condition, sourceDialect);
                    VisitExpr(branch.Value, sourceDialect);
                }
                if (@case.ElseExpression is not null)
                    VisitExpr(@case.ElseExpression, sourceDialect);
                return;

            case InExpr @in:
                VisitExpr(@in.Value, sourceDialect);
                foreach (var item in @in.Items) VisitExpr(item, sourceDialect);
                return;

            case BetweenExpr between:
                VisitExpr(between.Value, sourceDialect);
                VisitExpr(between.Lower, sourceDialect);
                VisitExpr(between.Upper, sourceDialect);
                return;

            case IsNullExpr isNull:
                VisitExpr(isNull.Value, sourceDialect);
                return;

            case SubqueryExpr subquery:
                VisitStatement(subquery.Query, sourceDialect);
                return;

            case ExistsExpr exists:
                VisitStatement(exists.Query, sourceDialect);
                return;

            default:
                throw new SqlCompilationException(
                    $"Unsupported expression during source-dialect validation: {expression.GetType().Name}");
        }
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
                Require(name, sourceDialect, sourceDialect != SqlAgentToolType.MsSqlServer,
                    "CURRENT_DATE is not Transact-SQL source syntax.");
                return;
            case "CURRENT_TIME":
                Require(name, sourceDialect,
                    sourceDialect is not (SqlAgentToolType.MsSqlServer or SqlAgentToolType.Oracle),
                    "CURRENT_TIME is not modeled as SQL Server or Oracle source syntax.");
                return;
            case "CURRENT_TIMESTAMP":
                return;

            case "STRING_AGG":
                Require(name, sourceDialect,
                    sourceDialect is SqlAgentToolType.Postgres or SqlAgentToolType.MsSqlServer,
                    "STRING_AGG is modeled for PostgreSQL and SQL Server source syntax.");
                return;
            case "GROUP_CONCAT":
                Require(name, sourceDialect,
                    sourceDialect is SqlAgentToolType.MySQL or SqlAgentToolType.Sqlite,
                    "GROUP_CONCAT is modeled for MySQL and SQLite source syntax.");
                return;
            case "LISTAGG":
                RequireProvider(name, sourceDialect, SqlAgentToolType.Oracle);
                return;
            case "LIST":
                RequireProvider(name, sourceDialect, SqlAgentToolType.Firebird);
                return;
        }
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
