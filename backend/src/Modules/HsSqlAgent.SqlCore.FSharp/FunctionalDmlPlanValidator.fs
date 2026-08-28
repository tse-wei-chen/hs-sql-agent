namespace HsSqlAgent.SqlCore.Internal

open System
open HsSqlAgent.SqlCore.Core.Analysis
open HsSqlAgent.SqlCore.Core.Ast
open HsSqlAgent.SqlCore.Core.Binding
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Core.Pipeline

/// DML-specific validation implemented in F#.
///
/// Query capability/authorization validation is still delegated to the common
/// validator in this slice. INSERT shape/scope rules and mutation-specific
/// boolean/volatile checks live here so production DML no longer invokes
/// CoreDmlPlanValidator.
module internal FunctionalDmlPlanValidator =

    let private identifierText (identifier: SqlIdentifier) =
        identifier.Parts
        |> Seq.map (fun part -> part.Value)
        |> String.concat "."

    let private insertColumnReferenceError
        (identifier: SqlIdentifier) =

        SqlCompilationException(
            $"INSERT VALUES scalar expression cannot reference column '{identifierText identifier}' outside a scalar subquery; use INSERT ... SELECT when the inserted value depends on a source row.")

    let private isWildcardIdentifier
        (identifier: SqlIdentifier) =

        if identifier.Parts.IsDefaultOrEmpty then
            false
        else
            let tail =
                identifier.Parts[identifier.Parts.Length - 1]

            tail.Value = "*"
            && not tail.WasQuoted

    let private isProjectionWildcard
        (expression: SqlExpr) =

        match expression with
        | :? ColumnExpr as column ->
            isWildcardIdentifier column.Name
        | :? BoundColumnExpr as column ->
            isWildcardIdentifier column.Name
        | _ ->
            false

    let rec private projectionWidth
        (statement: SqlStatement)
        : int option =

        match statement with
        | :? SelectStatement as select ->
            if select.Select
               |> Seq.exists (fun item ->
                   isProjectionWildcard item.Expression) then
                None
            else
                Some select.Select.Length

        | :? QueryStatement as query ->
            projectionWidth query.Head

        | _ ->
            None

    let private validateInsertShape
        (insert: InsertStatement) =

        if insert.Columns.IsDefaultOrEmpty then
            raise (SqlCompilationException(
                "INSERT requires at least one target column."))

        if insert.Columns
           |> Seq.exists (fun column ->
               column.Parts.Length <> 1) then
            raise (SqlCompilationException(
                "INSERT target columns must be unqualified."))

        match insert.Source with
        | :? InsertValuesSource as values ->
            if values.Rows.IsDefaultOrEmpty then
                raise (SqlCompilationException(
                    "INSERT VALUES requires at least one row."))

            for row in values.Rows do
                if row.Length <> insert.Columns.Length then
                    raise (SqlCompilationException(
                        "INSERT VALUES row width does not match target column count."))

        | :? InsertQuerySource as querySource ->
            match projectionWidth querySource.Query with
            | None ->
                raise (SqlCompilationException(
                    "INSERT ... SELECT requires a statically known source projection width; wildcard projections are rejected at the Core validation boundary."))

            | Some sourceWidth
                when sourceWidth <> insert.Columns.Length ->
                raise (SqlCompilationException(
                    $"INSERT ... SELECT projection width {sourceWidth} does not match target column count {insert.Columns.Length}."))

            | Some _ ->
                ()

        | other ->
            raise (SqlCompilationException(
                $"Unsupported INSERT source during shape validation: {other.GetType().Name}"))

    let rec private validateInsertValueScope
        (expression: SqlExpr) =

        match expression with
        | :? LiteralExpr
        | :? IntervalExpr ->
            ()

        | :? ColumnExpr as column ->
            raise (insertColumnReferenceError column.Name)

        | :? BoundColumnExpr as column ->
            raise (insertColumnReferenceError column.Name)

        | :? UnaryExpr as unary ->
            validateInsertValueScope unary.Operand

        | :? BinaryExpr as binary ->
            validateInsertValueScope binary.Left
            validateInsertValueScope binary.Right

        | :? FunctionCallExpr as functionCall ->
            for argument in functionCall.Arguments do
                validateInsertValueScope argument

            for item in functionCall.AggregateOrderBy do
                validateInsertValueScope item.Expression

        | :? FilterExpr as filter ->
            validateInsertValueScope filter.Expression
            validateInsertValueScope filter.Predicate

        | :? WindowedExpr as windowed ->
            validateInsertValueScope windowed.Expression

            for partition in windowed.Window.PartitionBy do
                validateInsertValueScope partition

            for item in windowed.Window.OrderBy do
                validateInsertValueScope item.Expression

        | :? CastExpr as cast ->
            validateInsertValueScope cast.Expression

        | :? CaseExpr as caseExpression ->
            for branch in caseExpression.Branches do
                validateInsertValueScope branch.Condition
                validateInsertValueScope branch.Value

            match Option.ofObj caseExpression.ElseExpression with
            | Some elseExpression ->
                validateInsertValueScope elseExpression
            | None ->
                ()

        | :? InExpr as inExpression ->
            validateInsertValueScope inExpression.Value

            for item in inExpression.Items do
                validateInsertValueScope item

        | :? BetweenExpr as between ->
            validateInsertValueScope between.Value
            validateInsertValueScope between.Lower
            validateInsertValueScope between.Upper

        | :? IsNullExpr as isNull ->
            validateInsertValueScope isNull.Value

        // Scalar/EXISTS subqueries own an independently bound FROM scope.
        | :? SubqueryExpr
        | :? ExistsExpr ->
            ()

        | other ->
            raise (SqlCompilationException(
                $"Unsupported INSERT VALUES expression during scope validation: {other.GetType().Name}"))

    let private validateNonInsert
        (common: CoreSqlPlanValidator)
        (statement: CanonicalStatement)
        (context: SqlPlanValidationContext) =

        let validated =
            common.Validate(statement, context)

        match validated.Statement with
        | :? UpdateStatement as update ->
            for assignment in update.Assignments do
                CoreBooleanProjectionRules.ValidateAssignment(
                    assignment.Value,
                    statement.TargetProvider)

            CoreDmlVolatilePredicateValidator.Validate(
                update.Predicate)

        | :? DeleteStatement as delete ->
            CoreDmlVolatilePredicateValidator.Validate(
                delete.Predicate)

        | _ ->
            ()

        validated

    let private validateInsert
        (common: CoreSqlPlanValidator)
        (statement: CanonicalStatement)
        (context: SqlPlanValidationContext)
        (insert: InsertStatement) =

        validateInsertShape insert

        let validationCarrier =
            match insert.Source with
            | :? InsertQuerySource as querySource ->
                querySource.Query

            | :? InsertValuesSource as values ->
                CoreInsertValuesCarrier.CreateValidationCarrier(
                    values)

            | other ->
                raise (SqlCompilationException(
                    $"Unsupported INSERT source during validation: {other.GetType().Name}"))

        let validatedCarrier =
            common.Validate(
                CanonicalStatement(
                    validationCarrier,
                    statement.Facts,
                    statement.SourceDialect,
                    statement.TargetProvider),
                context)

        let validatedInsert =
            match insert.Source with
            | :? InsertQuerySource as originalQuerySource ->
                let source =
                    CoreBindingAstClone.InsertQuery(
                        originalQuerySource,
                        validatedCarrier.Statement)

                CoreBindingAstClone.Insert(
                    insert,
                    source)

            | :? InsertValuesSource as originalValues ->
                let source =
                    CoreInsertValuesCarrier.RestoreFromValidationCarrier(
                        originalValues,
                        validatedCarrier.Statement)

                CoreBindingAstClone.Insert(
                    insert,
                    source)

            | other ->
                raise (SqlCompilationException(
                    $"Unsupported INSERT source after validation: {other.GetType().Name}"))

        match validatedInsert.Source with
        | :? InsertValuesSource as validatedValues ->
            for row in validatedValues.Rows do
                for value in row do
                    CoreBooleanProjectionRules.ValidateInsertValue(
                        value,
                        statement.TargetProvider)

                    validateInsertValueScope value

        | _ ->
            ()

        ValidatedSqlPlan(
            validatedInsert,
            statement.Facts,
            statement.SourceDialect,
            statement.TargetProvider,
            context.PolicyVersion)

    /// Validate a canonical INSERT/UPDATE/DELETE plan.
    let validate
        (statement: CanonicalStatement)
        (context: SqlPlanValidationContext)
        : ValidatedSqlPlan =

        let common = CoreSqlPlanValidator()

        match statement.Statement with
        | :? InsertStatement as insert ->
            validateInsert
                common
                statement
                context
                insert

        | :? UpdateStatement
        | :? DeleteStatement ->
            validateNonInsert
                common
                statement
                context

        | other ->
            raise (SqlCompilationException(
                $"Unsupported DML statement during validation: {other.GetType().Name}"))
