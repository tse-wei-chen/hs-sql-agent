namespace HsSqlAgent.SqlCore.Rewrite

open System
open System.Collections.Immutable
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Rewrite.RewritePolicy
open HsSqlAgent.SqlCore.Rewrite.RewriteRenderer

/// CLR boundary adapter. The rewrite compiler remains independent of compatibility AST types.
module internal RewriteFacadeAdapter =

    let private provider = function
        | SqlAgentToolType.Postgres -> Provider.PostgreSql
        | SqlAgentToolType.MySQL -> Provider.MySql
        | SqlAgentToolType.MsSqlServer -> Provider.SqlServer
        | SqlAgentToolType.Sqlite -> Provider.SQLite
        | SqlAgentToolType.Oracle -> Provider.Oracle
        | SqlAgentToolType.Firebird -> Provider.Firebird
        | value -> invalidArg "targetProvider" ("Unsupported target provider '" + string value + "'.")

    let private parameters (values: obj list) =
        values
        |> List.mapi (fun index value -> SqlParameterValue("p" + string (index + 1), value))
        |> ImmutableArray.CreateRange

    let compileQuery (sql: string) (targetProvider: SqlAgentToolType) =
        if String.IsNullOrWhiteSpace(sql) then invalidArg "sql" "SQL text cannot be empty."
        let rendered =
            RewritePipeline.compile
                { Provider = provider targetProvider
                  Policy = RewritePolicy.safeDefaults }
                sql
        CompiledSqlCommand(
            rendered.Sql,
            parameters rendered.Parameters,
            SqlStatementKind.Query,
            String.Empty,
            targetProvider)
