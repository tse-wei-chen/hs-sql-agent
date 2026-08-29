namespace HsSqlAgent.SqlCore.Core.Analysis

open System
open HsSqlAgent.SqlCore.Core.Ast
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models

/// F# implementation of the aggregate-local ORDER BY capability gate.
///
/// During the migration this intentionally reuses the legacy AST traversal and
/// capability rules so the F# pipeline can take ownership of the stage without
/// changing the behavior oracle at the same time.
[<AbstractClass; Sealed>]
type internal CoreAggregateLocalOrderingGuard private () =

    static member Validate(
        statement: SqlStatement,
        enforceSourceDialectSyntax: bool,
        sourceDialect: SqlAgentToolType,
        sourceProfile: SqlProviderCapabilityProfile | null,
        targetProvider: SqlAgentToolType,
        targetProfile: SqlProviderCapabilityProfile | null) =

        ArgumentNullException.ThrowIfNull(statement)

        for expression in CoreSqlAstTraversal.EnumerateExpressions(statement) do
            match expression with
            | :? FunctionCallExpr as functionCall when not functionCall.AggregateOrderBy.IsDefaultOrEmpty ->
                let functionName =
                    functionCall.Name.Parts
                    |> Seq.map (fun part -> part.Value)
                    |> String.concat "."
                    |> fun value -> value.ToUpperInvariant()

                match
                    SqlAggregateLocalOrderingCapabilityRules.ValidationError(
                        enforceSourceDialectSyntax,
                        sourceDialect,
                        sourceProfile,
                        targetProvider,
                        targetProfile,
                        functionName,
                        functionCall.AggregateOrderSyntax,
                        functionCall.IsDistinct)
                    with
                | null -> ()
                | error -> raise (SqlCompilationException(error))
            | _ -> ()
