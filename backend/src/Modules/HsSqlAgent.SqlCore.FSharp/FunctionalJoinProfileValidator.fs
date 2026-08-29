namespace HsSqlAgent.SqlCore.Core.Analysis

open System
open HsSqlAgent.SqlCore.Core.Ast
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models

/// F# implementation of the JOIN capability gate used by the migration pipeline.
///
/// The legacy C# validator intentionally remains in HsSqlAgent.SqlCore while the
/// old compiler is still used as the parity oracle. Because this type lives in
/// the F# migration assembly, FunctionalPipeline resolves this implementation
/// while the legacy C# pipeline continues resolving its original validator.
[<AbstractClass; Sealed>]
type internal CoreJoinProfileValidator private () =

    static member Validate(
        statement: SqlStatement,
        enforceSourceDialectSyntax: bool,
        sourceDialect: SqlAgentToolType,
        sourceProfile: SqlProviderCapabilityProfile | null,
        targetProvider: SqlAgentToolType,
        targetProfile: SqlProviderCapabilityProfile | null) =

        ArgumentNullException.ThrowIfNull(statement)

        for join in CoreSqlAstTraversal.EnumerateJoins(statement) do
            if enforceSourceDialectSyntax then
                let sourceError =
                    SqlJoinCapabilityRules.SourceValidationError(
                        join.Kind,
                        sourceDialect,
                        sourceProfile)

                if not (isNull sourceError) then
                    raise (SqlCompilationException(sourceError))

            let targetError =
                SqlJoinCapabilityRules.TargetValidationError(
                    join.Kind,
                    targetProvider,
                    targetProfile)

            if not (isNull targetError) then
                raise (SqlCompilationException(targetError))
