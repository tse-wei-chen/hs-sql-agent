namespace HsSqlAgent.SqlCore.Internal

open System
open System.Collections.Immutable
open HsSqlAgent.SqlCore.Core.Ast
open HsSqlAgent.SqlCore.Core.Binding
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Core.Normalization
open HsSqlAgent.SqlCore.Core.Pipeline
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models

/// F# ownership boundary for query normalization.
///
/// Statement/source traversal and primitive expression normalization live here.
/// Function canonicalization remains behind the legacy CoreSqlNormalizer oracle for now.
/// CAST traversal is F#-owned and delegates only target-type semantic mapping to the existing
/// CoreCastTypeNormalizer while that dialect matrix is migrated separately.
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

    let private normalizeOperator (context: Context) (value: string) =
        let normalized =
            value.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
            |> String.concat " "
            |> fun operatorText -> operatorText.ToUpperInvariant()
            |> function
                | "!=" -> "<>"
                | "NOTIN" -> "NOT IN"
                | "NOTBETWEEN" -> "NOT BETWEEN"
                | "NOTEXISTS" -> "NOT EXISTS"
                | operatorText -> operatorText

        let failIfUnsupported (error: string | null) =
            match error with
            | null -> ()
            | message -> raise (SqlCompilationException(message))

        match normalized with
        | "ILIKE" ->
            SqlIlikeCapabilityRules.SourceValidationError(context.SourceDialect)
            |> failIfUnsupported
        | "||" ->
            SqlConcatCapabilityRules.SourceSemanticValidationError(context.SourceDialect)
            |> failIfUnsupported
        | "%" ->
            SqlModuloCapabilityRules.SourceValidationError(context.SourceDialect)
            |> failIfUnsupported
        | _ -> ()

        normalized

    let private normalizeFunctionWithLegacyOracle
        (context: Context)
        (expression: FunctionCallExpr) =

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
                $"Legacy function normalization oracle returned unexpected carrier {other.GetType().Name}."))

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
                        normalizeExpression context assignment.Value))

            let predicate : SqlExpr | null =
                match update.Predicate with
                | null -> null
                | value -> normalizeExpression context value

            CoreBindingAstClone.Update(update, assignments, predicate) :> SqlStatement

        | :? DeleteStatement as delete ->
            let predicate : SqlExpr | null =
                match delete.Predicate with
                | null -> null
                | value -> normalizeExpression context value

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
                    normalizeExpression context item.Expression))

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
                    | value -> normalizeExpression context value

                JoinSource(
                    join.Kind.Trim().ToUpperInvariant(),
                    normalizeSource context join.Source,
                    predicate,
                    join.Span))

        let whereExpr : SqlExpr | null =
            match select.Where with
            | null -> null
            | value -> normalizeExpression context value

        let groupBy =
            select.GroupBy
            |> immutableMap (normalizeExpression context)

        let having : SqlExpr | null =
            match select.Having with
            | null -> null
            | value -> normalizeExpression context value

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
                normalizeExpression context item.Expression))

    and private normalizeWindow
        (context: Context)
        (window: WindowSpec) =

        CoreBindingAstClone.Window(
            window,
            window.PartitionBy |> immutableMap (normalizeExpression context),
            normalizeOrderBy context window.OrderBy)

    and private normalizeExpression
        (context: Context)
        (expression: SqlExpr)
        : SqlExpr =

        match expression with
        | :? LiteralExpr
        | :? IntervalExpr
        | :? BoundColumnExpr
        | :? ColumnExpr -> expression

        | :? UnaryExpr as unary ->
            UnaryExpr(
                normalizeOperator context unary.Operator,
                normalizeExpression context unary.Operand,
                unary.Span)
            :> SqlExpr

        | :? BinaryExpr as binary ->
            BinaryExpr(
                normalizeExpression context binary.Left,
                normalizeOperator context binary.Operator,
                normalizeExpression context binary.Right,
                binary.Span,
                binary.LikeEscape)
            :> SqlExpr

        | :? FunctionCallExpr as functionCall ->
            normalizeFunctionWithLegacyOracle context functionCall

        | :? CastExpr as castExpr ->
            CastExpr(
                normalizeExpression context castExpr.Expression,
                CoreCastTypeNormalizer.Normalize(
                    castExpr.TypeName,
                    context.SourceDialect,
                    context.TargetProvider),
                castExpr.Span)
            :> SqlExpr

        | :? FilterExpr as filter ->
            CoreBindingAstClone.Filter(
                filter,
                normalizeExpression context filter.Expression,
                normalizeExpression context filter.Predicate)
            :> SqlExpr

        | :? WindowedExpr as windowed ->
            CoreBindingAstClone.Windowed(
                windowed,
                normalizeExpression context windowed.Expression,
                normalizeWindow context windowed.Window)
            :> SqlExpr

        | :? CaseExpr as caseExpr ->
            let branches =
                caseExpr.Branches
                |> immutableMap (fun branch ->
                    CaseBranch(
                        normalizeExpression context branch.Condition,
                        normalizeExpression context branch.Value))

            let elseExpression : SqlExpr | null =
                match caseExpr.ElseExpression with
                | null -> null
                | value -> normalizeExpression context value

            CoreBindingAstClone.Case(caseExpr, branches, elseExpression) :> SqlExpr

        | :? InExpr as inExpr ->
            CoreBindingAstClone.In(
                inExpr,
                normalizeExpression context inExpr.Value,
                inExpr.Items |> immutableMap (normalizeExpression context))
            :> SqlExpr

        | :? BetweenExpr as between ->
            CoreBindingAstClone.Between(
                between,
                normalizeExpression context between.Value,
                normalizeExpression context between.Lower,
                normalizeExpression context between.Upper)
            :> SqlExpr

        | :? IsNullExpr as isNullExpr ->
            CoreBindingAstClone.IsNull(
                isNullExpr,
                normalizeExpression context isNullExpr.Value)
            :> SqlExpr

        | :? SubqueryExpr as subquery ->
            CoreBindingAstClone.Subquery(
                subquery,
                normalizeStatement context subquery.Query)
            :> SqlExpr

        | :? ExistsExpr as exists ->
            CoreBindingAstClone.Exists(
                exists,
                normalizeStatement context exists.Query)
            :> SqlExpr

        | other ->
            raise (SqlCompilationException(
                $"Unsupported expression during F# normalization: {other.GetType().Name}"))

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
