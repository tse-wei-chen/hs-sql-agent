namespace HsSqlAgent.SqlCore

open System
open HsSqlAgent.SqlCore.Core.Pipeline
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.Rewrite

[<AbstractClass; Sealed>]
type SqlCoreInspection private () =
    static member TryGetQueryFactsFromException(exception: Exception) =
        ArgumentNullException.ThrowIfNull(exception)

        let rec find (current: Exception | null) =
            match current with
            | null -> null
            | value ->
                match RewriteInspection.tryFromException value with
                | Some facts -> facts
                | None -> find value.InnerException

        find exception

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

        RewriteFacadeAdapter.parseSourceValidated sql sourceDialect null
        |> RewriteBinder.bind sourceDialect
        |> RewriteInspection.inspectBound

    static member GetQueryFacts(parsed: ParsedStatement) =
        ArgumentNullException.ThrowIfNull(parsed)

        if parsed.EnforceSourceDialectSyntax then
            match parsed.RawSql with
            | null -> ()
            | rawSql when String.IsNullOrWhiteSpace(rawSql) -> ()
            | rawSql ->
                RewriteFacadeAdapter.parseSourceValidated
                    rawSql
                    parsed.SourceDialect
                    parsed.SourceProfile
                |> ignore

        RewriteLegacyAstAdapter.toParsed parsed.Statement
        |> RewriteBinder.bind parsed.SourceDialect
        |> RewriteInspection.inspectBound
