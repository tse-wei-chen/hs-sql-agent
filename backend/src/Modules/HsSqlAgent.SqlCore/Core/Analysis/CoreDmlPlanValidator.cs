namespace HsSqlAgent.SqlCore.Core.Analysis;

/// <summary>
/// DML validator that reuses the common validator for authorization and expression capabilities
/// while preserving the canonical INSERT statement as the validated output.
/// </summary>
public sealed class CoreDmlPlanValidator : ISqlPlanValidator
{
    private readonly CoreSqlPlanValidator _common = new();

    public ValidatedSqlPlan Validate(
        CanonicalStatement statement,
        SqlPlanValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(statement);
        if (statement.Statement is not InsertStatement insert)
        {
            var validated = _common.Validate(statement, context);
            switch (validated.Statement)
            {
                case UpdateStatement update:
                    foreach (var assignment in update.Assignments)
                        CoreBooleanProjectionRules.ValidateAssignment(
                            assignment.Value,
                            statement.TargetProvider);
                    CoreDmlVolatilePredicateValidator.Validate(update.Predicate);
                    break;
                case DeleteStatement delete:
                    CoreDmlVolatilePredicateValidator.Validate(delete.Predicate);
                    break;
            }
            return validated;
        }

        ValidateInsertShape(insert);
        var validationCarrier = insert.Source switch
        {
            InsertQuerySource insertQuerySource => insertQuerySource.Query,
            InsertValuesSource values => CoreInsertValuesCarrier.CreateValidationCarrier(values),
            _ => throw new SqlCompilationException(
                $"Unsupported INSERT source during validation: {insert.Source.GetType().Name}")
        };

        var validatedCarrier = _common.Validate(
            statement with { Statement = validationCarrier },
            context);

        var validatedInsert = insert.Source switch
        {
            InsertQuerySource originalQuerySource => insert with
            {
                Source = originalQuerySource with { Query = validatedCarrier.Statement }
            },
            InsertValuesSource originalValues => insert with
            {
                Source = CoreInsertValuesCarrier.RestoreFromValidationCarrier(
                    originalValues,
                    validatedCarrier.Statement)
            },
            _ => throw new SqlCompilationException(
                $"Unsupported INSERT source after validation: {insert.Source.GetType().Name}")
        };

        if (validatedInsert.Source is InsertValuesSource validatedValues)
        {
            foreach (var row in validatedValues.Rows)
            foreach (var value in row)
            {
                CoreBooleanProjectionRules.ValidateInsertValue(
                    value,
                    statement.TargetProvider);
                ValidateInsertValueScope(value);
            }
        }

        return new ValidatedSqlPlan(
            validatedInsert,
            statement.Facts,
            statement.SourceDialect,
            statement.TargetProvider,
            context.PolicyVersion);
    }

    private static void ValidateInsertShape(InsertStatement insert)
    {
        if (insert.Columns.IsDefaultOrEmpty)
            throw new SqlCompilationException("INSERT requires at least one target column.");
        if (insert.Columns.Any(column => column.Parts.Length != 1))
            throw new SqlCompilationException("INSERT target columns must be unqualified.");

        switch (insert.Source)
        {
            case InsertValuesSource values:
                if (values.Rows.IsDefaultOrEmpty)
                    throw new SqlCompilationException("INSERT VALUES requires at least one row.");
                foreach (var row in values.Rows)
                {
                    if (row.Length != insert.Columns.Length)
                        throw new SqlCompilationException("INSERT VALUES row width does not match target column count.");
                }
                return;

            case InsertQuerySource querySource:
                var sourceWidth = ProjectionWidth(querySource.Query);
                if (sourceWidth is null)
                {
                    throw new SqlCompilationException(
                        "INSERT ... SELECT requires a statically known source projection width; " +
                        "wildcard projections are rejected at the Core validation boundary.");
                }
                if (sourceWidth.Value != insert.Columns.Length)
                {
                    throw new SqlCompilationException(
                        $"INSERT ... SELECT projection width {sourceWidth.Value} does not match " +
                        $"target column count {insert.Columns.Length}.");
                }
                return;

            default:
                throw new SqlCompilationException(
                    $"Unsupported INSERT source during shape validation: {insert.Source.GetType().Name}");
        }
    }

    private static void ValidateInsertValueScope(SqlExpr expression)
    {
        switch (expression)
        {
            case LiteralExpr:
            case IntervalExpr:
                return;

            case ColumnExpr column:
                throw InsertColumnReferenceError(column.Name);
            case BoundColumnExpr column:
                throw InsertColumnReferenceError(column.Name);

            case UnaryExpr unary:
                ValidateInsertValueScope(unary.Operand);
                return;
            case BinaryExpr binary:
                ValidateInsertValueScope(binary.Left);
                ValidateInsertValueScope(binary.Right);
                return;
            case FunctionCallExpr function:
                foreach (var argument in function.Arguments)
                    ValidateInsertValueScope(argument);
                foreach (var item in function.AggregateOrderBy)
                    ValidateInsertValueScope(item.Expression);
                return;
            case FilterExpr filter:
                ValidateInsertValueScope(filter.Expression);
                ValidateInsertValueScope(filter.Predicate);
                return;
            case WindowedExpr windowed:
                ValidateInsertValueScope(windowed.Expression);
                foreach (var partition in windowed.Window.PartitionBy)
                    ValidateInsertValueScope(partition);
                foreach (var item in windowed.Window.OrderBy)
                    ValidateInsertValueScope(item.Expression);
                return;
            case CastExpr cast:
                ValidateInsertValueScope(cast.Expression);
                return;
            case CaseExpr @case:
                foreach (var branch in @case.Branches)
                {
                    ValidateInsertValueScope(branch.Condition);
                    ValidateInsertValueScope(branch.Value);
                }
                if (@case.ElseExpression is not null)
                    ValidateInsertValueScope(@case.ElseExpression);
                return;
            case InExpr @in:
                ValidateInsertValueScope(@in.Value);
                foreach (var item in @in.Items)
                    ValidateInsertValueScope(item);
                return;
            case BetweenExpr between:
                ValidateInsertValueScope(between.Value);
                ValidateInsertValueScope(between.Lower);
                ValidateInsertValueScope(between.Upper);
                return;
            case IsNullExpr isNull:
                ValidateInsertValueScope(isNull.Value);
                return;

            // A scalar/EXISTS subquery owns its own FROM scope and has already been bound and
            // validated recursively by the common pipeline. Do not treat its internal columns as
            // free references from the VALUES row.
            case SubqueryExpr:
            case ExistsExpr:
                return;

            default:
                throw new SqlCompilationException(
                    $"Unsupported INSERT VALUES expression during scope validation: {expression.GetType().Name}");
        }
    }

    private static SqlCompilationException InsertColumnReferenceError(SqlIdentifier identifier) =>
        new(
            $"INSERT VALUES scalar expression cannot reference column '{IdentifierText(identifier)}' " +
            "outside a scalar subquery; use INSERT ... SELECT when the inserted value depends on a source row.");

    private static int? ProjectionWidth(SqlStatement statement) => statement switch
    {
        SelectStatement select when select.Select.Any(item => IsProjectionWildcard(item.Expression)) => null,
        SelectStatement select => select.Select.Length,
        QueryStatement query => ProjectionWidth(query.Head),
        _ => null
    };

    private static bool IsProjectionWildcard(SqlExpr expression) => expression switch
    {
        ColumnExpr column => IsWildcard(column.Name),
        BoundColumnExpr column => IsWildcard(column.Name),
        _ => false
    };

    private static bool IsWildcard(SqlIdentifier identifier) =>
        !identifier.Parts.IsDefaultOrEmpty
        && identifier.Parts[^1].Value == "*"
        && !identifier.Parts[^1].WasQuoted;

    private static string IdentifierText(SqlIdentifier identifier) =>
        string.Join('.', identifier.Parts.Select(part => part.Value));
}
