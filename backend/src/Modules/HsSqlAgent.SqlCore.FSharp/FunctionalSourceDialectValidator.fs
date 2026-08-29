namespace HsSqlAgent.SqlCore.Core.Analysis

open System
open HsSqlAgent.SqlCore.Core.Ast
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models

/// F# implementation of provider-specific raw source syntax validation.
///
/// This intentionally mirrors the legacy structural walk while migration is in progress so
/// normalization cannot accidentally authorize syntax that was invalid for the declared source.
[<AbstractClass; Sealed>]
type internal CoreSourceDialectValidator private () =

    static member private IdentifierText(identifier: SqlIdentifier) =
        identifier.Parts
        |> Seq.map (fun part -> part.Value)
        |> String.concat "."

    static member private ValidateOrderByModifier(item: OrderByItem, sourceDialect: SqlAgentToolType) =
        match SqlNullOrderingCapabilityRules.SourceValidationError(sourceDialect, item.NullOrdering) with
        | null -> ()
        | error -> raise (SqlCompilationException(error))

    static member private ValidateCurrentTemporalSource(kind: SqlCurrentTemporalKind, sourceDialect: SqlAgentToolType) =
        match SqlCurrentTemporalCapabilityRules.SourceValidationError(kind, sourceDialect) with
        | null -> ()
        | error -> raise (SqlCompilationException(error))

    static member private ValidateFunction(functionCall: FunctionCallExpr, sourceDialect: SqlAgentToolType) =
        let name = CoreSourceDialectValidator.IdentifierText(functionCall.Name)
        let arity = functionCall.Arguments.Length

        match SqlSourceFunctionRegistry.Find(name) with
        | null ->
            let mutable currentTemporalKind = Unchecked.defaultof<SqlCurrentTemporalKind>
            if SqlCurrentTemporalCapabilityRules.TryParseRawSourceName(name, &currentTemporalKind) then
                CoreSourceDialectValidator.ValidateCurrentTemporalSource(currentTemporalKind, sourceDialect)
        | contract ->
            match contract.ValidationError(sourceDialect, arity) with
            | null -> ()
            | error -> raise (SqlCompilationException(error))

    static member private ValidateAggregateSeparatorClause(functionCall: FunctionCallExpr, sourceDialect: SqlAgentToolType) =
        if not (isNull functionCall.AggregateSeparatorClause) then
            match SqlSourceFunctionRegistry.Find(CoreSourceDialectValidator.IdentifierText(functionCall.Name)) with
            | null ->
                raise (
                    SqlCompilationException(
                        "Aggregate SEPARATOR clause is modeled only for MySQL GROUP_CONCAT raw source syntax."))
            | contract when contract.SupportsAggregateSeparatorClause(sourceDialect) -> ()
            | _ ->
                raise (
                    SqlCompilationException(
                        "Aggregate SEPARATOR clause is modeled only for MySQL GROUP_CONCAT raw source syntax."))

    static member private ValidateExpressionNode(expression: SqlExpr, sourceDialect: SqlAgentToolType) =
        match expression with
        | :? IntervalExpr ->
            match SqlIntervalLiteralCapabilityRules.SourceValidationError(sourceDialect) with
            | null -> ()
            | error -> raise (SqlCompilationException(error))
        | :? BinaryExpr as binary when binary.Operator.Equals("||", StringComparison.OrdinalIgnoreCase) ->
            match SqlConcatCapabilityRules.RawSourceSyntaxError(sourceDialect) with
            | null -> ()
            | error -> raise (SqlCompilationException(error))
        | :? FunctionCallExpr as functionCall ->
            CoreSourceDialectValidator.ValidateFunction(functionCall, sourceDialect)
            CoreSourceDialectValidator.ValidateAggregateSeparatorClause(functionCall, sourceDialect)
            for item in functionCall.AggregateOrderBy do
                CoreSourceDialectValidator.ValidateOrderByModifier(item, sourceDialect)
        | :? FilterExpr ->
            match SqlAggregateFilterCapabilityRules.RawSourceSyntaxError(sourceDialect) with
            | null -> ()
            | error -> raise (SqlCompilationException(error))
        | :? WindowedExpr as windowed ->
            for item in windowed.Window.OrderBy do
                CoreSourceDialectValidator.ValidateOrderByModifier(item, sourceDialect)
        | :? SubqueryExpr as subquery ->
            CoreSourceDialectValidator.ValidateStatementOrderByModifiers(subquery.Query, sourceDialect)
        | :? ExistsExpr as exists ->
            CoreSourceDialectValidator.ValidateStatementOrderByModifiers(exists.Query, sourceDialect)
        | _ -> ()

    static member private ValidateExpressionTree(expression: SqlExpr, sourceDialect: SqlAgentToolType) =
        for node in CoreSqlAstTraversal.EnumerateExpressions(expression) do
            CoreSourceDialectValidator.ValidateExpressionNode(node, sourceDialect)

    static member private VisitOrderBy(item: OrderByItem, sourceDialect: SqlAgentToolType) =
        CoreSourceDialectValidator.ValidateOrderByModifier(item, sourceDialect)
        CoreSourceDialectValidator.ValidateExpressionTree(item.Expression, sourceDialect)

    static member private VisitSource(source: TableSource, sourceDialect: SqlAgentToolType) =
        match source with
        | :? DerivedTableSource as derived ->
            CoreSourceDialectValidator.VisitStatement(derived.Query, sourceDialect)
        | _ -> ()

    static member private VisitStatement(statement: SqlStatement, sourceDialect: SqlAgentToolType) =
        match statement with
        | :? SelectStatement as select ->
            for cte in select.Ctes do
                CoreSourceDialectValidator.VisitStatement(cte.Query, sourceDialect)

            match select.From with
            | null -> ()
            | source -> CoreSourceDialectValidator.VisitSource(source, sourceDialect)

            for join in select.Joins do
                CoreSourceDialectValidator.VisitSource(join.Source, sourceDialect)
                match join.Predicate with
                | null -> ()
                | predicate -> CoreSourceDialectValidator.ValidateExpressionTree(predicate, sourceDialect)

            for item in select.Select do
                CoreSourceDialectValidator.ValidateExpressionTree(item.Expression, sourceDialect)

            match select.Where with
            | null -> ()
            | predicate -> CoreSourceDialectValidator.ValidateExpressionTree(predicate, sourceDialect)

            for item in select.GroupBy do
                CoreSourceDialectValidator.ValidateExpressionTree(item, sourceDialect)

            match select.Having with
            | null -> ()
            | predicate -> CoreSourceDialectValidator.ValidateExpressionTree(predicate, sourceDialect)

            for item in select.OrderBy do
                CoreSourceDialectValidator.VisitOrderBy(item, sourceDialect)

        | :? QueryStatement as query ->
            CoreSourceDialectValidator.VisitStatement(query.Head, sourceDialect)
            for operation in query.SetOperations do
                CoreSourceDialectValidator.VisitStatement(operation.Query, sourceDialect)
            for item in query.OrderBy do
                CoreSourceDialectValidator.VisitOrderBy(item, sourceDialect)

        | :? UpdateStatement as update ->
            for assignment in update.Assignments do
                CoreSourceDialectValidator.ValidateExpressionTree(assignment.Value, sourceDialect)
            match update.Predicate with
            | null -> ()
            | predicate -> CoreSourceDialectValidator.ValidateExpressionTree(predicate, sourceDialect)

        | :? DeleteStatement as delete ->
            match delete.Predicate with
            | null -> ()
            | predicate -> CoreSourceDialectValidator.ValidateExpressionTree(predicate, sourceDialect)

        | :? InsertStatement as insert ->
            match insert.Source with
            | :? InsertValuesSource as values ->
                for row in values.Rows do
                    for value in row do
                        CoreSourceDialectValidator.ValidateExpressionTree(value, sourceDialect)
            | :? InsertQuerySource as querySource ->
                CoreSourceDialectValidator.VisitStatement(querySource.Query, sourceDialect)
            | source ->
                raise (
                    SqlCompilationException(
                        $"Unsupported INSERT source during source-dialect validation: {source.GetType().Name}"))

        | _ ->
            raise (
                SqlCompilationException(
                    $"Unsupported statement during source-dialect validation: {statement.GetType().Name}"))

    static member private ValidateStatementOrderByModifiers(statement: SqlStatement, sourceDialect: SqlAgentToolType) =
        match statement with
        | :? SelectStatement as select ->
            for cte in select.Ctes do
                CoreSourceDialectValidator.ValidateStatementOrderByModifiers(cte.Query, sourceDialect)

            match select.From with
            | :? DerivedTableSource as derived ->
                CoreSourceDialectValidator.ValidateStatementOrderByModifiers(derived.Query, sourceDialect)
            | _ -> ()

            for join in select.Joins do
                match join.Source with
                | :? DerivedTableSource as joinedDerived ->
                    CoreSourceDialectValidator.ValidateStatementOrderByModifiers(joinedDerived.Query, sourceDialect)
                | _ -> ()

            for item in select.OrderBy do
                CoreSourceDialectValidator.ValidateOrderByModifier(item, sourceDialect)

        | :? QueryStatement as query ->
            CoreSourceDialectValidator.ValidateStatementOrderByModifiers(query.Head, sourceDialect)
            for operation in query.SetOperations do
                CoreSourceDialectValidator.ValidateStatementOrderByModifiers(operation.Query, sourceDialect)
            for item in query.OrderBy do
                CoreSourceDialectValidator.ValidateOrderByModifier(item, sourceDialect)

        | :? InsertStatement as insert ->
            match insert.Source with
            | :? InsertQuerySource as querySource ->
                CoreSourceDialectValidator.ValidateStatementOrderByModifiers(querySource.Query, sourceDialect)
            | _ -> ()

        | :? UpdateStatement
        | :? DeleteStatement -> ()

        | _ ->
            raise (
                SqlCompilationException(
                    $"Unsupported statement during source ORDER BY metadata validation: {statement.GetType().Name}"))

    static member Validate(statement: SqlStatement, sourceDialect: SqlAgentToolType) =
        ArgumentNullException.ThrowIfNull(statement)
        CoreSourceDialectValidator.VisitStatement(statement, sourceDialect)
