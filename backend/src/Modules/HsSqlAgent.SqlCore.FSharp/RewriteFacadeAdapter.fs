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

    let private sourceOrderingProofs source : SourceOrderingProofs =
        { NullsFirst =
            SqlNullOrderingCapabilityRules.SourceValidationError(
                source,
                HsSqlAgent.SqlCore.Core.Ast.NullOrderingKind.First)
            |> capabilityProof
          NullsLast =
            SqlNullOrderingCapabilityRules.SourceValidationError(
                source,
                HsSqlAgent.SqlCore.Core.Ast.NullOrderingKind.Last)
            |> capabilityProof }

    let private targetNullOrdering target =
        if SqlNullOrderingCapabilityRules.RequiresTargetRewrite(target) then
            TargetNullOrdering.RewriteNullOrdering
        else
            TargetNullOrdering.NativeNullOrdering

    let private filterPredicateProofs provider side : FilterPredicateProofs =
        { OuterReference =
            SqlAggregateFilterCapabilityRules.PredicateValidationError(
                provider,
                side,
                SqlAggregateFilterPredicateFeature.OuterReference)
            |> capabilityProof
          Subquery =
            SqlAggregateFilterCapabilityRules.PredicateValidationError(
                provider,
                side,
                SqlAggregateFilterPredicateFeature.Subquery)
            |> capabilityProof
          WindowFunction =
            SqlAggregateFilterCapabilityRules.PredicateValidationError(
                provider,
                side,
                SqlAggregateFilterPredicateFeature.WindowFunction)
            |> capabilityProof }

    let private sourceRegexProof source =
        match SqlSourceFunctionRegistry.Find("REGEXP_LIKE") with
        | null -> invalidOp "REGEXP_LIKE source function contract is missing."
        | contract ->
            contract.ValidationError(source, 2)
            |> capabilityProof

    let private sourceExpressionProofs source (sourceProfile: SqlProviderCapabilityProfile | null) : ExpressionProofs =
        let filterError =
            match SqlAggregateFilterCapabilityRules.RawSourceSyntaxError(source) with
            | null -> SqlAggregateFilterCapabilityRules.ValidationError(source, sourceProfile, "source")
            | value -> value
        { ILike =
            SqlIlikeCapabilityRules.SourceValidationError(source)
            |> capabilityProof
          IntervalLiteral =
            SqlIntervalLiteralCapabilityRules.SourceValidationError(source)
            |> capabilityProof
          RegexMatch = sourceRegexProof source
          AggregateFilter = filterError |> capabilityProof
          OffsetTimestamp = CapabilityProof.ProvenCapability
          FirebirdTimeZoneType = CapabilityProof.ProvenCapability
          FirebirdExtendedDecimal = CapabilityProof.ProvenCapability
          StandaloneTime = CapabilityProof.ProvenCapability
          FilterPredicate = filterPredicateProofs source "source" }

    let private providerName = function
        | SqlAgentToolType.Postgres -> "Postgres"
        | SqlAgentToolType.MySQL -> "MySQL"
        | SqlAgentToolType.MsSqlServer -> "MsSqlServer"
        | SqlAgentToolType.Sqlite -> "Sqlite"
        | SqlAgentToolType.Oracle -> "Oracle"
        | SqlAgentToolType.Firebird -> "Firebird"
        | value -> string value

    let private targetExpressionProofs target (targetProfile: SqlProviderCapabilityProfile | null) : ExpressionProofs =
        { ILike =
            if SqlIlikeCapabilityRules.SupportsTarget(target) then
                CapabilityProof.ProvenCapability
            else
                CapabilityProof.RejectedCapability(
                    "SQL capability 'operator.ilike' is not supported by provider "
                    + providerName target
                    + " for this Core plan.")
          IntervalLiteral =
            if SqlIntervalLiteralCapabilityRules.IsTargetSupported(target) then
                CapabilityProof.ProvenCapability
            else
                CapabilityProof.RejectedCapability(
                    "SQL capability 'expression.interval' is not supported by provider "
                    + providerName target
                    + " for this Core plan.")
          RegexMatch =
            SqlRegexCapabilityRules.TargetValidationError(target, targetProfile)
            |> capabilityProof
          AggregateFilter =
            SqlAggregateFilterCapabilityRules.ValidationError(target, targetProfile, "target")
            |> capabilityProof
          OffsetTimestamp =
            SqlOffsetTimestampCapabilityRules.TargetValidationError(target, targetProfile)
            |> capabilityProof
          FirebirdTimeZoneType =
            SqlFirebirdTimeZoneTypeCapabilityRules.CastTargetValidationError(
                target,
                targetProfile,
                "TIMESTAMP WITH TIME ZONE")
            |> capabilityProof
          FirebirdExtendedDecimal =
            if target <> SqlAgentToolType.Firebird
               || SqlFirebirdTimeZoneTypeCapabilityRules.SupportsTargetProfile(targetProfile) then
                CapabilityProof.ProvenCapability
            else
                CapabilityProof.RejectedCapability(
                    "SQL capability 'numeric.decimal_extended' requires an explicit Firebird target capability profile with ServerVersion 4.0 or newer.")
          StandaloneTime =
            SqlStandaloneTimeCapabilityRules.TargetValidationError(target)
            |> capabilityProof
          FilterPredicate = filterPredicateProofs target "target" }

    let private sourceDmlProofs source (sourceProfile: SqlProviderCapabilityProfile | null) : DmlProofs =
        let sourceVersion : Version | null =
            match sourceProfile with
            | null -> null
            | value -> value.ServerVersion
        { Returning =
            SqlDmlReturningCapabilityRules.SourceValidationError(source, sourceVersion)
            |> capabilityProof
          ReturningExpression =
            SqlDmlReturningExpressionCapabilityRules.SourceValidationError(source)
            |> capabilityProof
          UpdateFrom = CapabilityProof.ProvenCapability
          DeleteUsing = CapabilityProof.ProvenCapability }

    let private targetDmlProofs target (targetProfile: SqlProviderCapabilityProfile | null) : DmlProofs =
        { Returning =
            SqlDmlReturningCapabilityRules.TargetValidationError(target, targetProfile)
            |> capabilityProof
          ReturningExpression =
            SqlDmlReturningExpressionCapabilityRules.TargetValidationError(target)
            |> capabilityProof
          UpdateFrom =
            SqlDmlUpdateFromCapabilityRules.TargetValidationError(target)
            |> capabilityProof
          DeleteUsing =
            SqlDmlDeleteUsingCapabilityRules.TargetValidationError(target)
            |> capabilityProof }

    let private sourceOnConflictProof source (sourceProfile: SqlProviderCapabilityProfile | null) =
        let sourceVersion : Version | null =
            match sourceProfile with
            | null -> null
            | value -> value.ServerVersion
        SqlDmlUpsertCapabilityRules.OnConflictSourceValidationError(source, sourceVersion)
        |> capabilityProof

    let private sourceLexicalSemantics source (sourceProfile: SqlProviderCapabilityProfile | null) : RewriteLexer.LexicalSemantics =
        let grammar = SqlSourceDialectGrammarRules.For(source)
        let delimiter feature =
            if grammar.SupportsLexicalFeature(feature) then
                RewriteLexer.IdentifierDelimiterSemantics.AllowIdentifierDelimiter
            else
                RewriteLexer.IdentifierDelimiterSemantics.RejectIdentifierDelimiter
        let doubleQuote =
            if grammar.SupportsLexicalFeature(SqlSourceLexicalFeatures.DoubleQuotedIdentifierRequiresAnsiMode)
               && not (SqlSourceDialectGrammarRules.UsesMySqlAnsiQuotedIdentifiers(source, sourceProfile)) then
                RewriteLexer.DoubleQuoteSemantics.RejectMySqlDoubleQuoteAmbiguity
            else
                RewriteLexer.DoubleQuoteSemantics.AllowDoubleQuotedIdentifier
        let backslash =
            if grammar.SupportsLexicalFeature(SqlSourceLexicalFeatures.BackslashSensitiveQuotedText)
               && not (SqlSourceDialectGrammarRules.UsesMySqlNoBackslashEscapes(source, sourceProfile)) then
                RewriteLexer.BackslashSemantics.RejectMySqlBackslashAmbiguity
            else
                RewriteLexer.BackslashSemantics.BackslashIsLiteral
        { DoubleQuote = doubleQuote
          Backtick = delimiter SqlSourceLexicalFeatures.BacktickQuotedIdentifier
          Bracket = delimiter SqlSourceLexicalFeatures.BracketQuotedIdentifier
          Backslash = backslash }

    let private sourceSemantics source (sourceProfile: SqlProviderCapabilityProfile | null) : RewriteParser.SourceSemantics =
        { EnforceDialectSyntax = true
          MySqlPipes =
            if SqlConcatCapabilityRules.SupportsMySqlPipesAsConcat(source, sourceProfile) then
                RewriteParser.MySqlPipesSemantics.PipesAsConcat
            else
                RewriteParser.MySqlPipesSemantics.RejectAmbiguousPipes
          Joins = sourceJoinProofs source sourceProfile
          Expressions = sourceExpressionProofs source sourceProfile
          Dml = sourceDmlProofs source sourceProfile
          OnConflict = sourceOnConflictProof source sourceProfile
          Ordering = sourceOrderingProofs source
          Lexical = sourceLexicalSemantics source sourceProfile }

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

    let private mySqlUniqueKeyAssurance (assurance: DmlConflictTargetAssurance | null) =
        match assurance with
        | null -> MySqlUniqueKeyAssurance.MissingMySqlUniqueKeyAssurance
        | value when value.MatchedUniqueKeyColumns.IsDefaultOrEmpty ->
            MySqlUniqueKeyAssurance.MissingMySqlUniqueKeyAssurance
        | value ->
            MySqlUniqueKeyAssurance.AssuredMySqlUniqueKey(
                value.MatchedUniqueKeyColumns |> Seq.toList,
                value.IsSoleEnforcedUniqueKey)

    let private conflictProofs target (targetProfile: SqlProviderCapabilityProfile | null) (assurance: DmlConflictTargetAssurance | null) : ConflictProofs =
        let firebirdPrimaryKey, sourceRows =
            match assurance with
            | null ->
                ColumnSetAssurance.MissingAssurance,
                ColumnSetAssurance.MissingAssurance
            | value ->
                columnSetAssurance value.PrimaryKeyColumns,
                columnSetAssurance value.SourceRowsUniqueByInsertColumns
        { DirectTarget =
            SqlDmlUpsertCapabilityRules.DirectTargetValidationError(target, targetProfile)
            |> capabilityProof
          MySqlConditionalTarget =
            SqlDmlUpsertCapabilityRules.MySqlConditionalTargetValidationError(targetProfile)
            |> capabilityProof
          FirebirdPrimaryKey = firebirdPrimaryKey
          MySqlUniqueKey = mySqlUniqueKeyAssurance assurance
          SourceRowsUniqueByInsertColumns = sourceRows }

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
                  TargetExpressions = targetExpressionProofs target targetProfile
                  TargetJoins = targetJoinProofs target targetProfile
                  TargetOrdering = targetNullOrdering target
                  TargetDml = targetDmlProofs target targetProfile
                  ConflictProofs = conflictProofs target targetProfile conflictTargetAssurance
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


    let private legacyKind (statement: HsSqlAgent.SqlCore.Core.Ast.SqlStatement) =
        match statement with
        | :? HsSqlAgent.SqlCore.Core.Ast.SelectStatement
        | :? HsSqlAgent.SqlCore.Core.Ast.QueryStatement -> SqlStatementKind.Query
        | :? HsSqlAgent.SqlCore.Core.Ast.InsertStatement -> SqlStatementKind.Insert
        | :? HsSqlAgent.SqlCore.Core.Ast.UpdateStatement -> SqlStatementKind.Update
        | :? HsSqlAgent.SqlCore.Core.Ast.DeleteStatement -> SqlStatementKind.Delete
        | value -> raise (SqlCompilationException("Unsupported legacy statement '" + value.GetType().Name + "'."))

    let private parsedSourceSemantics (parsed: ParsedStatement) =
        if parsed.EnforceSourceDialectSyntax then
            sourceSemantics parsed.SourceDialect parsed.SourceProfile
        else
            RewriteParser.SourceSemantics.defaultValue

    let private runParsed options parsed =
        try RewritePipeline.compileParsed options parsed
        with
        | :? UnauthorizedAccessException -> reraise()
        | :? SqlCompilationException -> reraise()
        | :? InvalidOperationException as ex when compilationErrorMessage ex.Message ->
            raise (SqlCompilationException(ex.Message, ex))

    let private compileParsed (parsed: ParsedStatement) target (targetProfile: SqlProviderCapabilityProfile | null) (conflictTargetAssurance: DmlConflictTargetAssurance | null) policyVersion policy allowed =
        let source = parsed.SourceDialect
        let rendered =
            runParsed
                { RewritePipeline.CompileOptions.SourceDialect = sourceDialect source
                  SourceSemantics = parsedSourceSemantics parsed
                  Provider = provider target
                  TargetRuntime = targetRuntime target targetProfile
                  TargetExpressions = targetExpressionProofs target targetProfile
                  TargetJoins = targetJoinProofs target targetProfile
                  TargetOrdering = targetNullOrdering target
                  TargetDml = targetDmlProofs target targetProfile
                  ConflictProofs = conflictProofs target targetProfile conflictTargetAssurance
                  Policy = policy
                  AllowedTables = allowed }
                (RewriteLegacyAstAdapter.toParsed parsed.Statement)
        let kind = legacyKind parsed.Statement
        let parameterValues = parameters target rendered.Parameters
        let command = CompiledSqlCommand(rendered.Sql, parameterValues, kind, String.Empty, target, ReturnsRows = rendered.ReturnsRows)
        let fingerprint = DmlFingerprintService.ComputePlanFingerprint(command, policyVersion)
        CompiledSqlCommand(rendered.Sql, parameterValues, kind, fingerprint, target, ReturnsRows = rendered.ReturnsRows)

    let compileQueryParsedValidated (parsed: ParsedStatement) target (targetProfile: SqlProviderCapabilityProfile | null) (validationContext: SqlPlanValidationContext) (executionPolicy: SqlExecutionPlanPolicy) =
        ArgumentNullException.ThrowIfNull(parsed)
        ArgumentNullException.ThrowIfNull(validationContext)
        ArgumentNullException.ThrowIfNull(executionPolicy)
        ArgumentException.ThrowIfNullOrWhiteSpace(validationContext.PolicyVersion)
        let command =
            compileParsed
                parsed
                target
                targetProfile
                null
                validationContext.PolicyVersion
                (queryPolicy executionPolicy.QueryMaxRows)
                (allowedTables validationContext.AllowedTables)
        if command.Kind <> SqlStatementKind.Query then
            invalidArg "parsed" "CompileQuery requires a SELECT statement."
        command

    let compileDmlParsedValidated (parsed: ParsedStatement) target (targetProfile: SqlProviderCapabilityProfile | null) (validationContext: SqlPlanValidationContext) (policy: DmlCompilationPolicy | null) (conflictTargetAssurance: DmlConflictTargetAssurance | null) =
        ArgumentNullException.ThrowIfNull(parsed)
        ArgumentNullException.ThrowIfNull(validationContext)
        ArgumentException.ThrowIfNullOrWhiteSpace(validationContext.PolicyVersion)
        let command =
            compileParsed
                parsed
                target
                targetProfile
                conflictTargetAssurance
                validationContext.PolicyVersion
                (dmlPolicy policy)
                (allowedTables validationContext.AllowedTables)
        if command.Kind = SqlStatementKind.Query then
            invalidArg "parsed" "CompileDml requires INSERT, UPDATE, or DELETE."
        command
