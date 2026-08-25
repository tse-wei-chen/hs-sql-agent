using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Core.Compilation;

namespace HsSqlAgent.SqlCore.Core.Analysis;

/// <summary>
/// Rejects nondeterministic functions whose value can change between DML preview/revalidation and
/// mutation. A changing predicate can select a different row identity set even when the affected
/// row count remains unchanged, so count revalidation alone is not sufficient.
/// Current-temporal expressions are intentionally outside this validator; their approval-time
/// freezing policy is handled separately from random-function determinism.
/// </summary>
internal static class CoreDmlVolatilePredicateValidator
{
    private static readonly HashSet<string> RandomFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "RAND",
        "RANDOM"
    };

    public static void Validate(SqlExpr? predicate)
    {
        if (predicate is not null)
            Visit(predicate);
    }

    private static void VisitStatement(SqlStatement statement)
    {
        switch (statement)
        {
            case SelectStatement select:
                foreach (var cte in select.Ctes)
                    VisitStatement(cte.Query);
                VisitSource(select.From);
                foreach (var join in select.Joins)
                {
                    VisitSource(join.Source);
                    if (join.Predicate is not null) Visit(join.Predicate);
                }
                foreach (var item in select.Select) Visit(item.Expression);
                if (select.Where is not null) Visit(select.Where);
                foreach (var group in select.GroupBy) Visit(group);
                if (select.Having is not null) Visit(select.Having);
                foreach (var item in select.OrderBy) Visit(item.Expression);
                return;

            case QueryStatement query:
                VisitStatement(query.Head);
                foreach (var operation in query.SetOperations)
                    VisitStatement(operation.Query);
                foreach (var item in query.OrderBy) Visit(item.Expression);
                return;
        }
    }

    private static void VisitSource(TableSource? source)
    {
        if (source is DerivedTableSource derived)
            VisitStatement(derived.Query);
    }

    private static void Visit(SqlExpr expression)
    {
        switch (expression)
        {
            case LiteralExpr:
            case ColumnExpr:
            case IntervalExpr:
                return;

            case UnaryExpr unary:
                Visit(unary.Operand);
                return;

            case BinaryExpr binary:
                Visit(binary.Left);
                Visit(binary.Right);
                return;

            case FunctionCallExpr function:
                var name = string.Join('.', function.Name.Parts.Select(part => part.Value));
                if (RandomFunctions.Contains(name))
                {
                    throw new SqlCompilationException(
                        $"Nondeterministic function '{name}' is not allowed in UPDATE/DELETE predicates because the approved row set could change before mutation.");
                }
                foreach (var argument in function.Arguments) Visit(argument);
                return;

            case FilterExpr filter:
                Visit(filter.Expression);
                Visit(filter.Predicate);
                return;

            case WindowedExpr windowed:
                Visit(windowed.Expression);
                foreach (var partition in windowed.Window.PartitionBy) Visit(partition);
                foreach (var item in windowed.Window.OrderBy) Visit(item.Expression);
                return;

            case CastExpr cast:
                Visit(cast.Expression);
                return;

            case CaseExpr @case:
                foreach (var branch in @case.Branches)
                {
                    Visit(branch.Condition);
                    Visit(branch.Value);
                }
                if (@case.ElseExpression is not null) Visit(@case.ElseExpression);
                return;

            case InExpr @in:
                Visit(@in.Value);
                foreach (var item in @in.Items) Visit(item);
                return;

            case BetweenExpr between:
                Visit(between.Value);
                Visit(between.Lower);
                Visit(between.Upper);
                return;

            case IsNullExpr isNull:
                Visit(isNull.Value);
                return;

            case SubqueryExpr subquery:
                VisitStatement(subquery.Query);
                return;

            case ExistsExpr exists:
                VisitStatement(exists.Query);
                return;
        }
    }
}
