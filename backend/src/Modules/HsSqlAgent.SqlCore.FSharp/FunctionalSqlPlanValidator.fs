namespace HsSqlAgent.SqlCore.Internal

open System
open System.Collections.Generic
open HsSqlAgent.SqlCore.Core.Analysis
open HsSqlAgent.SqlCore.Core.Ast
open HsSqlAgent.SqlCore.Core.Binding
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Core.Pipeline
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models

/// Common query/canonical-plan validation implemented in F#.
///
/// Authorization and recursive capability validation are explicit exhaustive
/// traversals over the legacy CLR AST boundary. Provider capability contracts
/// remain centralized in their existing rule types during this migration slice.
module internal FunctionalSqlPlanValidator =

    type private ExpressionContext =
        | Projection
        | Predicate
        | GroupBy
        | OrderBy
        | FunctionArgument
        | Assignment

    let private identifierText (identifier: SqlIdentifier) =
        identifier.Parts
        |> Seq.map (fun part -> part.Value)
        |> String.concat "."

    let private capabilityError
        provider
        capability =

        SqlCompilationException(
            $"SQL capability '{capability}' is not supported by provider {provider} for this Core plan.")

    let private validateTableAccess
        (facts: QueryFacts)
        (allowedTables: IReadOnlySet<string> | null) =

        match Option.ofObj allowedTables with
        | None ->
            ()

        | Some allowed when allowed.Count = 0 ->
            ()

        | Some allowed ->
            let normalized =
                HashSet<string>(
                    allowed,
                    StringComparer.OrdinalIgnoreCase)

            let violations =
                facts.ReferencedTables
                |> Seq.filter (fun table ->
                    not (normalized.Contains(table)))
                |> Seq.sortWith (fun left right ->
                    StringComparer.OrdinalIgnoreCase.Compare(
                        left,
                        right))
                |> Seq.toArray

            if violations.Length > 0 then
                raise (UnauthorizedAccessException(
                    $"SQL plan is not authorized to access table(s): {String.Join(", ", violations)}"))

    let private validateJoinKind kind =
        match kind with
        | "INNER"
        | "LEFT"
        | "RIGHT"
        | "FULL"
        | "CROSS" ->
            ()
        | other ->
            raise (SqlCompilationException(
                $"Unsupported JOIN kind '{other}'."))

    let private validateOrdering
        orderBy
        provider =

        if SqlNullOrderingCapabilityRules.RequiresTargetRewrite(
            provider) then

            let hasExplicit =
                orderBy
                |> Seq.exists (fun item ->
                    item.NullOrdering
                    <> NullOrderingKind.Default)

            if hasExplicit then
                raise (capabilityError
                    provider
                    "ordering.nulls")

    let private isWildcard
        (expression: SqlExpr) =

        let checkIdentifier
            (identifier: SqlIdentifier) =

            identifier.Parts.Length = 1
            && identifier.Parts[0].Value = "*"
            && not identifier.Parts[0].WasQuoted

        match expression with
        | :? ColumnExpr as column ->
            checkIdentifier column.Name
        | :? BoundColumnExpr as column ->
            checkIdentifier column.Name
        | _ ->
            false

    let private validateWindowBound
        (bound: WindowFrameBoundCore) =

        let requiresOffset =
            bound.Kind = WindowFrameBoundKindCore.Preceding
            || bound.Kind = WindowFrameBoundKindCore.Following

        if requiresOffset then
            if not bound.Offset.HasValue
               || bound.Offset.Value < 0 then
                raise (SqlCompilationException(
                    $"Window frame bound '{bound.Kind}' requires a non-negative offset."))
        elif bound.Offset.HasValue then
            raise (SqlCompilationException(
                $"Window frame bound '{bound.Kind}' must not carry an offset."))

    let private windowBoundPosition
        (bound: WindowFrameBoundCore) =

        match bound.Kind with
        | WindowFrameBoundKindCore.UnboundedPreceding ->
            Int64.MinValue

        | WindowFrameBoundKindCore.Preceding ->
            -int64 bound.Offset.Value

        | WindowFrameBoundKindCore.CurrentRow ->
            0L

        | WindowFrameBoundKindCore.Following ->
            int64 bound.Offset.Value

        | WindowFrameBoundKindCore.UnboundedFollowing ->
            Int64.MaxValue

        | other ->
            raise (SqlCompilationException(
                $"Unsupported window frame bound '{other}'."))

    let private validateWindowFrame
        (frame: WindowFrame | null) =

        match Option.ofObj frame with
        | None ->
            ()

        | Some value ->
            validateWindowBound value.Start

            match Option.ofObj value.End with
            | None ->
                if value.Start.Kind
                   = WindowFrameBoundKindCore.UnboundedFollowing then
                    raise (SqlCompilationException(
                        "Window frame cannot start with UNBOUNDED FOLLOWING."))

            | Some ending ->
                validateWindowBound ending

                if value.Start.Kind
                   = WindowFrameBoundKindCore.UnboundedFollowing then
                    raise (SqlCompilationException(
                        "Window frame cannot start with UNBOUNDED FOLLOWING."))

                if ending.Kind
                   = WindowFrameBoundKindCore.UnboundedPreceding then
                    raise (SqlCompilationException(
                        "Window frame cannot end with UNBOUNDED PRECEDING."))

                if windowBoundPosition value.Start
                   > windowBoundPosition ending then
                    raise (SqlCompilationException(
                        "Window frame start must not be logically after its end bound."))

    let private validatePlanShapeRules
        (contract: SqlCanonicalFunctionContract)
        (functionCall: FunctionCallExpr)
        provider =

        for rule in contract.PlanShapeRules do
            if rule.ArgumentIndex < 0
               || functionCall.Arguments.Length
                  <= rule.ArgumentIndex then
                raise (SqlCompilationException(
                    $"Canonical function '{contract.Name}' declares an invalid plan-shape argument index {rule.ArgumentIndex}."))

            let argument =
                functionCall.Arguments[rule.ArgumentIndex]

            match rule.Kind with
            | SqlCanonicalPlanShapeValidationKind.DistinctWildcardForbidden ->
                if functionCall.IsDistinct
                   && isWildcard argument then

                    let message =
                        match Option.ofObj rule.ValidationMessage with
                        | Some value ->
                            value
                        | None ->
                            $"Canonical function '{contract.Name}' does not allow DISTINCT wildcard arguments."

                    raise (SqlCompilationException(message))

            | SqlCanonicalPlanShapeValidationKind.LiteralStringRequired ->
                let isLiteralString =
                    match argument with
                    | :? LiteralExpr as literal ->
                        literal.Value :? string
                    | _ ->
                        false

                if not isLiteralString then
                    match Option.ofObj rule.CapabilityId with
                    | Some capability
                        when not (
                            String.IsNullOrWhiteSpace(
                                capability)) ->
                        raise (capabilityError
                            provider
                            capability)

                    | _ ->
                        let message =
                            match Option.ofObj rule.ValidationMessage with
                            | Some value ->
                                value
                            | None ->
                                $"Canonical function '{contract.Name}' requires a literal string argument at position {rule.ArgumentIndex + 1}."

                        raise (SqlCompilationException(message))

            | other ->
                raise (SqlCompilationException(
                    $"Unsupported canonical plan-shape rule '{other}' for function '{contract.Name}'."))

    let private validateFunction
        (functionCall: FunctionCallExpr)
        provider
        withinWindow =

        let name =
            identifierText functionCall.Name
            |> fun value -> value.ToUpperInvariant()

        match Option.ofObj (
            SqlCanonicalFunctionRegistry.Find(name)) with
        | Some shape ->
            if not (
                shape.AcceptsArgumentCount(
                    functionCall.Arguments.Length)) then

                let expected =
                    if shape.MinArguments
                       = shape.MaxArguments then
                        string shape.MinArguments
                    else
                        $"{shape.MinArguments}-{shape.MaxArguments}"

                raise (SqlCompilationException(
                    $"Function '{name}' requires {expected} argument(s); received {functionCall.Arguments.Length}."))

            if functionCall.IsDistinct
               && not shape.AllowDistinct then
                raise (SqlCompilationException(
                    $"Function '{name}' does not support DISTINCT in the Core pipeline."))

            if shape.RequireWindow
               && not withinWindow then
                raise (SqlCompilationException(
                    $"Function '{name}' requires an OVER clause."))

            validatePlanShapeRules
                shape
                functionCall
                provider

        | None ->
            if functionCall.IsDistinct then
                raise (SqlCompilationException(
                    $"Function '{name}' has no Core DISTINCT capability declaration."))

    let private validateFilterTarget
        (expression: SqlExpr) =

        match expression with
        | :? FunctionCallExpr as functionCall ->
            let name =
                identifierText functionCall.Name
                |> fun value -> value.ToUpperInvariant()

            match Option.ofObj (
                SqlCanonicalFunctionRegistry.Find(name)) with
            | Some shape when shape.AllowFilter ->
                ()
            | _ ->
                raise (SqlCompilationException(
                    $"Function '{name}' does not support FILTER in the Core pipeline."))

        | _ ->
            raise (SqlCompilationException(
                "FILTER must modify a directly modeled aggregate function."))

    let private validateWindowTarget
        (expression: SqlExpr) =

        let functionCall =
            match expression with
            | :? FunctionCallExpr as direct ->
                Some direct

            | :? FilterExpr as filter ->
                match filter.Expression with
                | :? FunctionCallExpr as filtered ->
                    Some filtered
                | _ ->
                    None

            | _ ->
                None

        match functionCall with
        | None ->
            raise (SqlCompilationException(
                "OVER must modify a directly modeled aggregate or window function."))

        | Some functionCall ->
            let name =
                identifierText functionCall.Name
                |> fun value -> value.ToUpperInvariant()

            match Option.ofObj (
                SqlCanonicalFunctionRegistry.Find(name)) with
            | Some shape when shape.AllowWindow ->
                ()
            | _ ->
                raise (SqlCompilationException(
                    $"Function '{name}' does not support OVER in the Core pipeline."))

    let rec private validateCapabilities
        (statement: SqlStatement)
        provider =

        match statement with
        | :? SelectStatement as select ->
            validateSelect select provider

        | :? QueryStatement as query ->
            validateSelect query.Head provider

            for operation in query.SetOperations do
                validateCapabilities
                    operation.Query
                    provider

            validateOrdering
                query.OrderBy
                provider

            for item in query.OrderBy do
                validateExpression
                    item.Expression
                    provider
                    OrderBy
                    false

        | :? UpdateStatement as update ->
            if update.Assignments.IsDefaultOrEmpty then
                raise (SqlCompilationException(
                    "UPDATE requires at least one assignment."))

            for assignment in update.Assignments do
                validateExpression
                    assignment.Value
                    provider
                    Assignment
                    false

            match Option.ofObj update.Predicate with
            | Some predicate ->
                validateExpression
                    predicate
                    provider
                    Predicate
                    false
            | None ->
                ()

        | :? DeleteStatement as delete ->
            match Option.ofObj delete.Predicate with
            | Some predicate ->
                validateExpression
                    predicate
                    provider
                    Predicate
                    false
            | None ->
                ()

        | other ->
            raise (SqlCompilationException(
                $"Unsupported statement during capability validation: {other.GetType().Name}"))

    and private validateSelect
        (select: SelectStatement)
        provider =

        for cte in select.Ctes do
            validateCapabilities
                cte.Query
                provider

        match Option.ofObj select.From with
        | Some source ->
            validateSource
                source
                provider
        | None ->
            ()

        for join in select.Joins do
            validateJoinKind join.Kind
            validateSource join.Source provider

            match Option.ofObj join.Predicate with
            | Some predicate ->
                validateExpression
                    predicate
                    provider
                    Predicate
                    false
            | None ->
                ()

        for item in select.Select do
            validateExpression
                item.Expression
                provider
                Projection
                false

            CoreBooleanProjectionRules.Validate(
                item.Expression,
                provider)

        match Option.ofObj select.Where with
        | Some predicate ->
            validateExpression
                predicate
                provider
                Predicate
                false
        | None ->
            ()

        for expression in select.GroupBy do
            validateExpression
                expression
                provider
                GroupBy
                false

        match Option.ofObj select.Having with
        | Some predicate ->
            validateExpression
                predicate
                provider
                Predicate
                false
        | None ->
            ()

        validateOrdering
            select.OrderBy
            provider

        for item in select.OrderBy do
            validateExpression
                item.Expression
                provider
                OrderBy
                false

    and private validateSource
        (source: TableSource)
        provider =

        match source with
        | :? NamedTableSource ->
            ()

        | :? DerivedTableSource as derived ->
            validateCapabilities
                derived.Query
                provider

        | other ->
            raise (SqlCompilationException(
                $"Unsupported table source during capability validation: {other.GetType().Name}"))

    and private validateAggregateLocalOrdering
        (functionCall: FunctionCallExpr)
        provider =

        if not functionCall.AggregateOrderBy.IsDefaultOrEmpty then
            let name =
                identifierText functionCall.Name
                |> fun value -> value.ToUpperInvariant()

            let everyOrderingExpressionReferencesColumn =
                functionCall.AggregateOrderBy
                |> Seq.forall (fun item ->
                    CoreSqlAstTraversal
                        .EnumerateExpressions(
                            item.Expression)
                    |> Seq.exists (fun node ->
                        node :? ColumnExpr
                        || node :? BoundColumnExpr))

            let shapeError =
                SqlAggregateLocalOrderingCapabilityRules
                    .CanonicalTargetShapeValidationError(
                        name,
                        provider,
                        everyOrderingExpressionReferencesColumn)

            match Option.ofObj shapeError with
            | Some message ->
                raise (SqlCompilationException(message))
            | None ->
                ()

            validateOrdering
                functionCall.AggregateOrderBy
                provider

            for item in functionCall.AggregateOrderBy do
                validateExpression
                    item.Expression
                    provider
                    OrderBy
                    false

    and private validateExpression
        (expression: SqlExpr)
        provider
        context
        withinWindow =

        match expression with
        | :? LiteralExpr
        | :? ColumnExpr
        | :? BoundColumnExpr ->
            ()

        | :? IntervalExpr ->
            if not (
                SqlIntervalLiteralCapabilityRules
                    .IsTargetSupported(provider)) then
                raise (capabilityError
                    provider
                    "expression.interval")

        | :? UnaryExpr as unary ->
            validateExpression
                unary.Operand
                provider
                context
                false

        | :? BinaryExpr as binary ->
            if binary.Operator.Equals(
                    "ILIKE",
                    StringComparison.OrdinalIgnoreCase)
               && not (
                   SqlIlikeCapabilityRules
                       .SupportsTarget(provider)) then
                raise (capabilityError
                    provider
                    "operator.ilike")

            validateExpression
                binary.Left
                provider
                context
                false

            validateExpression
                binary.Right
                provider
                context
                false

        | :? FunctionCallExpr as functionCall ->
            validateFunction
                functionCall
                provider
                withinWindow

            validateAggregateLocalOrdering
                functionCall
                provider

            for argument in functionCall.Arguments do
                validateExpression
                    argument
                    provider
                    FunctionArgument
                    false

        | :? FilterExpr as filter ->
            if not (
                SqlAggregateFilterCapabilityRules
                    .CanEverSupportProvider(provider)) then
                raise (capabilityError
                    provider
                    "expression.filter")

            validateFilterTarget filter.Expression

            validateExpression
                filter.Expression
                provider
                context
                withinWindow

            validateExpression
                filter.Predicate
                provider
                Predicate
                false

        | :? WindowedExpr as windowed ->
            validateWindowTarget windowed.Expression

            validateExpression
                windowed.Expression
                provider
                context
                true

            for partition in windowed.Window.PartitionBy do
                validateExpression
                    partition
                    provider
                    GroupBy
                    false

            validateOrdering
                windowed.Window.OrderBy
                provider

            for item in windowed.Window.OrderBy do
                validateExpression
                    item.Expression
                    provider
                    OrderBy
                    false

            validateWindowFrame
                windowed.Window.Frame

        | :? CastExpr as cast ->
            validateExpression
                cast.Expression
                provider
                context
                false

        | :? CaseExpr as caseExpression ->
            for branch in caseExpression.Branches do
                validateExpression
                    branch.Condition
                    provider
                    Predicate
                    false

                validateExpression
                    branch.Value
                    provider
                    context
                    false

            match Option.ofObj caseExpression.ElseExpression with
            | Some elseExpression ->
                validateExpression
                    elseExpression
                    provider
                    context
                    false
            | None ->
                ()

        | :? InExpr as inExpression ->
            validateExpression
                inExpression.Value
                provider
                context
                false

            for item in inExpression.Items do
                validateExpression
                    item
                    provider
                    context
                    false

        | :? BetweenExpr as between ->
            validateExpression
                between.Value
                provider
                context
                false
            validateExpression
                between.Lower
                provider
                context
                false
            validateExpression
                between.Upper
                provider
                context
                false

        | :? IsNullExpr as isNull ->
            validateExpression
                isNull.Value
                provider
                context
                false

        | :? SubqueryExpr as subquery ->
            validateCapabilities
                subquery.Query
                provider

        | :? ExistsExpr as exists ->
            validateCapabilities
                exists.Query
                provider

        | other ->
            raise (SqlCompilationException(
                $"Unsupported expression during capability validation: {other.GetType().Name}"))

    /// Validate a canonical query/update/delete carrier plan.
    let validate
        (statement: CanonicalStatement)
        (context: SqlPlanValidationContext)
        : ValidatedSqlPlan =

        ArgumentException.ThrowIfNullOrWhiteSpace(
            context.PolicyVersion)

        let canonicalStatement =
            CoreCteColumnAliasRewriter.Rewrite(
                statement.Statement)

        validateTableAccess
            statement.Facts
            context.AllowedTables

        CoreSqlSemanticValidator.Validate(
            canonicalStatement,
            statement.TargetProvider)

        validateCapabilities
            canonicalStatement
            statement.TargetProvider

        ValidatedSqlPlan(
            canonicalStatement,
            statement.Facts,
            statement.SourceDialect,
            statement.TargetProvider,
            context.PolicyVersion)
