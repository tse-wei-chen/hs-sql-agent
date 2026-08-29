namespace HsSqlAgent.SqlCore.Rewrite

open System
open System.Collections.Immutable
open HsSqlAgent.SqlCore.Core.Compilation
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

    let private parameters (values: (obj | null) list) =
        values
        |> List.mapi (fun index value -> SqlParameterValue("p" + string (index + 1), value))
        |> ImmutableArray.CreateRange

    let private statementKind (sql: string) =
        match RewriteLexer.tokenize sql with
        | { Kind = Keyword "SELECT" } :: _ -> SqlStatementKind.Query
        | { Kind = Keyword "INSERT" } :: _ -> SqlStatementKind.Insert
        | { Kind = Keyword "UPDATE" } :: _ -> SqlStatementKind.Update
        | { Kind = Keyword "DELETE" } :: _ -> SqlStatementKind.Delete
        | token :: _ -> invalidArg "sql" ("Unsupported SQL statement at offset " + string token.Start + ".")
        | [] -> invalidArg "sql" "SQL text cannot be empty."

    let private compile (sql: string) (targetProvider: SqlAgentToolType) =
        if String.IsNullOrWhiteSpace(sql) then invalidArg "sql" "SQL text cannot be empty."
        let kind = statementKind sql
        let rendered =
            RewritePipeline.compile
                { Provider = provider targetProvider
                  Policy = RewritePolicy.safeDefaults }
                sql
        CompiledSqlCommand(
            rendered.Sql,
            parameters rendered.Parameters,
            kind,
            String.Empty,
            targetProvider)

    let compileQuery (sql: string) (targetProvider: SqlAgentToolType) =
        let command = compile sql targetProvider
        if command.Kind <> SqlStatementKind.Query then invalidArg "sql" "CompileQuery requires a SELECT statement."
        command

    let compileDml (sql: string) (targetProvider: SqlAgentToolType) =
        let command = compile sql targetProvider
        if command.Kind = SqlStatementKind.Query then invalidArg "sql" "CompileDml requires INSERT, UPDATE, or DELETE."
        command
