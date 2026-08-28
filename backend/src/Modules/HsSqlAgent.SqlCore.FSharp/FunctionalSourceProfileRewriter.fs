namespace HsSqlAgent.SqlCore.Internal

open System
open HsSqlAgent.SqlCore.Core.Ast
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models

/// Source-session-dependent canonical rewrites implemented in F#.
module internal FunctionalSourceProfileRewriter =

    [<Literal>]
    let private MySqlPipesConcatMarker =
        "__CORE_MYSQL_PIPES_AS_CONCAT__"

    let validateProfile
        sourceDialect
        (sourceProfile: SqlProviderCapabilityProfile | null) =

        match SqlProviderCapabilityProfileRules.ValidationIssue(
            sourceProfile,
            sourceDialect) with

        | SqlProviderCapabilityProfileValidationIssue.None ->
            ()

        | SqlProviderCapabilityProfileValidationIssue.ProviderMismatch ->
            match Option.ofObj sourceProfile with
            | Some profile ->
                raise (SqlCompilationException(
                    $"Source capability profile declares provider {profile.Provider}, but parsed SQL declares source dialect {sourceDialect}."))
            | None ->
                raise (SqlCompilationException(
                    "Unsupported source capability profile validation issue."))

        | SqlProviderCapabilityProfileValidationIssue.NegativeCompatibilityLevel ->
            raise (SqlCompilationException(
                "Provider compatibility level must be non-negative."))

        | _ ->
            raise (SqlCompilationException(
                "Unsupported source capability profile validation issue."))

    let supportsMySqlPipesAsConcat
        sourceDialect
        (sourceProfile: SqlProviderCapabilityProfile | null) =

        SqlConcatCapabilityRules.SupportsMySqlPipesAsConcat(
            sourceDialect,
            sourceProfile)

    let private rewriteBinaryOperator
        (rewriteOperator: string -> string)
        (expression: SqlExpr) =

        match expression with
        | :? BinaryExpr as binary ->
            let rewrittenOperator =
                rewriteOperator binary.Operator

            if rewrittenOperator = binary.Operator then
                expression
            else
                BinaryExpr(
                    binary.Left,
                    rewrittenOperator,
                    binary.Right,
                    binary.Span,
                    binary.LikeEscape)
                :> SqlExpr

        | _ ->
            expression

    let prepare
        (statement: SqlStatement)
        sourceDialect
        (sourceProfile: SqlProviderCapabilityProfile | null) =

        validateProfile
            sourceDialect
            sourceProfile

        if not (
            supportsMySqlPipesAsConcat
                sourceDialect
                sourceProfile) then
            statement
        else
            FunctionalAstRewriter.rewrite
                "source-profile"
                (rewriteBinaryOperator (fun operator ->
                    if operator = "||" then
                        MySqlPipesConcatMarker
                    else
                        operator))
                statement

    let restore
        (statement: SqlStatement) =

        FunctionalAstRewriter.rewrite
            "source-profile"
            (rewriteBinaryOperator (fun operator ->
                if operator.Equals(
                    MySqlPipesConcatMarker,
                    StringComparison.OrdinalIgnoreCase) then
                    "||"
                else
                    operator))
            statement
