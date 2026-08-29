namespace HsSqlAgent.SqlCore.Models

open System
open HsSqlAgent.SqlCore.Core.Ast
open HsSqlAgent.SqlCore.Core.Binding
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums

/// F# ownership of the proven DML RETURNING expression capability slice.
///
/// The contract remains intentionally fail-closed: only PostgreSQL source/target support
/// richer RETURNING expressions, and expression validation stays limited to the deterministic
/// target-row subset already proven by the legacy implementation.
[<AbstractClass; Sealed>]
type internal SqlDmlReturningExpressionCapabilityRules private () =

    static member private CapabilityId = "dml.returning.expression"

    static member SupportsSource(sourceDialect: SqlAgentToolType) =
        sourceDialect = SqlAgentToolType.Postgres

    static member SupportsTarget(provider: SqlAgentToolType) =
        provider = SqlAgentToolType.Postgres

    static member SourceValidationError(sourceDialect: SqlAgentToolType) : string | null =
        if SqlDmlReturningExpressionCapabilityRules.SupportsSource(sourceDialect) then
            null
        else
            $"SQL capability '{SqlDmlReturningExpressionCapabilityRules.CapabilityId}' is currently declared only for the PostgreSQL source dialect; source dialect {sourceDialect} remains fail-closed."

    static member TargetValidationError(provider: SqlAgentToolType) : string | null =
        if SqlDmlReturningExpressionCapabilityRules.SupportsTarget(provider) then
            null
        else
            $"SQL capability '{SqlDmlReturningExpressionCapabilityRules.CapabilityId}' is currently lowered only for PostgreSQL targets; target provider {provider} remains fail-closed."

    static member HasExpressionItems(statement: SqlStatement) =
        match statement with
        | :? InsertStatement as insert ->
            insert.Returning |> Seq.exists (fun item -> item :? DmlReturningExpressionItem)
        | :? UpdateStatement as update ->
            update.Returning |> Seq.exists (fun item -> item :? DmlReturningExpressionItem)
        | :? DeleteStatement as delete ->
            delete.Returning |> Seq.exists (fun item -> item :? DmlReturningExpressionItem)
        | _ -> false

    static member private Fail(message: string) =
        raise (SqlCompilationException(message))

    static member private ValidateTargetColumn(identifier: SqlIdentifier) =
        if identifier.Parts.Length <> 1 then
            SqlDmlReturningExpressionCapabilityRules.Fail(
                $"SQL capability '{SqlDmlReturningExpressionCapabilityRules.CapabilityId}' accepts unqualified target-row columns only; qualified/source-table references remain fail-closed.")

    static member private ValidateLike(likeExpr: BinaryExpr) =
        SqlDmlReturningExpressionCapabilityRules.ValidateNode(likeExpr.Left)
        SqlDmlReturningExpressionCapabilityRules.ValidateNode(likeExpr.Right)

        match likeExpr.LikeEscape with
        | null -> ()
        | escape when escape.Length = 1 && not (Char.IsControl(escape[0])) -> ()
        | _ ->
            SqlDmlReturningExpressionCapabilityRules.Fail(
                $"SQL capability '{SqlDmlReturningExpressionCapabilityRules.CapabilityId}' LIKE/ILIKE ESCAPE requires exactly one non-control character.")

    static member private ValidatePredicate(expression: SqlExpr) =
        match expression with
        | :? UnaryExpr as unary when unary.Operator = "NOT" ->
            SqlDmlReturningExpressionCapabilityRules.ValidatePredicate(unary.Operand)

        | :? BinaryExpr as binary
            when isNull binary.LikeEscape && (binary.Operator = "AND" || binary.Operator = "OR") ->
            SqlDmlReturningExpressionCapabilityRules.ValidatePredicate(binary.Left)
            SqlDmlReturningExpressionCapabilityRules.ValidatePredicate(binary.Right)

        | :? BinaryExpr as binary
            when isNull binary.LikeEscape
                 && (binary.Operator = "=" || binary.Operator = "<>" || binary.Operator = "!="
                     || binary.Operator = ">" || binary.Operator = "<" || binary.Operator = ">="
                     || binary.Operator = "<=") ->
            SqlDmlReturningExpressionCapabilityRules.ValidateNode(binary.Left)
            SqlDmlReturningExpressionCapabilityRules.ValidateNode(binary.Right)

        | :? BinaryExpr as binary when binary.Operator = "LIKE" || binary.Operator = "ILIKE" ->
            SqlDmlReturningExpressionCapabilityRules.ValidateLike(binary)

        | :? IsNullExpr as isNullExpr ->
            SqlDmlReturningExpressionCapabilityRules.ValidateNode(isNullExpr.Value)

        | :? BetweenExpr as between ->
            SqlDmlReturningExpressionCapabilityRules.ValidateNode(between.Value)
            SqlDmlReturningExpressionCapabilityRules.ValidateNode(between.Lower)
            SqlDmlReturningExpressionCapabilityRules.ValidateNode(between.Upper)

        | :? InExpr as inExpr when not inExpr.Items.IsDefaultOrEmpty ->
            SqlDmlReturningExpressionCapabilityRules.ValidateNode(inExpr.Value)
            for item in inExpr.Items do
                SqlDmlReturningExpressionCapabilityRules.ValidateNode(item)

        | _ ->
            SqlDmlReturningExpressionCapabilityRules.Fail(
                $"SQL capability '{SqlDmlReturningExpressionCapabilityRules.CapabilityId}' accepts only comparison, LIKE/ILIKE with a validated optional ESCAPE, IS NULL, BETWEEN, finite IN-list, AND/OR, and NOT predicates over the proven target-row expression subset; predicate node {expression.GetType().Name} remains fail-closed.")

    static member private ValidateSearchedCase(searchedCase: CaseExpr) =
        if searchedCase.Branches.IsDefaultOrEmpty then
            SqlDmlReturningExpressionCapabilityRules.Fail(
                $"SQL capability '{SqlDmlReturningExpressionCapabilityRules.CapabilityId}' requires searched CASE to contain at least one WHEN branch.")

        for branch in searchedCase.Branches do
            SqlDmlReturningExpressionCapabilityRules.ValidatePredicate(branch.Condition)
            SqlDmlReturningExpressionCapabilityRules.ValidateNode(branch.Value)

        match searchedCase.ElseExpression with
        | null -> ()
        | elseExpression -> SqlDmlReturningExpressionCapabilityRules.ValidateNode(elseExpression)

    static member private ValidateSimpleCase(simpleCase: SimpleCaseExpr) =
        if simpleCase.Branches.IsDefaultOrEmpty then
            SqlDmlReturningExpressionCapabilityRules.Fail(
                $"SQL capability '{SqlDmlReturningExpressionCapabilityRules.CapabilityId}' requires simple CASE to contain at least one WHEN branch.")

        for branch in simpleCase.Branches do
            match branch.Condition with
            | :? BinaryExpr as equality when equality.Operator = "=" && isNull equality.LikeEscape ->
                SqlDmlReturningExpressionCapabilityRules.ValidateNode(equality.Left)
                SqlDmlReturningExpressionCapabilityRules.ValidateNode(equality.Right)
                SqlDmlReturningExpressionCapabilityRules.ValidateNode(branch.Value)
            | _ ->
                SqlDmlReturningExpressionCapabilityRules.Fail(
                    $"SQL capability '{SqlDmlReturningExpressionCapabilityRules.CapabilityId}' accepts only canonical simple CASE equality branches.")

        match simpleCase.ElseExpression with
        | null -> ()
        | elseExpression -> SqlDmlReturningExpressionCapabilityRules.ValidateNode(elseExpression)

    static member private ValidateScalarFunction(functionCall: FunctionCallExpr) =
        if functionCall.Name.Parts.Length <> 1 then
            SqlDmlReturningExpressionCapabilityRules.Fail(
                $"SQL capability '{SqlDmlReturningExpressionCapabilityRules.CapabilityId}' accepts canonical unqualified function names only; qualified function references remain fail-closed.")

        let name = functionCall.Name.Parts[0].Value
        match SqlCanonicalFunctionRegistry.Find(name) with
        | null ->
            SqlDmlReturningExpressionCapabilityRules.Fail(
                $"SQL capability '{SqlDmlReturningExpressionCapabilityRules.CapabilityId}' accepts only registered direct-portable scalar functions with canonical arity and no DISTINCT; function '{name}' remains fail-closed.")
        | contract
            when contract.Kind = SqlCanonicalFunctionKind.Scalar
                 && contract.IsDirectPortable
                 && not functionCall.IsDistinct
                 && contract.AcceptsArgumentCount(functionCall.Arguments.Length) ->
            for argument in functionCall.Arguments do
                SqlDmlReturningExpressionCapabilityRules.ValidateNode(argument)
        | _ ->
            SqlDmlReturningExpressionCapabilityRules.Fail(
                $"SQL capability '{SqlDmlReturningExpressionCapabilityRules.CapabilityId}' accepts only registered direct-portable scalar functions with canonical arity and no DISTINCT; function '{name}' remains fail-closed.")

    static member private ValidateNode(expression: SqlExpr) =
        match expression with
        | :? ColumnExpr as column ->
            SqlDmlReturningExpressionCapabilityRules.ValidateTargetColumn(column.Name)
        | :? BoundColumnExpr as column ->
            SqlDmlReturningExpressionCapabilityRules.ValidateTargetColumn(column.Name)
        | :? LiteralExpr -> ()
        | :? UnaryExpr as unary when unary.Operator = "+" || unary.Operator = "-" ->
            SqlDmlReturningExpressionCapabilityRules.ValidateNode(unary.Operand)
        | :? BinaryExpr as binary
            when isNull binary.LikeEscape
                 && (binary.Operator = "+" || binary.Operator = "-" || binary.Operator = "*"
                     || binary.Operator = "/" || binary.Operator = "%" || binary.Operator = "||") ->
            SqlDmlReturningExpressionCapabilityRules.ValidateNode(binary.Left)
            SqlDmlReturningExpressionCapabilityRules.ValidateNode(binary.Right)
        | :? CastExpr as cast ->
            SqlDmlReturningExpressionCapabilityRules.ValidateNode(cast.Expression)
        | :? FunctionCallExpr as functionCall ->
            SqlDmlReturningExpressionCapabilityRules.ValidateScalarFunction(functionCall)
        | :? SimpleCaseExpr as simpleCase ->
            SqlDmlReturningExpressionCapabilityRules.ValidateSimpleCase(simpleCase)
        | :? CaseExpr as searchedCase ->
            SqlDmlReturningExpressionCapabilityRules.ValidateSearchedCase(searchedCase)
        | :? UnaryExpr as unary when unary.Operator = "NOT" ->
            SqlDmlReturningExpressionCapabilityRules.ValidatePredicate(unary)
        | :? BinaryExpr as binary
            when (isNull binary.LikeEscape && (binary.Operator = "AND" || binary.Operator = "OR"))
                 || binary.Operator = "=" || binary.Operator = "<>" || binary.Operator = "!="
                 || binary.Operator = ">" || binary.Operator = "<" || binary.Operator = ">="
                 || binary.Operator = "<=" || binary.Operator = "LIKE" || binary.Operator = "ILIKE" ->
            SqlDmlReturningExpressionCapabilityRules.ValidatePredicate(binary)
        | :? IsNullExpr
        | :? BetweenExpr
        | :? InExpr ->
            SqlDmlReturningExpressionCapabilityRules.ValidatePredicate(expression)
        | _ ->
            SqlDmlReturningExpressionCapabilityRules.Fail(
                $"SQL capability '{SqlDmlReturningExpressionCapabilityRules.CapabilityId}' currently accepts only unqualified target columns, literals, arithmetic/concatenation, unary +/-, CAST, registered direct-portable scalar functions, validated CASE expressions, and the validated target-row predicate subset. Expression node {expression.GetType().Name} remains fail-closed.")

    static member ValidateSource(statement: SqlStatement, sourceDialect: SqlAgentToolType) =
        if SqlDmlReturningExpressionCapabilityRules.HasExpressionItems(statement) then
            match SqlDmlReturningExpressionCapabilityRules.SourceValidationError(sourceDialect) with
            | null -> ()
            | error -> raise (SqlCompilationException(error))

    static member ValidateExpression(item: DmlReturningExpressionItem) =
        ArgumentNullException.ThrowIfNull(item)
        SqlDmlReturningExpressionCapabilityRules.ValidateNode(item.Expression)
