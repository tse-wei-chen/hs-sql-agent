namespace HsSqlAgent.SqlCore.Rewrite

open System
open System.Collections.Generic
open System.Collections.Immutable
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Core.Execution
open HsSqlAgent.SqlCore.Core.Pipeline
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Rewrite.CoreModel
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

    let private clrParameterValue (value: obj | null) : obj | null =
        match value with
        | :? int64 as integer when integer >= int64 Int32.MinValue && integer <= int64 Int32.MaxValue -> box (int integer)
        | _ -> value

    let private parameters targetProvider (values: (obj | null) list) =
        let prefix =
            match targetProvider with
            | SqlAgentToolType.Oracle -> ":p"
            | _ -> "@p"
        values
        |> List.mapi (fun index value -> SqlParameterValue(prefix + string index, clrParameterValue value))
        |> ImmutableArray.CreateRange

    let private statementKind (sql: string) =
        match RewriteLexer.tokenize sql with
        | { Kind = Keyword "SELECT" } :: _
        | { Kind = Keyword "WITH" } :: _ -> SqlStatementKind.Query
        | { Kind = Keyword "INSERT" } :: _ -> SqlStatementKind.Insert
        | { Kind = Keyword "UPDATE" } :: _ -> SqlStatementKind.Update
        | { Kind = Keyword "DELETE" } :: _ -> SqlStatementKind.Delete
        | token :: _ -> invalidArg "sql" ("Unsupported SQL statement at offset " + string token.Start + ".")
        | [] -> invalidArg "sql" "SQL text cannot be empty."

    let private shouldSurfaceAsCompilationError (message: string) =
        message.Contains("requires provider-specific lowering", StringComparison.Ordinal)
        || message.Contains("requires lowering for this provider", StringComparison.Ordinal)
        || message.Contains("requires provider lowering", StringComparison.Ordinal)
        || message.Contains("lowering is not available", StringComparison.Ordinal)
        || message.Contains("OFFSET requires ORDER BY", StringComparison.Ordinal)
        || message.Contains("RETURNING is not supported", StringComparison.Ordinal)

    let private allowedTables (tables: IReadOnlySet<string> | null) =
        match tables with
        | null -> None
        | values when values.Count = 0 -> None
        | values -> Some(values |> Seq.toList)

    let private compileRewrite options sql =
        try
            RewritePipeline.compile options sql
        with
        | :? SqlCompilationException -> reraise()
        | :? ArgumentException as ex -> raise (SqlCompilationException(ex.Message, ex))
        | :? InvalidOperationException as ex when shouldSurfaceAsCompilationError ex.Message -> raise (SqlCompilationException(ex.Message, ex))

    let private compile (sql: string) (targetProvider: SqlAgentToolType) (policyVersion: string) (policy: ExecutionPolicy) allowed =
        if String.IsNullOrWhiteSpace(sql) then invalidArg "sql" "SQL text cannot be empty."
        let kind = statementKind sql
        let rendered =
            compileRewrite
                { Provider = provider targetProvider
                  Policy = policy
                  AllowedTables = allowed }
                sql
        let parameterValues = parameters targetProvider rendered.Parameters
        let command = CompiledSqlCommand(rendered.Sql, parameterValues, kind, String.Empty, targetProvider)
        let fingerprint = DmlFingerprintService.ComputePlanFingerprint(command, policyVersion)
        CompiledSqlCommand(rendered.Sql, parameterValues, kind, fingerprint, targetProvider)

    let private queryPolicy queryMaxRows =
        let queryRows =
            if queryMaxRows <= 0 then RowCap.Unlimited
            else RowCap.MaxRows(PositiveRowCount.create queryMaxRows)
        { RewritePolicy.safeDefaults with QueryRows = queryRows }

    let compileQuery (sql: string) (targetProvider: SqlAgentToolType) (policyVersion: string) (queryMaxRows: int) =
        let command = compile sql targetProvider policyVersion (queryPolicy queryMaxRows) None
        if command.Kind <> SqlStatementKind.Query then invalidArg "sql" "CompileQuery requires a SELECT statement."
        command

    let compileQueryValidated (sql: string) (targetProvider: SqlAgentToolType) (validationContext: SqlPlanValidationContext) (executionPolicy: SqlExecutionPlanPolicy) =
        ArgumentNullException.ThrowIfNull(validationContext)
        ArgumentNullException.ThrowIfNull(executionPolicy)
        ArgumentException.ThrowIfNullOrWhiteSpace(validationContext.PolicyVersion)
        let command =
            compile sql targetProvider validationContext.PolicyVersion (queryPolicy executionPolicy.QueryMaxRows) (allowedTables validationContext.AllowedTables)
        if command.Kind <> SqlStatementKind.Query then invalidArg "sql" "CompileQuery requires a SELECT statement."
        command

    let compileDml (sql: string) (targetProvider: SqlAgentToolType) (policyVersion: string) =
        let command = compile sql targetProvider policyVersion RewritePolicy.safeDefaults None
        if command.Kind = SqlStatementKind.Query then invalidArg "sql" "CompileDml requires INSERT, UPDATE, or DELETE."
        command

    let compileDmlValidated (sql: string) (targetProvider: SqlAgentToolType) (validationContext: SqlPlanValidationContext) =
        ArgumentNullException.ThrowIfNull(validationContext)
        ArgumentException.ThrowIfNullOrWhiteSpace(validationContext.PolicyVersion)
        let command = compile sql targetProvider validationContext.PolicyVersion RewritePolicy.safeDefaults (allowedTables validationContext.AllowedTables)
        if command.Kind = SqlStatementKind.Query then invalidArg "sql" "CompileDml requires INSERT, UPDATE, or DELETE."
        command
