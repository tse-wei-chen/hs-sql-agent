namespace HsSqlAgent.SqlCore.Core.Pipeline

open HsSqlAgent.SqlCore.Core.Analysis
open HsSqlAgent.SqlCore.Core.Ast
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Core.Lowering
open HsSqlAgent.SqlCore.Models

module private FunctionalNativeBackendCompatibility =

    [<RequireQualifiedAccess>]
    type QueryPosition =
        | Root
        | InsertSelectSource
        | CteDefinition
        | DerivedTable
        | SetBranch
        | ScalarSubquery

    let private cteScopeError capability detail =
        SqlCompilationException(
            $"SQL capability '{capability}' is not supported by the native SQL backend: {detail}.")

    let private canLowerNestedCteFragment provider allowNestedCteFragments =
        allowNestedCteFragments
        && SqlNestedCteCapabilityRules.SupportsTarget(provider)

    let private requiresSetTailWrapper (query: QueryStatement) =
        not query.OrderBy.IsDefaultOrEmpty
        || query.Limit.HasValue
        || (query.Offset.HasValue && query.Offset.Value > 0)

    let private canPreserveSetTailCte
        (query: QueryStatement)
        position
        provider
        allowNestedCteFragments =

        match position with
        | QueryPosition.Root
        | QueryPosition.InsertSelectSource -> true
        | QueryPosition.DerivedTable
        | QueryPosition.SetBranch
        | QueryPosition.CteDefinition ->
            canLowerNestedCteFragment provider allowNestedCteFragments
        | QueryPosition.ScalarSubquery ->
            canLowerNestedCteFragment provider allowNestedCteFragments
            && CoreNativeSetTailScope.CanRenderDirectTail(query)

    let private validateCtePlacement ctes position provider allowNestedCteFragments =
        if not ctes.IsDefaultOrEmpty then
            let nestedSupported =
                canLowerNestedCteFragment provider allowNestedCteFragments

            match position with
            | QueryPosition.CteDefinition when not nestedSupported ->
                raise (cteScopeError
                    "select.cte_scope"
                    $"provider {provider} has no declared portable nested-WITH-inside-a-CTE-definition contract")
            | QueryPosition.ScalarSubquery when not nestedSupported ->
                raise (cteScopeError
                    "select.cte_scope"
                    $"provider {provider} has no declared portable WITH-at-the-root-of-a-scalar/EXISTS-subquery contract")
            | QueryPosition.DerivedTable when not nestedSupported ->
                raise (cteScopeError
                    "select.cte_scope"
                    $"provider {provider} has no declared portable WITH-in-derived-table lowering contract")
            | QueryPosition.SetBranch when not nestedSupported ->
                raise (cteScopeError
                    "select.cte_scope"
                    $"provider {provider} has no declared portable WITH-in-set-operation-branch lowering contract")
            | _ -> ()

    let rec private visitExpression expression provider allowNestedCteFragments =
        match expression with
        | :? SubqueryExpr as subquery ->
            validateStatement
                subquery.Query
                QueryPosition.ScalarSubquery
                provider
                allowNestedCteFragments
        | :? ExistsExpr as exists ->
            validateStatement
                exists.Query
                QueryPosition.ScalarSubquery
                provider
                allowNestedCteFragments
        | other ->
            for child in CoreSqlAstTraversal.EnumerateDirectChildren(other) do
                visitExpression child provider allowNestedCteFragments

    and private visitSubqueryExpressions
        (select: SelectStatement)
        provider
        allowNestedCteFragments =

        for item in select.Select do
            visitExpression item.Expression provider allowNestedCteFragments
        match select.Where with
        | null -> ()
        | expression -> visitExpression expression provider allowNestedCteFragments
        for expression in select.GroupBy do
            visitExpression expression provider allowNestedCteFragments
        match select.Having with
        | null -> ()
        | expression -> visitExpression expression provider allowNestedCteFragments
        for item in select.OrderBy do
            visitExpression item.Expression provider allowNestedCteFragments
        for join in select.Joins do
            match join.Predicate with
            | null -> ()
            | expression -> visitExpression expression provider allowNestedCteFragments

    and private validateStatement
        statement
        position
        provider
        allowNestedCteFragments =

        match statement with
        | :? SelectStatement as select ->
            validateCtePlacement select.Ctes position provider allowNestedCteFragments

            for cte in select.Ctes do
                validateStatement
                    cte.Query
                    QueryPosition.CteDefinition
                    provider
                    allowNestedCteFragments

            match select.From with
            | :? DerivedTableSource as derived ->
                validateStatement
                    derived.Query
                    QueryPosition.DerivedTable
                    provider
                    allowNestedCteFragments
            | _ -> ()

            for join in select.Joins do
                match join.Source with
                | :? DerivedTableSource as derived ->
                    validateStatement
                        derived.Query
                        QueryPosition.DerivedTable
                        provider
                        allowNestedCteFragments
                | _ -> ()

            visitSubqueryExpressions select provider allowNestedCteFragments

        | :? QueryStatement as query ->
            validateCtePlacement query.Head.Ctes position provider allowNestedCteFragments

            if not query.Head.Ctes.IsDefaultOrEmpty
               && requiresSetTailWrapper query
               && not (canPreserveSetTailCte query position provider allowNestedCteFragments) then
                let detail =
                    match position with
                    | QueryPosition.ScalarSubquery ->
                        "a scalar/EXISTS root CTE set query needs a scope-preserving direct set tail; Core currently permits that path only when ORDER BY references a combined output name or output ordinal"
                    | _ ->
                        "a set-operation query with a root CTE and outer ORDER BY/LIMIT/OFFSET would enter a nested Select compilation path that cannot preserve its CTE definition"
                raise (cteScopeError "select.cte_scope" detail)

            validateStatement query.Head position provider allowNestedCteFragments
            for operation in query.SetOperations do
                validateStatement
                    operation.Query
                    QueryPosition.SetBranch
                    provider
                    allowNestedCteFragments

        | other ->
            raise (SqlCompilationException(
                $"Unsupported statement for native backend compatibility validation: {other.GetType().Name}"))

    let validateQuery statement provider =
        validateStatement statement QueryPosition.Root provider true

    let validateInsertSelect statement provider =
        validateStatement statement QueryPosition.InsertSelectSource provider true

    let validateDml statement provider =
        let failIfUnsupported (error: string | null) =
            match error with
            | null -> ()
            | message -> raise (SqlCompilationException(message))

        match statement with
        | :? InsertStatement as insert ->
            match insert.Source with
            | :? InsertQuerySource as querySource ->
                validateStatement
                    querySource.Query
                    QueryPosition.InsertSelectSource
                    provider
                    true
            | :? InsertValuesSource as values ->
                for row in values.Rows do
                    for value in row do
                        visitExpression value provider true
            | other ->
                raise (SqlCompilationException(
                    $"Unsupported INSERT source for native DML backend compatibility validation: {other.GetType().Name}"))

        | :? UpdateStatement as update ->
            if not update.From.IsDefaultOrEmpty then
                SqlDmlUpdateFromCapabilityRules.TargetValidationError(provider)
                |> failIfUnsupported

            for assignment in update.Assignments do
                visitExpression assignment.Value provider true
            match update.Predicate with
            | null -> ()
            | expression -> visitExpression expression provider true

        | :? DeleteStatement as delete ->
            if not delete.Using.IsDefaultOrEmpty then
                SqlDmlDeleteUsingCapabilityRules.TargetValidationError(provider)
                |> failIfUnsupported

            match delete.Predicate with
            | null -> ()
            | expression -> visitExpression expression provider true

        | other ->
            raise (SqlCompilationException(
                $"Unsupported statement for native DML backend compatibility validation: {other.GetType().Name}"))

[<AbstractClass; Sealed>]
type internal CoreNativeBackendCompatibility private () =
    static member ValidateQuery(statement: SqlStatement, provider: SqlAgentToolType) =
        FunctionalNativeBackendCompatibility.validateQuery statement provider

    static member ValidateInsertSelect(statement: SqlStatement, provider: SqlAgentToolType) =
        FunctionalNativeBackendCompatibility.validateInsertSelect statement provider

    static member ValidateDml(statement: SqlStatement, provider: SqlAgentToolType) =
        FunctionalNativeBackendCompatibility.validateDml statement provider
