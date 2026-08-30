namespace HsSqlAgent.SqlCore.Rewrite

open System
open System.Collections.Generic
open System.Collections.Immutable
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Core.Execution
open HsSqlAgent.SqlCore.Core.Pipeline
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.RewritePolicy
open HsSqlAgent.SqlCore.Rewrite.RewriteRenderer
open HsSqlAgent.SqlCore.Rewrite.Typestate

/// CLR boundary adapter. SQL text crosses into the closed F# DU exactly once here.
module internal RewriteFacadeAdapter =

    let private provider = function
        | SqlAgentToolType.Postgres -> Provider.PostgreSql
        | SqlAgentToolType.MySQL -> Provider.MySql
        | SqlAgentToolType.MsSqlServer -> Provider.SqlServer
        | SqlAgentToolType.Sqlite -> Provider.SQLite
        | SqlAgentToolType.Oracle -> Provider.Oracle
        | SqlAgentToolType.Firebird -> Provider.Firebird
        | value -> invalidArg "targetProvider" ("Unsupported target provider '" + string value + "'.")

    let private sourceDialect = function
        | SqlAgentToolType.Postgres -> RewriteParser.SourceDialect.PostgreSql
        | SqlAgentToolType.MySQL -> RewriteParser.SourceDialect.MySql
        | SqlAgentToolType.MsSqlServer -> RewriteParser.SourceDialect.SqlServer
        | SqlAgentToolType.Sqlite -> RewriteParser.SourceDialect.SQLite
        | SqlAgentToolType.Oracle -> RewriteParser.SourceDialect.Oracle
        | SqlAgentToolType.Firebird -> RewriteParser.SourceDialect.Firebird
        | value -> invalidArg "sourceDialect" ("Unsupported source dialect '" + string value + "'.")

    let private capabilityProof (message: string | null) =
        match message with
        | null -> CapabilityProof.ProvenCapability
        | value -> CapabilityProof.RejectedCapability value

    let private sourceJoinProofs source (sourceProfile: SqlProviderCapabilityProfile | null) : JoinProofs =
        { RightJoin =
            SqlJoinCapabilityRules.SourceValidationError("RIGHT", source, sourceProfile)
            |> capabilityProof
          FullJoin =
            SqlJoinCapabilityRules.SourceValidationError("FULL", source, sourceProfile)
            |> capabilityProof }

    let private targetJoinProofs target (targetProfile: SqlProviderCapabilityProfile | null) : JoinProofs =
        { RightJoin =
            SqlJoinCapabilityRules.TargetValidationError("RIGHT", target, targetProfile)
            |> capabilityProof
          FullJoin =
            SqlJoinCapabilityRules.TargetValidationError("FULL", target, targetProfile)
            |> capabilityProof }

    let private sourceSemantics source (sourceProfile: SqlProviderCapabilityProfile | null) : RewriteParser.SourceSemantics =
        { MySqlPipes =
            if SqlConcatCapabilityRules.SupportsMySqlPipesAsConcat(source, sourceProfile) then
                RewriteParser.MySqlPipesSemantics.PipesAsConcat
            else
                RewriteParser.MySqlPipesSemantics.RejectAmbiguousPipes
          Joins = sourceJoinProofs source sourceProfile }

    let private targetRuntime target (targetProfile: SqlProviderCapabilityProfile | null) =
        match target with
        | SqlAgentToolType.Postgres -> TargetRuntime.PostgreSqlRuntime
        | SqlAgentToolType.MySQL -> TargetRuntime.MySqlRuntime
        | SqlAgentToolType.Sqlite -> TargetRuntime.SQLiteRuntime
        | SqlAgentToolType.Oracle -> TargetRuntime.OracleRuntime
        | SqlAgentToolType.Firebird -> TargetRuntime.FirebirdRuntime
        | SqlAgentToolType.MsSqlServer ->
            match SqlConcatCapabilityRules.EvaluateSqlServerTarget(targetProfile) with
            | SqlServerConcatTargetMode.NativePipes ->
                TargetRuntime.SqlServerRuntime(SqlServerConcatCapability.Proven SqlServerConcatLowering.NativePipes)
            | SqlServerConcatTargetMode.PlusOperator ->
                TargetRuntime.SqlServerRuntime(SqlServerConcatCapability.Proven SqlServerConcatLowering.PlusOperator)
            | SqlServerConcatTargetMode.Rejected ->
                TargetRuntime.SqlServerRuntime(
                    SqlServerConcatCapability.Unproven(
                        SqlConcatCapabilityRules.SqlServerTargetValidationError(targetProfile)))
            | value ->
                invalidOp ("Unsupported SQL Server concat target mode '" + string value + "'.")
        | value -> invalidArg "targetProvider" ("Unsupported target provider '" + string value + "'.")

    let private columnSetAssurance (columns: ImmutableArray<string>) =
        if columns.IsDefaultOrEmpty then
            ColumnSetAssurance.MissingAssurance
        else
            ColumnSetAssurance.AssuredColumns(columns |> Seq.toList)

    let private conflictProofs (assurance: DmlConflictTargetAssurance | null) : ConflictProofs =
        match assurance with
        | null ->
            { FirebirdPrimaryKey = ColumnSetAssurance.MissingAssurance
              SourceRowsUniqueByInsertColumns = ColumnSetAssurance.MissingAssurance }
        | value ->
            { FirebirdPrimaryKey = columnSetAssurance value.PrimaryKeyColumns
              SourceRowsUniqueByInsertColumns = columnSetAssurance value.SourceRowsUniqueByInsertColumns }

    let private parameters targetProvider (values: (obj | null) list) =
        let prefix = if targetProvider = SqlAgentToolType.Oracle then ":p" else "@p"
        values
        |> List.mapi (fun index value -> SqlParameterValue(prefix + string index, value))
        |> ImmutableArray.CreateRange

    let private allowedTables (tables: IReadOnlySet<string> | null) =
        match tables with
        | null -> None
        | values when values.Count = 0 -> None
        | values -> Some(values |> Seq.toList)

    let private queryPolicy queryMaxRows =
        let queryRows =
            if queryMaxRows <= 0 then RowCap.Unlimited
            else RowCap.MaxRows(PositiveRowCount.create queryMaxRows)
        { RewritePolicy.safeDefaults with QueryRows = queryRows }

    let private mutationSafety requireWhere allowFullTable =
        if not requireWhere && allowFullTable then MutationSafety.AllowAllRows
        else MutationSafety.RequirePredicate

    let private dmlPolicy (policy: DmlCompilationPolicy | null) =
        match policy with
        | null -> RewritePolicy.safeDefaults
        | value ->
            { RewritePolicy.safeDefaults with
                UpdateSafety = mutationSafety value.RequireWhereForUpdate value.AllowFullTableUpdate
                DeleteSafety = mutationSafety value.RequireWhereForDelete value.AllowFullTableDelete }

    let private compilationErrorMessage (message: string) =
        message.StartsWith("INSERT ", StringComparison.Ordinal)
        || message.StartsWith("CTE ", StringComparison.Ordinal)
        || message.StartsWith("Column reference", StringComparison.Ordinal)
        || message.StartsWith("COUNT(DISTINCT *)", StringComparison.Ordinal)
        || message.StartsWith("ORDER BY projection alias", StringComparison.Ordinal)
        || message.StartsWith("ORDER BY alias", StringComparison.Ordinal)
        || message.StartsWith("SQL capability", StringComparison.Ordinal)
        || message.Contains("not supported by the target provider", StringComparison.Ordinal)
        || message.Contains("requires provider", StringComparison.Ordinal)
        || message.Contains("cannot be safely lowered", StringComparison.Ordinal)
        || message.Contains("Pagination requires", StringComparison.Ordinal)
        || message.Contains("OFFSET pagination", StringComparison.Ordinal)

    let private run options sql =
        try RewritePipeline.compile options sql
        with
        | :? UnauthorizedAccessException -> reraise()
        | :? SqlCompilationException -> reraise()
        | :? InvalidOperationException as ex when compilationErrorMessage ex.Message ->
            raise (SqlCompilationException(ex.Message, ex))

    let private compile source target (sourceProfile: SqlProviderCapabilityProfile | null) (targetProfile: SqlProviderCapabilityProfile | null) (conflictTargetAssurance: DmlConflictTargetAssurance | null) policyVersion policy allowed sql =
        if String.IsNullOrWhiteSpace(sql) then invalidArg "sql" "SQL text cannot be empty."
        let rendered =
            run
                { RewritePipeline.CompileOptions.SourceDialect = sourceDialect source
                  SourceSemantics = sourceSemantics source sourceProfile
                  Provider = provider target
                  TargetRuntime = targetRuntime target targetProfile
                  TargetJoins = targetJoinProofs target targetProfile
                  ConflictProofs = conflictProofs conflictTargetAssurance
                  Policy = policy
                  AllowedTables = allowed }
                sql
        let parameterValues = parameters target rendered.Parameters
        let trimmed = sql.TrimStart()
        let kind =
            if trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("WITH", StringComparison.OrdinalIgnoreCase) then SqlStatementKind.Query
            elif trimmed.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("UPDATE OR INSERT", StringComparison.OrdinalIgnoreCase) then SqlStatementKind.Insert
            elif trimmed.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) then SqlStatementKind.Update
            elif trimmed.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase) then SqlStatementKind.Delete
            else invalidArg "sql" "Unsupported SQL statement."
        let command = CompiledSqlCommand(rendered.Sql, parameterValues, kind, String.Empty, target, ReturnsRows = rendered.ReturnsRows)
        let fingerprint = DmlFingerprintService.ComputePlanFingerprint(command, policyVersion)
        CompiledSqlCommand(rendered.Sql, parameterValues, kind, fingerprint, target, ReturnsRows = rendered.ReturnsRows)

    let compileQueryValidated sql source target (sourceProfile: SqlProviderCapabilityProfile | null) (targetProfile: SqlProviderCapabilityProfile | null) (validationContext: SqlPlanValidationContext) (executionPolicy: SqlExecutionPlanPolicy) =
        ArgumentNullException.ThrowIfNull(validationContext)
        ArgumentNullException.ThrowIfNull(executionPolicy)
        ArgumentException.ThrowIfNullOrWhiteSpace(validationContext.PolicyVersion)
        let command = compile source target sourceProfile targetProfile null validationContext.PolicyVersion (queryPolicy executionPolicy.QueryMaxRows) (allowedTables validationContext.AllowedTables) sql
        if command.Kind <> SqlStatementKind.Query then invalidArg "sql" "CompileQuery requires a SELECT statement."
        command

    let compileDmlValidated sql source target (sourceProfile: SqlProviderCapabilityProfile | null) (targetProfile: SqlProviderCapabilityProfile | null) (validationContext: SqlPlanValidationContext) (policy: DmlCompilationPolicy | null) (conflictTargetAssurance: DmlConflictTargetAssurance | null) =
        ArgumentNullException.ThrowIfNull(validationContext)
        ArgumentException.ThrowIfNullOrWhiteSpace(validationContext.PolicyVersion)
        let command = compile source target sourceProfile targetProfile conflictTargetAssurance validationContext.PolicyVersion (dmlPolicy policy) (allowedTables validationContext.AllowedTables) sql
        if command.Kind = SqlStatementKind.Query then invalidArg "sql" "CompileDml requires INSERT, UPDATE, or DELETE."
        command
