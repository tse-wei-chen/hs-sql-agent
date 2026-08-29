namespace HsSqlAgent.SqlCore.Internal

open System
open System.Collections.Immutable
open HsSqlAgent.SqlCore.Core.Ast
open HsSqlAgent.SqlCore.Core.Binding
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Core.Normalization
open HsSqlAgent.SqlCore.Core.Pipeline
open HsSqlAgent.SqlCore.Enums

/// F# ownership boundary for statement/source traversal during query normalization.
///
/// Expression canonicalization is deliberately kept behind the legacy CoreSqlNormalizer oracle
/// for this migration slice. That preserves the existing six-dialect function/date/operator
/// semantics while moving recursive statement reconstruction into F# first.
module internal FunctionalQueryNormalizer =

    type private Context =
        {
            Facts: QueryFacts
            SourceDialect: SqlAgentToolType
            TargetProvider: SqlAgentToolType
        }

    let private immutableMap mapper values =
        values
        |> Seq.map mapper
        |> ImmutableArray.CreateRange

    let private normalizeExpressionWithLegacyOracle
        (context: Context)
        (expression: SqlExpr) =

        let carrier =
            SelectStatement(
                ImmutableArray<CteDefinition>.Empty,
                false,
                ImmutableArray.Create(SelectItem(expression, null, expression.Span)),
                null,
                ImmutableArray<JoinSource>.Empty,
                null,
                ImmutableArray<SqlExpr>.Empty,
                null,
                ImmutableArray<OrderByItem>.Empty,
                Nullable<int>(),
                Nullable<int>(),
                expression.Span)

        let normalized =
            CoreSqlNormalizer
                .CreateDefault()
                .Normalize(
                    BoundStatement(
                        carrier,
                        context.Facts,
                        context.SourceDialect),
                    context.TargetProvider)

        match normalized.Statement with
        | :? SelectStatement as select when select.Select.Length = 1 ->
            select.Select[0].Expression
        | other ->
            raise (SqlCompilationException(
                $"Legacy expression normalization oracle returned unexpected carrier {other.GetType().Name}."))

    let rec private normalizeStatement
        (context: Context)
        (statement: SqlStatement)
        : SqlStatement =

        match statement with
        | :? SelectStatement as select ->
            normalizeSelect context select :> SqlStatement

        | :? QueryStatement as query ->
            let head = normalizeSelect context query.Head
            let setOperations =
                query.SetOperations
                |> immutableMap (fun operation ->
                    CoreBindingAstClone.SetOperation(
                        operation,
                        normalizeStatement context operation.Query))
            let orderBy = normalizeOrderBy context query.OrderBy
            CoreBindingAstClone.Query(query, head, setOperations, orderBy) :> SqlStatement

        | :? UpdateStatement as update ->
            let assignments =
                update.Assignments
                |> immutableMap (fun assignment ->
                    CoreBindingAstClone.Assignment(
                        assignment,
                        normalizeExpressionWithLegacyOracle context assignment.Value))

            let predicate : SqlExpr | null =
                match update.Predicate with
                | null -> null
                | value -> normalizeExpressionWithLegacyOracle context value

            CoreBindingAstClone.Update(update, assignments, predicate) :> SqlStatement

        | :? DeleteStatement as delete ->
            let predicate : SqlExpr | null =
                match delete.Predicate with
                | null -> null
                | value -> normalizeExpressionWithLegacyOracle context value

            CoreBindingAstClone.Delete(delete, predicate) :> SqlStatement

        | other ->
            raise (SqlCompilationException(
                $"Unsupported statement during F# normalization traversal: {other.GetType().Name}"))

    and private normalizeSelect
        (context: Context)
        (select: SelectStatement) =

        let ctes =
            select.Ctes
            |> immutableMap (fun cte ->
                CoreBindingAstClone.Cte(
                    cte,
                    normalizeStatement context cte.Query))

        let selectItems =
            select.Select
            |> immutableMap (fun item ->
                CoreBindingAstClone.SelectItem(
                    item,
                    normalizeExpressionWithLegacyOracle context item.Expression))

        let fromSource : TableSource | null =
            match select.From with
            | null -> null
            | source -> normalizeSource context source

        let joins =
            select.Joins
            |> immutableMap (fun join ->
                let predicate : SqlExpr | null =
                    match join.Predicate with
                    | null -> null
                    | value -> normalizeExpressionWithLegacyOracle context value

                CoreBindingAstClone.Join(
                    join,
                    normalizeSource context join.Source,
                    predicate))

        let whereExpr : SqlExpr | null =
            match select.Where with
            | null -> null
            | value -> normalizeExpressionWithLegacyOracle context value

        let groupBy =
            select.GroupBy
            |> immutableMap (normalizeExpressionWithLegacyOracle context)

        let having : SqlExpr | null =
            match select.Having with
            | null -> null
            | value -> normalizeExpressionWithLegacyOracle context value

        CoreBindingAstClone.Select(
            select,
            ctes,
            fromSource,
            joins,
            selectItems,
            whereExpr,
            groupBy,
            having,
            normalizeOrderBy context select.OrderBy)

    and private normalizeSource
        (context: Context)
        (source: TableSource)
        : TableSource =

        match source with
        | :? NamedTableSource as named -> named :> TableSource
        | :? DerivedTableSource as derived ->
            CoreBindingAstClone.Derived(
                derived,
                normalizeStatement context derived.Query)
            :> TableSource
        | other ->
            raise (SqlCompilationException(
                $"Unsupported table source during F# normalization traversal: {other.GetType().Name}"))

    and private normalizeOrderBy
        (context: Context)
        (orderBy: ImmutableArray<OrderByItem>) =

        orderBy
        |> immutableMap (fun item ->
            CoreBindingAstClone.OrderBy(
                item,
                normalizeExpressionWithLegacyOracle context item.Expression))

    let normalize
        (statement: BoundStatement)
        (targetProvider: SqlAgentToolType)
        : CanonicalStatement =

        ArgumentNullException.ThrowIfNull(statement)

        let context =
            {
                Facts = statement.Facts
                SourceDialect = statement.SourceDialect
                TargetProvider = targetProvider
            }

        CanonicalStatement(
            normalizeStatement context statement.Statement,
            statement.Facts,
            statement.SourceDialect,
            targetProvider)
