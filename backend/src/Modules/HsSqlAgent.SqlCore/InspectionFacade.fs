namespace HsSqlAgent.SqlCore

open System
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Core.Pipeline
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.Rewrite

[<AbstractClass; Sealed>]
type SqlCoreInspection private () =
    static member TryGetQueryFactsFromException(error: Exception) =
        ArgumentNullException.ThrowIfNull(error)

        let rec find (current: Exception | null) =
            match current with
            | null -> null
            | value ->
                match RewriteInspection.tryFromException value with
                | Some facts -> facts
                | None -> find value.InnerException

        find error

    static member GetDeterminismFacts(
        sql: string,
        sourceDialect: SqlAgentToolType,
        targetProvider: SqlAgentToolType) =

        RewriteFacadeAdapter.determinismFacts
            sql
            sourceDialect
            targetProvider
            null
            null

    static member GetDeterminismFacts(
        sql: string,
        sourceDialect: SqlAgentToolType,
        targetProvider: SqlAgentToolType,
        sourceProfile: SqlProviderCapabilityProfile,
        targetProfile: SqlProviderCapabilityProfile) =

        RewriteFacadeAdapter.determinismFacts
            sql
            sourceDialect
            targetProvider
            sourceProfile
            targetProfile

    static member GetQueryFacts(sql: string, sourceDialect: SqlAgentToolType) =
        ArgumentNullException.ThrowIfNull(sql)

        RewriteResourceBudget.withCompilation (fun () ->
            let parsed =
                RewriteResourceBudget.withStage "Parsing" SqlDiagnosticStage.Parse (fun () ->
                    RewriteFacadeAdapter.parseSourceValidated sql sourceDialect null)
            let bound =
                RewriteResourceBudget.withStage "Binding" SqlDiagnosticStage.Binding (fun () ->
                    RewriteBinder.bind sourceDialect parsed)
            RewriteResourceBudget.withStage "Inspection" SqlDiagnosticStage.Binding (fun () ->
                RewriteInspection.inspectBound bound))

    static member GetQueryFacts(parsed: ParsedStatement) =
        ArgumentNullException.ThrowIfNull(parsed)

        RewriteResourceBudget.withCompilation (fun () ->
            if parsed.EnforceSourceDialectSyntax then
                match parsed.RawSql with
                | null -> ()
                | rawSql when String.IsNullOrWhiteSpace(rawSql) -> ()
                | rawSql ->
                    RewriteResourceBudget.withStage "Parsing" SqlDiagnosticStage.Parse (fun () ->
                        RewriteFacadeAdapter.parseSourceValidated
                            rawSql parsed.SourceDialect parsed.SourceProfile)
                    |> ignore

            let bound =
                RewriteResourceBudget.withStage "Binding" SqlDiagnosticStage.Binding (fun () ->
                    RewriteLegacyAstAdapter.toParsed parsed.Statement
                    |> RewriteBinder.bind parsed.SourceDialect)
            RewriteResourceBudget.withStage "Inspection" SqlDiagnosticStage.Binding (fun () ->
                RewriteInspection.inspectBound bound))
