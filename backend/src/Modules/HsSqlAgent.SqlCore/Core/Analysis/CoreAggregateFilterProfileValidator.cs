namespace HsSqlAgent.SqlCore.Core.Analysis;

/// <summary>
/// Enforces runtime-version boundaries for aggregate FILTER after binding has exposed the complete
/// query graph. Source and target profiles remain independent: a source profile never authorizes a
/// target capability, even when both sides name the same provider.
/// </summary>
internal static class CoreAggregateFilterProfileValidator
{
    public static void Validate(
        SqlStatement statement,
        bool enforceSourceDialectSyntax,
        SqlAgentToolType sourceDialect,
        SqlProviderCapabilityProfile? sourceProfile,
        SqlAgentToolType targetProvider,
        SqlProviderCapabilityProfile? targetProfile)
    {
        ArgumentNullException.ThrowIfNull(statement);
        if (!ContainsFilter(statement)) return;

        if (enforceSourceDialectSyntax)
            ValidateRuntime("source", sourceDialect, sourceProfile);
        ValidateRuntime("target", targetProvider, targetProfile);
    }

    private static void ValidateRuntime(
        string side,
        SqlAgentToolType provider,
        SqlProviderCapabilityProfile? profile)
    {
        var error = SqlAggregateFilterCapabilityRules.ValidationError(provider, profile, side);
        if (error is not null)
            throw new SqlCompilationException(error);
    }

    private static bool ContainsFilter(SqlStatement statement) => statement switch
    {
        SelectStatement select =>
            select.Ctes.Any(cte => ContainsFilter(cte.Query))
            || select.From is not null && ContainsFilter(select.From)
            || select.Joins.Any(join =>
                ContainsFilter(join.Source)
                || join.Predicate is not null && ContainsFilter(join.Predicate))
            || select.Select.Any(item => ContainsFilter(item.Expression))
            || select.Where is not null && ContainsFilter(select.Where)
            || select.GroupBy.Any(ContainsFilter)
            || select.Having is not null && ContainsFilter(select.Having)
            || select.OrderBy.Any(item => ContainsFilter(item.Expression)),
        QueryStatement query =>
            ContainsFilter(query.Head)
            || query.SetOperations.Any(operation => ContainsFilter(operation.Query))
            || query.OrderBy.Any(item => ContainsFilter(item.Expression)),
        InsertStatement insert => insert.Source switch
        {
            InsertValuesSource values => values.Rows.Any(row => row.Any(ContainsFilter)),
            InsertQuerySource querySource => ContainsFilter(querySource.Query),
            _ => throw new SqlCompilationException(
                $"Unsupported INSERT source during aggregate FILTER profile validation: {insert.Source.GetType().Name}")
        },
        UpdateStatement update =>
            update.Assignments.Any(assignment => ContainsFilter(assignment.Value))
            || update.Predicate is not null && ContainsFilter(update.Predicate),
        DeleteStatement delete =>
            delete.Predicate is not null && ContainsFilter(delete.Predicate),
        _ => throw new SqlCompilationException(
            $"Unsupported statement during aggregate FILTER profile validation: {statement.GetType().Name}")
    };

    private static bool ContainsFilter(TableSource source) => source switch
    {
        NamedTableSource => false,
        DerivedTableSource derived => ContainsFilter(derived.Query),
        _ => throw new SqlCompilationException(
            $"Unsupported table source during aggregate FILTER profile validation: {source.GetType().Name}")
    };

    private static bool ContainsFilter(SqlExpr expression) => expression switch
    {
        FilterExpr => true,
        LiteralExpr or ColumnExpr or BoundColumnExpr or IntervalExpr => false,
        UnaryExpr unary => ContainsFilter(unary.Operand),
        BinaryExpr binary => ContainsFilter(binary.Left) || ContainsFilter(binary.Right),
        FunctionCallExpr function => function.Arguments.Any(ContainsFilter),
        WindowedExpr windowed =>
            ContainsFilter(windowed.Expression)
            || windowed.Window.PartitionBy.Any(ContainsFilter)
            || windowed.Window.OrderBy.Any(item => ContainsFilter(item.Expression)),
        CastExpr cast => ContainsFilter(cast.Expression),
        CaseExpr @case =>
            @case.Branches.Any(branch => ContainsFilter(branch.Condition) || ContainsFilter(branch.Value))
            || @case.ElseExpression is not null && ContainsFilter(@case.ElseExpression),
        InExpr @in => ContainsFilter(@in.Value) || @in.Items.Any(ContainsFilter),
        BetweenExpr between =>
            ContainsFilter(between.Value)
            || ContainsFilter(between.Lower)
            || ContainsFilter(between.Upper),
        IsNullExpr isNull => ContainsFilter(isNull.Value),
        SubqueryExpr subquery => ContainsFilter(subquery.Query),
        ExistsExpr exists => ContainsFilter(exists.Query),
        _ => throw new SqlCompilationException(
            $"Unsupported expression during aggregate FILTER profile validation: {expression.GetType().Name}")
    };
}
