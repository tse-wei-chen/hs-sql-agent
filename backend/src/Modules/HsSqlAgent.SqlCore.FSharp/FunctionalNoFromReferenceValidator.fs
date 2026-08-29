namespace HsSqlAgent.SqlCore.Internal

open HsSqlAgent.SqlCore.Core.Ast
open HsSqlAgent.SqlCore.Core.Binding
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models

/// Guards no-FROM queries from accidentally binding user column references to
/// provider dummy sources such as Oracle DUAL or Firebird RDB$DATABASE.
module internal FunctionalNoFromReferenceValidator =

    let private identifierText (identifier: SqlIdentifier) =
        identifier.Parts
        |> Seq.map (fun part -> part.Value)
        |> String.concat "."

    let private isUnqualifiedWildcardIdentifier
        (identifier: SqlIdentifier) =

        identifier.Parts.Length = 1
        && identifier.Parts[0].Value = "*"
        && not identifier.Parts[0].WasQuoted

    let private isUnqualifiedWildcardExpression
        (expression: SqlExpr) =

        match expression with
        | :? ColumnExpr as column ->
            isUnqualifiedWildcardIdentifier column.Name
        | :? BoundColumnExpr as column ->
            isUnqualifiedWildcardIdentifier column.Name
        | _ ->
            false

    let private isWildcard
        (identifier: SqlIdentifier) =

        if identifier.Parts.IsDefaultOrEmpty then
            false
        else
            let tail =
                identifier.Parts[identifier.Parts.Length - 1]

            tail.Value = "*"
            && not tail.WasQuoted

    let private validateNoFromColumn
        (identifier: SqlIdentifier)
        allowNoFromWildcard =

        if allowNoFromWildcard
           && isUnqualifiedWildcardIdentifier identifier then
            ()
        else
            raise (SqlCompilationException(
                $"Column reference '{identifierText identifier}' requires a FROM source in the portable Core query model."))

    let private isProjectionAliasReference
        (expression: SqlExpr)
        (select: SelectStatement)
        provider =

        let identifier =
            match expression with
            | :? ColumnExpr as column ->
                Some column.Name
            | :? BoundColumnExpr as column ->
                Some column.Name
            | _ ->
                None

        match identifier with
        | None ->
            false

        | Some value
            when value.Parts.Length <> 1
                 || isWildcard value ->
            false

        | Some value ->
            let reference =
                value.Parts[0]

            let matches =
                select.Select
                |> Seq.choose (fun item ->
                    Option.ofObj item.Alias)
                |> Seq.filter (fun alias ->
                    SqlIdentifierDialectRules.Equivalent(
                        alias,
                        reference,
                        provider))
                |> Seq.length

            if matches > 1 then
                raise (SqlCompilationException(
                    $"ORDER BY projection alias '{reference.Value}' is ambiguous in a no-FROM query."))

            matches = 1

    let rec private validateStatement
        (statement: SqlStatement)
        provider =

        match statement with
        | :? SelectStatement as select ->
            validateSelect select provider

        | :? QueryStatement as query ->
            validateSelect query.Head provider

            for operation in query.SetOperations do
                validateStatement operation.Query provider

            for item in query.OrderBy do
                visitNestedSubqueries
                    item.Expression
                    provider

        | :? InsertStatement as insert ->
            match insert.Source with
            | :? InsertQuerySource as querySource ->
                validateStatement
                    querySource.Query
                    provider

            | :? InsertValuesSource as values ->
                for row in values.Rows do
                    for value in row do
                        visitNestedSubqueries
                            value
                            provider

            | _ ->
                ()

        | :? UpdateStatement as update ->
            for assignment in update.Assignments do
                visitNestedSubqueries
                    assignment.Value
                    provider

            match Option.ofObj update.Predicate with
            | Some predicate ->
                visitNestedSubqueries
                    predicate
                    provider
            | None ->
                ()

        | :? DeleteStatement as delete ->
            match Option.ofObj delete.Predicate with
            | Some predicate ->
                visitNestedSubqueries
                    predicate
                    provider
            | None ->
                ()

        | _ ->
            ()

    and private validateSelect
        (select: SelectStatement)
        provider =

        for cte in select.Ctes do
            validateStatement cte.Query provider

        match Option.ofObj select.From with
        | Some (:? DerivedTableSource as derived) ->
            validateStatement derived.Query provider
        | _ ->
            ()

        for join in select.Joins do
            match join.Source with
            | :? DerivedTableSource as derived ->
                validateStatement derived.Query provider
            | _ ->
                ()

            match Option.ofObj join.Predicate with
            | Some predicate ->
                visitNestedSubqueries
                    predicate
                    provider
            | None ->
                ()

        match Option.ofObj select.From with
        | Some _ ->
            for item in select.Select do
                visitNestedSubqueries
                    item.Expression
                    provider

            match Option.ofObj select.Where with
            | Some predicate ->
                visitNestedSubqueries
                    predicate
                    provider
            | None ->
                ()

            for expression in select.GroupBy do
                visitNestedSubqueries
                    expression
                    provider

            match Option.ofObj select.Having with
            | Some predicate ->
                visitNestedSubqueries
                    predicate
                    provider
            | None ->
                ()

            for item in select.OrderBy do
                visitNestedSubqueries
                    item.Expression
                    provider

        | None ->
            if not select.Joins.IsDefaultOrEmpty then
                raise (SqlCompilationException(
                    "A Core SELECT cannot contain JOIN sources without a primary FROM source."))

            for item in select.Select do
                validateNoFromExpression
                    item.Expression
                    provider
                    false

            match Option.ofObj select.Where with
            | Some predicate ->
                validateNoFromExpression
                    predicate
                    provider
                    false
            | None ->
                ()

            for expression in select.GroupBy do
                validateNoFromExpression
                    expression
                    provider
                    false

            match Option.ofObj select.Having with
            | Some predicate ->
                validateNoFromExpression
                    predicate
                    provider
                    false
            | None ->
                ()

            for item in select.OrderBy do
                if not (
                    isProjectionAliasReference
                        item.Expression
                        select
                        provider) then

                    validateNoFromExpression
                        item.Expression
                        provider
                        false

    and private validateNoFromExpression
        (expression: SqlExpr)
        provider
        allowNoFromWildcard =

        match expression with
        | :? LiteralExpr
        | :? IntervalExpr ->
            ()

        | :? ColumnExpr as column ->
            validateNoFromColumn
                column.Name
                allowNoFromWildcard

        | :? BoundColumnExpr as column ->
            if not (isWildcard column.Name)
               && Option.isSome (Option.ofObj column.Source) then
                ()
            else
                validateNoFromColumn
                    column.Name
                    allowNoFromWildcard

        | :? UnaryExpr as unary ->
            validateNoFromExpression
                unary.Operand
                provider
                false

        | :? BinaryExpr as binary ->
            validateNoFromExpression
                binary.Left
                provider
                false

            validateNoFromExpression
                binary.Right
                provider
                false

        | :? FunctionCallExpr as functionCall ->
            let functionName =
                identifierText functionCall.Name
                |> fun value -> value.ToUpperInvariant()

            let wildcardArgumentIndex =
                match Option.ofObj (
                    SqlCanonicalFunctionRegistry.Find(
                        functionName)) with
                | Some contract
                    when contract.NoFromWildcardArgumentIndex.HasValue ->
                    Some contract.NoFromWildcardArgumentIndex.Value
                | _ ->
                    None

            for index = 0 to functionCall.Arguments.Length - 1 do
                let argument =
                    functionCall.Arguments[index]

                let allowFunctionWildcard =
                    wildcardArgumentIndex = Some index
                    && isUnqualifiedWildcardExpression argument

                validateNoFromExpression
                    argument
                    provider
                    allowFunctionWildcard

            for item in functionCall.AggregateOrderBy do
                validateNoFromExpression
                    item.Expression
                    provider
                    false

        | :? FilterExpr as filter ->
            validateNoFromExpression
                filter.Expression
                provider
                false

            validateNoFromExpression
                filter.Predicate
                provider
                false

        | :? WindowedExpr as windowed ->
            validateNoFromExpression
                windowed.Expression
                provider
                false

            for partition in windowed.Window.PartitionBy do
                validateNoFromExpression
                    partition
                    provider
                    false

            for item in windowed.Window.OrderBy do
                validateNoFromExpression
                    item.Expression
                    provider
                    false

        | :? CastExpr as cast ->
            validateNoFromExpression
                cast.Expression
                provider
                false

        | :? CaseExpr as caseExpression ->
            for branch in caseExpression.Branches do
                validateNoFromExpression
                    branch.Condition
                    provider
                    false

                validateNoFromExpression
                    branch.Value
                    provider
                    false

            match Option.ofObj caseExpression.ElseExpression with
            | Some elseExpression ->
                validateNoFromExpression
                    elseExpression
                    provider
                    false
            | None ->
                ()

        | :? InExpr as inExpression ->
            validateNoFromExpression
                inExpression.Value
                provider
                false

            for item in inExpression.Items do
                validateNoFromExpression
                    item
                    provider
                    false

        | :? BetweenExpr as between ->
            validateNoFromExpression
                between.Value
                provider
                false

            validateNoFromExpression
                between.Lower
                provider
                false

            validateNoFromExpression
                between.Upper
                provider
                false

        | :? IsNullExpr as isNull ->
            validateNoFromExpression
                isNull.Value
                provider
                false

        | :? SubqueryExpr as subquery ->
            validateStatement
                subquery.Query
                provider

        | :? ExistsExpr as exists ->
            validateStatement
                exists.Query
                provider

        | other ->
            raise (SqlCompilationException(
                $"Unsupported expression during no-FROM reference validation: {other.GetType().Name}"))

    and private visitNestedSubqueries
        (expression: SqlExpr)
        provider =

        match expression with
        | :? LiteralExpr
        | :? IntervalExpr
        | :? ColumnExpr
        | :? BoundColumnExpr ->
            ()

        | :? UnaryExpr as unary ->
            visitNestedSubqueries
                unary.Operand
                provider

        | :? BinaryExpr as binary ->
            visitNestedSubqueries
                binary.Left
                provider

            visitNestedSubqueries
                binary.Right
                provider

        | :? FunctionCallExpr as functionCall ->
            for argument in functionCall.Arguments do
                visitNestedSubqueries
                    argument
                    provider

            for item in functionCall.AggregateOrderBy do
                visitNestedSubqueries
                    item.Expression
                    provider

        | :? FilterExpr as filter ->
            visitNestedSubqueries
                filter.Expression
                provider

            visitNestedSubqueries
                filter.Predicate
                provider

        | :? WindowedExpr as windowed ->
            visitNestedSubqueries
                windowed.Expression
                provider

            for partition in windowed.Window.PartitionBy do
                visitNestedSubqueries
                    partition
                    provider

            for item in windowed.Window.OrderBy do
                visitNestedSubqueries
                    item.Expression
                    provider

        | :? CastExpr as cast ->
            visitNestedSubqueries
                cast.Expression
                provider

        | :? CaseExpr as caseExpression ->
            for branch in caseExpression.Branches do
                visitNestedSubqueries
                    branch.Condition
                    provider

                visitNestedSubqueries
                    branch.Value
                    provider

            match Option.ofObj caseExpression.ElseExpression with
            | Some elseExpression ->
                visitNestedSubqueries
                    elseExpression
                    provider
            | None ->
                ()

        | :? InExpr as inExpression ->
            visitNestedSubqueries
                inExpression.Value
                provider

            for item in inExpression.Items do
                visitNestedSubqueries
                    item
                    provider

        | :? BetweenExpr as between ->
            visitNestedSubqueries
                between.Value
                provider

            visitNestedSubqueries
                between.Lower
                provider

            visitNestedSubqueries
                between.Upper
                provider

        | :? IsNullExpr as isNull ->
            visitNestedSubqueries
                isNull.Value
                provider

        | :? SubqueryExpr as subquery ->
            validateStatement
                subquery.Query
                provider

        | :? ExistsExpr as exists ->
            validateStatement
                exists.Query
                provider

        | other ->
            raise (SqlCompilationException(
                $"Unsupported expression during nested no-FROM validation: {other.GetType().Name}"))

    let validate
        (statement: SqlStatement)
        provider =

        validateStatement statement provider
