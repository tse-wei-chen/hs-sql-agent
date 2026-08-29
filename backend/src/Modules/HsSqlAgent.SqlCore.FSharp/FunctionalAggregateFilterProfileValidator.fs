namespace HsSqlAgent.SqlCore.Core.Analysis

open System
open HsSqlAgent.SqlCore.Core.Ast
open HsSqlAgent.SqlCore.Core.Binding
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models

/// F# implementation of aggregate FILTER runtime/profile and predicate-shape validation.
///
/// The migration stage still traverses the legacy bound AST because outer-reference
/// provenance is not yet represented in FunctionalAst. Capability policy remains
/// centralized in SqlAggregateFilterCapabilityRules.
[<AbstractClass; Sealed>]
type internal CoreAggregateFilterProfileValidator private () =

    static member private ValidateRuntime(
        side: string,
        provider: SqlAgentToolType,
        profile: SqlProviderCapabilityProfile | null) =

        match SqlAggregateFilterCapabilityRules.ValidationError(provider, profile, side) with
        | null -> ()
        | error -> raise (SqlCompilationException(error))

    static member private ValidatePredicate(
        expression: SqlExpr,
        provider: SqlAgentToolType,
        side: string) =

        for node in CoreSqlAstTraversal.EnumerateExpressions(expression) do
            let feature =
                match node with
                | :? BoundColumnExpr as column when column.IsOuterReference ->
                    Some SqlAggregateFilterPredicateFeature.OuterReference
                | :? SubqueryExpr
                | :? ExistsExpr ->
                    Some SqlAggregateFilterPredicateFeature.Subquery
                | :? WindowedExpr ->
                    Some SqlAggregateFilterPredicateFeature.WindowFunction
                | _ -> None

            match feature with
            | None -> ()
            | Some predicateFeature ->
                match
                    SqlAggregateFilterCapabilityRules.PredicateValidationError(
                        provider,
                        side,
                        predicateFeature)
                    with
                | null -> ()
                | error -> raise (SqlCompilationException(error))

    static member private ValidateFilterPredicates(
        statement: SqlStatement,
        provider: SqlAgentToolType,
        side: string) =

        for expression in CoreSqlAstTraversal.EnumerateExpressions(statement) do
            match expression with
            | :? FilterExpr as filter ->
                CoreAggregateFilterProfileValidator.ValidatePredicate(filter.Predicate, provider, side)
            | _ -> ()

    static member Validate(
        statement: SqlStatement,
        enforceSourceDialectSyntax: bool,
        sourceDialect: SqlAgentToolType,
        sourceProfile: SqlProviderCapabilityProfile | null,
        targetProvider: SqlAgentToolType,
        targetProfile: SqlProviderCapabilityProfile | null) =

        ArgumentNullException.ThrowIfNull(statement)

        let hasFilter =
            CoreSqlAstTraversal.EnumerateExpressions(statement)
            |> Seq.exists (fun expression -> expression :? FilterExpr)

        if hasFilter then
            if enforceSourceDialectSyntax then
                CoreAggregateFilterProfileValidator.ValidateRuntime("source", sourceDialect, sourceProfile)
                CoreAggregateFilterProfileValidator.ValidateFilterPredicates(statement, sourceDialect, "source")

            CoreAggregateFilterProfileValidator.ValidateRuntime("target", targetProvider, targetProfile)
            CoreAggregateFilterProfileValidator.ValidateFilterPredicates(statement, targetProvider, "target")
