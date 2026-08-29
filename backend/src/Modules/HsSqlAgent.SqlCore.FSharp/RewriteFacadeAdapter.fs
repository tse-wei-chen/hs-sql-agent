namespace HsSqlAgent.SqlCore.Rewrite

open System
open System.Collections.Immutable
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Core.Execution
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Rewrite.RewriteLexer
open HsSqlAgent.SqlCore.Rewrite.RewritePolicy
open HsSqlAgent.SqlCore.Rewrite.RewriteRenderer

/// CLR boundary adapter. The rewrite compiler remains independent of compatibility AST types.
/// Keep all legacy enum/DTO conversion here so rewrite internals stay closed and F#-native.
module internal RewriteFacadeAdapter =

    let private provider = function
        | SqlAgentToolType.Postgres -> Provider.PostgreSql
        | SqlAgentToolType.MySQL -> Provider.MySql
        | SqlAgentToolType.MsSqlServer -> Provider.SqlServer
        | SqlAgentToolType.Sqlite -> Provider.SQLite
        | SqlAgentToolType.Oracle -> Provider.Oracle
        | SqlAgentToolType.Firebird -> Provider.Firebird
        | value -> invalidArg "targetProvider" ("Unsupported target provider '" + string value + "'.")

    let private parameters targetProvider (values: (obj | null) list) =
        let prefix =
            match targetProvider with
            | SqlAgentToolType.Oracle -> ":p"
            | _ -> "@p"
        values
        |> List.mapi (fun index value -> SqlParameterValue(prefix + string index, value))
        |> ImmutableArray.CreateRange

    let private statementKind (sql: string) =
        match RewriteLexer.tokenize sql with
        | { Kind = Keyword "SELECT" } :: _ -> SqlStatementKind.Query
        | { Kind = Keyword "INSERT" } :: _ -> SqlStatementKind.Insert
        | { Kind = Keyword "UPDATE" } :: _ -> SqlStatementKind.Update
        | { Kind = Keyword "DELETE" } :: _ -> SqlStatementKind.Delete
        | token :: _ -> invalidArg "sql" ("Unsupported SQL statement at offset " + string token.Start + ".")
        | [] -> invalidArg "sql" "SQL text cannot be empty."

    let private compile (sql: string) (targetProvider: SqlAgentToolType) (policyVersion: string) (policy: ExecutionPolicy) =
        if String.IsNullOrWhiteSpace(sql) then invalidArg "sql" "SQL text cannot be empty."
        let kind = statementKind sql
        let rendered =
            RewritePipeline.compile
                { Provider = provider targetProvider
                  Policy = policy }
                sql
        let parameterValues = parameters targetProvider rendered.Parameters
        let command =
            CompiledSqlCommand(
                rendered.Sql,
                parameterValues,
                kind,
                String.Empty,
                targetProvider)
        let fingerprint = DmlFingerprintService.ComputePlanFingerprint(command, policyVersion)
        CompiledSqlCommand(
            rendered.Sql,
            parameterValues,
            kind,
            fingerprint,
            targetProvider)

    let compileQuery (sql: string) (targetProvider: SqlAgentToolType) (policyVersion: string) (queryMaxRows: int) =
        let policy = { RewritePolicy.safeDefaults with QueryMaxRows = queryMaxRows }
        let command = compile sql targetProvider policyVersion policy
        if command.Kind <> SqlStatementKind.Query then invalidArg "sql" "CompileQuery requires a SELECT statement."
        command

    let compileDml (sql: string) (targetProvider: SqlAgentToolType) (policyVersion: string) =
        let command = compile sql targetProvider policyVersion RewritePolicy.safeDefaults
        if command.Kind = SqlStatementKind.Query then invalidArg "sql" "CompileDml requires INSERT, UPDATE, or DELETE."
        command
