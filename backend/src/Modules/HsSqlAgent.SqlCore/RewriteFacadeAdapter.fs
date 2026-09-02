
namespace HsSqlAgent.SqlCore.Rewrite

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.Security.Cryptography
open System.Text
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Core.Execution
open HsSqlAgent.SqlCore.Core.Pipeline
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.SqlParsing
open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.RewritePolicy
open HsSqlAgent.SqlCore.Rewrite.Typestate

/// CLR boundary adapter. SQL text crosses into the closed F# DU exactly once here.
module internal RewriteFacadeAdapter =

    let private sourceDialect = function
        | SqlAgentToolType.Postgres -> RewriteParser.SourceDialect.PostgreSql
        | SqlAgentToolType.MySQL -> RewriteParser.SourceDialect.MySql
        | SqlAgentToolType.MsSqlServer -> RewriteParser.SourceDialect.SqlServer
        | SqlAgentToolType.Sqlite -> RewriteParser.SourceDialect.SQLite
        | SqlAgentToolType.Oracle -> RewriteParser.SourceDialect.Oracle
        | SqlAgentToolType.Firebird -> RewriteParser.SourceDialect.Firebird
        | value -> invalidArg "sourceDialect" ("Unsupported source dialect '" + string value + "'.")

    let private capabilityProof side (message: string | null) =
        match message with
        | null -> CapabilityProof.ProvenCapability
        | value ->
            CapabilityProof.RejectedCapability(
                CapabilityRejection.create side value)

    let private sourceCapabilityProof message =
        capabilityProof CapabilitySide.SourceCapability message

    let private targetCapabilityProof message =
        capabilityProof CapabilitySide.TargetCapability message

    let private rejectedTarget message =
        CapabilityProof.RejectedCapability(
            CapabilityRejection.create CapabilitySide.TargetCapability message)

    let private sourceJoinProofs source (sourceProfile: SqlProviderCapabilityProfile | null) : JoinProofs =
        { RightJoin =
            SqlJoinCapabilityRules.SourceValidationError("RIGHT", source, sourceProfile)
            |> sourceCapabilityProof
          FullJoin =
            SqlJoinCapabilityRules.SourceValidationError("FULL", source, sourceProfile)
            |> sourceCapabilityProof }

    let private targetJoinProofs target (targetProfile: SqlProviderCapabilityProfile | null) : JoinProofs =
        { RightJoin =
            SqlJoinCapabilityRules.TargetValidationError("RIGHT", target, targetProfile)
            |> targetCapabilityProof
          FullJoin =
            SqlJoinCapabilityRules.TargetValidationError("FULL", target, targetProfile)
            |> targetCapabilityProof }

    let private sourceOrderingProofs source : SourceOrderingProofs =
        { NullsFirst =
            SqlNullOrderingCapabilityRules.SourceValidationError(
                source,
                HsSqlAgent.SqlCore.Core.Ast.NullOrderingKind.First)
            |> sourceCapabilityProof
          NullsLast =
            SqlNullOrderingCapabilityRules.SourceValidationError(
                source,
                HsSqlAgent.SqlCore.Core.Ast.NullOrderingKind.Last)
            |> sourceCapabilityProof }

    let private targetNullOrdering target =
        if SqlNullOrderingCapabilityRules.RequiresTargetRewrite(target) then
            TargetNullOrdering.RewriteNullOrdering
        else
            TargetNullOrdering.NativeNullOrdering

    let private filterPredicateProofs provider side proof : FilterPredicateProofs =
        { OuterReference =
            SqlAggregateFilterCapabilityRules.PredicateValidationError(
                provider,
                side,
                SqlAggregateFilterPredicateFeature.OuterReference)
            |> proof
          Subquery =
            SqlAggregateFilterCapabilityRules.PredicateValidationError(
                provider,
                side,
                SqlAggregateFilterPredicateFeature.Subquery)
            |> proof
          WindowFunction =
            SqlAggregateFilterCapabilityRules.PredicateValidationError(
                provider,
                side,
                SqlAggregateFilterPredicateFeature.WindowFunction)
            |> proof }

    let private sourceRegexProof source =
        match SqlSourceFunctionRegistry.Find("REGEXP_LIKE") with
        | null -> invalidOp "REGEXP_LIKE source function contract is missing."
        | contract ->
            contract.ValidationError(source, 2)
            |> sourceCapabilityProof

    let private sourceExpressionProofs source (sourceProfile: SqlProviderCapabilityProfile | null) : ExpressionProofs =
        let filterError =
            match SqlAggregateFilterCapabilityRules.RawSourceSyntaxError(source) with
            | null -> SqlAggregateFilterCapabilityRules.ValidationError(source, sourceProfile, "source")
            | value -> value
        { ILike =
            SqlIlikeCapabilityRules.SourceValidationError(source)
            |> sourceCapabilityProof
          DistinctFrom = CapabilityProof.ProvenCapability
          IntervalLiteral =
            SqlIntervalLiteralCapabilityRules.SourceValidationError(source)
            |> sourceCapabilityProof
          RegexMatch = sourceRegexProof source
          AggregateFilter = filterError |> sourceCapabilityProof
          QuotedFunction =
            SqlQuotedFunctionCapabilityRules.SourceValidationError(source)
            |> sourceCapabilityProof
          QualifiedFunction =
            SqlQualifiedFunctionCapabilityRules.SourceValidationError(source)
            |> sourceCapabilityProof
          OffsetTimestamp = CapabilityProof.ProvenCapability
          FirebirdTimeZoneType =
            SqlFirebirdTimeZoneTypeCapabilityRules.CastSourceValidationError(source, sourceProfile)
            |> sourceCapabilityProof
          FirebirdExtendedDecimal = CapabilityProof.ProvenCapability
          StandaloneTime = CapabilityProof.ProvenCapability
          FilterPredicate = filterPredicateProofs source "source" sourceCapabilityProof }

    let private providerName = function
        | SqlAgentToolType.Postgres -> "Postgres"
        | SqlAgentToolType.MySQL -> "MySQL"
        | SqlAgentToolType.MsSqlServer -> "MsSqlServer"
        | SqlAgentToolType.Sqlite -> "Sqlite"
        | SqlAgentToolType.Oracle -> "Oracle"
        | SqlAgentToolType.Firebird -> "Firebird"
        | value -> string value

    let private targetExpressionProofs source target (targetProfile: SqlProviderCapabilityProfile | null) : ExpressionProofs =
        { ILike =
            if SqlIlikeCapabilityRules.SupportsTarget(target) then
                CapabilityProof.ProvenCapability
            else
                rejectedTarget (
                    "PostgreSQL-specific ILIKE cannot be lowered here: SQL capability 'operator.ilike' is not supported by provider "
                    + providerName target
                    + " for this Core plan.")
          DistinctFrom =
            SqlDistinctFromCapabilityRules.TargetValidationError(target, targetProfile)
            |> targetCapabilityProof
          IntervalLiteral =
            if SqlIntervalLiteralCapabilityRules.IsTargetSupported(target) then
                CapabilityProof.ProvenCapability
            else
                rejectedTarget (
                    "SQL capability 'expression.interval' is not supported by provider "
                    + providerName target
                    + " for this Core plan.")
          RegexMatch =
            SqlRegexCapabilityRules.TargetValidationError(target, targetProfile)
            |> targetCapabilityProof
          AggregateFilter =
            SqlAggregateFilterCapabilityRules.ValidationError(target, targetProfile, "target")
            |> targetCapabilityProof
          QuotedFunction =
            SqlQuotedFunctionCapabilityRules.TargetValidationError(source, target)
            |> targetCapabilityProof
          QualifiedFunction =
            SqlQualifiedFunctionCapabilityRules.TargetValidationError(source, target)
            |> targetCapabilityProof
          OffsetTimestamp =
            SqlOffsetTimestampCapabilityRules.TargetValidationError(target, targetProfile)
            |> targetCapabilityProof
          FirebirdTimeZoneType =
            SqlFirebirdTimeZoneTypeCapabilityRules.CastTargetValidationError(target, targetProfile)
            |> targetCapabilityProof
          FirebirdExtendedDecimal =
            if target <> SqlAgentToolType.Firebird
               || SqlFirebirdTimeZoneTypeCapabilityRules.SupportsTargetProfile(targetProfile) then
                CapabilityProof.ProvenCapability
            else
                rejectedTarget (
                    "SQL capability 'numeric.decimal_extended' requires an explicit Firebird target capability profile with ServerVersion 4.0 or newer.")
          StandaloneTime =
            SqlStandaloneTimeCapabilityRules.TargetValidationError(target)
            |> targetCapabilityProof
          FilterPredicate = filterPredicateProofs target "target" targetCapabilityProof }

    let private sourceDmlProofs source (sourceProfile: SqlProviderCapabilityProfile | null) : DmlProofs =
        let sourceVersion : Version | null =
            match sourceProfile with
            | null -> null
            | value -> value.ServerVersion
        { Returning =
            SqlDmlReturningCapabilityRules.SourceValidationError(source, sourceVersion)
            |> sourceCapabilityProof
          ReturningExpression =
            SqlDmlReturningExpressionCapabilityRules.SourceValidationError(source, sourceProfile)
            |> sourceCapabilityProof
          TargetAlias =
            SqlDmlTargetAliasCapabilityRules.SourceValidationError(source)
            |> sourceCapabilityProof
          UpdateFrom =
            SqlDmlUpdateFromCapabilityRules.SourceValidationError(source, sourceProfile)
            |> sourceCapabilityProof
          DeleteUsing =
            SqlDmlDeleteUsingCapabilityRules.SourceValidationError(source, sourceProfile)
            |> sourceCapabilityProof
          Merge =
            SqlDmlMergeCapabilityRules.SourceValidationError(source)
            |> sourceCapabilityProof
          SqlServerOutput = OutputAssuranceNotRequired }

    let private targetDmlProofs
        source
        target
        (targetProfile: SqlProviderCapabilityProfile | null)
        (resultRowAssurance: DmlResultRowAssurance | null) : DmlProofs =

        let returning =
            if source = SqlAgentToolType.MsSqlServer || target = SqlAgentToolType.MsSqlServer then
                if source <> target then
                    rejectedTarget (
                        "SQL Server OUTPUT and RETURNING row-image semantics are native-only until trigger timing is proven equivalent across providers. Source provider "
                        + string source
                        + ", target provider "
                        + string target
                        + ".")
                elif isNull resultRowAssurance then
                    rejectedTarget (
                        "SQL Server OUTPUT without INTO requires metadata-backed statement assurance proving the target table has no enabled trigger for the DML operation.")
                else
                    ProvenCapability
            else
                SqlDmlReturningCapabilityRules.TargetValidationError(target, targetProfile)
                |> targetCapabilityProof

        let outputAssurance =
            if target <> SqlAgentToolType.MsSqlServer then
                OutputAssuranceNotRequired
            elif isNull resultRowAssurance then
                MissingSqlServerOutputAssurance
            else
                AssuredNoEnabledOutputTriggers(
                    resultRowAssurance.TargetTable,
                    resultRowAssurance.Operation)

        { Returning = returning
          ReturningExpression =
            SqlDmlReturningExpressionCapabilityRules.TargetValidationError(target, targetProfile)
            |> targetCapabilityProof
          TargetAlias =
            SqlDmlTargetAliasCapabilityRules.TargetValidationError(target)
            |> targetCapabilityProof
          UpdateFrom =
            SqlDmlUpdateFromCapabilityRules.TargetValidationError(target, targetProfile)
            |> targetCapabilityProof
          DeleteUsing =
            SqlDmlDeleteUsingCapabilityRules.TargetValidationError(target, targetProfile)
            |> targetCapabilityProof
          Merge =
            SqlDmlMergeCapabilityRules.TargetValidationError(source, target)
            |> targetCapabilityProof
          SqlServerOutput = outputAssurance }

    let private sourceOnConflictProof source (sourceProfile: SqlProviderCapabilityProfile | null) =
        let sourceVersion : Version | null =
            match sourceProfile with
            | null -> null
            | value -> value.ServerVersion
        SqlDmlUpsertCapabilityRules.OnConflictSourceValidationError(source, sourceVersion)
        |> sourceCapabilityProof

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
          Backslash = backslash
          HashLineComment = grammar.SupportsLexicalFeature(SqlSourceLexicalFeatures.HashLineComment)
          DashDashCommentRequiresSeparator =
            grammar.SupportsLexicalFeature(SqlSourceLexicalFeatures.DashDashCommentRequiresSeparator)
          PostgresEscapeString =
            grammar.SupportsLexicalFeature(SqlSourceLexicalFeatures.PostgresEscapeString)
          PostgresDollarQuotedString =
            grammar.SupportsLexicalFeature(SqlSourceLexicalFeatures.PostgresDollarQuotedString)
          OracleQuotedString =
            grammar.SupportsLexicalFeature(SqlSourceLexicalFeatures.OracleQuotedString)
          HashPrefixedIdentifier =
            grammar.SupportsLexicalFeature(SqlSourceLexicalFeatures.HashPrefixedIdentifier) }

    let private sourceSemantics source (sourceProfile: SqlProviderCapabilityProfile | null) : RewriteParser.SourceSemantics =
        { EnforceDialectSyntax = true
          MySqlPipes =
            if SqlConcatCapabilityRules.SupportsMySqlPipesAsConcat(source, sourceProfile) then
                RewriteParser.MySqlPipesSemantics.PipesAsConcat
            else
                RewriteParser.MySqlPipesSemantics.RejectAmbiguousPipes
          MySqlNoBackslashEscapes =
            SqlSourceDialectGrammarRules.UsesMySqlNoBackslashEscapes(source, sourceProfile)
          MySqlNullSafeEqualitySyntax =
            SqlDistinctFromCapabilityRules.MySqlNullSafeEqualitySourceSyntaxValidationError(source)
            |> sourceCapabilityProof
          MySqlJsonArrowSyntax =
            SqlJsonCapabilityRules.MySqlArrowSourceValidationError(source, sourceProfile)
            |> sourceCapabilityProof
          PostgresJsonArrowSyntax =
            SqlJsonCapabilityRules.PostgresArrowSourceValidationError(source, sourceProfile)
            |> sourceCapabilityProof
          DistinctFromSyntax =
            SqlDistinctFromCapabilityRules.SourceSyntaxValidationError(source, sourceProfile)
            |> sourceCapabilityProof
          Joins = sourceJoinProofs source sourceProfile
          Expressions = sourceExpressionProofs source sourceProfile
          Dml = sourceDmlProofs source sourceProfile
          OnConflict = sourceOnConflictProof source sourceProfile
          Ordering = sourceOrderingProofs source
          FetchPercent =
            SqlFetchPercentCapabilityRules.SourceValidationError(source, sourceProfile)
            |> sourceCapabilityProof
          FetchWithTies =
            SqlFetchWithTiesCapabilityRules.SourceValidationError(source, sourceProfile)
            |> sourceCapabilityProof
          LateralDerivedTable =
            SqlLateralDerivedTableCapabilityRules.SourceValidationError(source, sourceProfile)
            |> sourceCapabilityProof
          RecursiveCte =
            SqlRecursiveCteCapabilityRules.SourceValidationError(source, sourceProfile)
            |> sourceCapabilityProof
          Lexical = sourceLexicalSemantics source sourceProfile }

    let parseSourceValidated sql source (sourceProfile: SqlProviderCapabilityProfile | null) =
        RewriteParser.parseForWith
            (sourceSemantics source sourceProfile)
            (sourceDialect source)
            sql

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

    let private structuralFingerprint (document: Document) =
        document
        |> sprintf "%A"
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    let internal determinismFacts
        sql
        source
        target
        (sourceProfile: SqlProviderCapabilityProfile | null)
        (targetProfile: SqlProviderCapabilityProfile | null) =

        if String.IsNullOrWhiteSpace(sql) then invalidArg "sql" "SQL text cannot be empty."
        if not (isNull sourceProfile) && sourceProfile.Provider <> source then
            invalidArg "sourceProfile" "Source capability profile provider does not match source dialect."
        if not (isNull targetProfile) && targetProfile.Provider <> target then
            invalidArg "targetProfile" "Target capability profile provider does not match target provider."

        let semantics = sourceSemantics source sourceProfile
        let parserDialect = sourceDialect source
        let runtime = targetRuntime target targetProfile
        let parsed = parseSourceValidated sql source sourceProfile
        let parsedDocument = Parsed.value parsed
        let canonical =
            parsed
            |> RewriteBinder.bind source
            |> RewriteStages.normalize
                semantics.EnforceDialectSyntax
                parserDialect
                runtime
                semantics.Expressions.RegexMatch
                semantics.Ordering
                semantics.MySqlPipes
                sourceProfile
                targetProfile
        let canonicalDocument = Canonical.value canonical
        let renormalized =
            RewriteStages.normalizeDocumentForInspection
                parserDialect
                runtime
                canonicalDocument
        let canonicalFingerprint = structuralFingerprint canonicalDocument
        let renormalizedFingerprint = structuralFingerprint renormalized

        SqlDeterminismInspectionFacts(
            structuralFingerprint parsedDocument,
            canonicalFingerprint,
            renormalizedFingerprint,
            StringComparer.Ordinal.Equals(canonicalFingerprint, renormalizedFingerprint))

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

    let private conflictProofs source target (targetProfile: SqlProviderCapabilityProfile | null) (assurance: DmlConflictTargetAssurance | null) : ConflictProofs =
        let firebirdPrimaryKey, sourceRows, mergeTargetKey =
            match assurance with
            | null ->
                ColumnSetAssurance.MissingAssurance,
                ColumnSetAssurance.MissingAssurance,
                ColumnSetAssurance.MissingAssurance
            | value ->
                let mergeColumns =
                    if not value.MatchedUniqueKeyColumns.IsDefaultOrEmpty then
                        value.MatchedUniqueKeyColumns
                    else
                        value.PrimaryKeyColumns
                columnSetAssurance value.PrimaryKeyColumns,
                columnSetAssurance value.SourceRowsUniqueByInsertColumns,
                columnSetAssurance mergeColumns
        { SourceProvider = source
          DirectTarget =
            SqlDmlUpsertCapabilityRules.DirectTargetValidationError(target, targetProfile)
            |> targetCapabilityProof
          MySqlConditionalTarget =
            SqlDmlUpsertCapabilityRules.MySqlConditionalTargetValidationError(targetProfile)
            |> targetCapabilityProof
          FirebirdPrimaryKey = firebirdPrimaryKey
          MySqlUniqueKey = mySqlUniqueKeyAssurance assurance
          SourceRowsUniqueByInsertColumns = sourceRows
          MergeTargetKey = mergeTargetKey }

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

    let private unknownQualifierError (message: string) =
        message.Contains("references unknown table/alias qualifier", StringComparison.Ordinal)

    let private diagnosticDataKey = "HsSqlAgent.SqlCore.Diagnostic"

    let private decisionBoundaryForStage = function
        | SqlDiagnosticStage.Lexical -> SqlCompileDecisionBoundary.Lexical
        | SqlDiagnosticStage.Parse -> SqlCompileDecisionBoundary.Parse
        | SqlDiagnosticStage.Binding -> SqlCompileDecisionBoundary.Binding
        | SqlDiagnosticStage.SourceValidation -> SqlCompileDecisionBoundary.SourceValidation
        | SqlDiagnosticStage.SemanticValidation -> SqlCompileDecisionBoundary.SemanticValidation
        | SqlDiagnosticStage.TargetCapability -> SqlCompileDecisionBoundary.TargetCapability
        | SqlDiagnosticStage.Policy -> SqlCompileDecisionBoundary.Policy
        | SqlDiagnosticStage.RenderingInvariant -> SqlCompileDecisionBoundary.RenderingInvariant
        | value -> invalidOp ("Unsupported SQL diagnostic stage '" + string value + "'.")

    let private exceptionDecisionBoundary (ex: exn) =
        let fromDiagnostic (diagnostic: SqlDiagnostic) =
            decisionBoundaryForStage diagnostic.Stage
        match ex with
        | :? SqlParseException as parseError when not (isNull parseError.Diagnostic) ->
            fromDiagnostic parseError.Diagnostic
        | :? SqlCompilationException as compilationError when not (isNull compilationError.Diagnostic) ->
            fromDiagnostic compilationError.Diagnostic
        | _ ->
            match ex.Data[diagnosticDataKey] with
            | :? SqlDiagnostic as diagnostic -> fromDiagnostic diagnostic
            | _ ->
                match ex with
                | :? SqlParseException -> SqlCompileDecisionBoundary.Parse
                | :? UnauthorizedAccessException -> SqlCompileDecisionBoundary.Policy
                | :? ArgumentException -> SqlCompileDecisionBoundary.InputValidation
                | :? InvalidOperationException -> SqlCompileDecisionBoundary.SemanticValidation
                | _ -> SqlCompileDecisionBoundary.InputValidation

    let private exceptionDecisionCode (ex: exn) =
        let diagnosticCode (diagnostic: SqlDiagnostic) = diagnostic.Code
        match ex with
        | :? SqlParseException as parseError when not (isNull parseError.Diagnostic) ->
            diagnosticCode parseError.Diagnostic
        | :? SqlCompilationException as compilationError when not (isNull compilationError.Diagnostic) ->
            diagnosticCode compilationError.Diagnostic
        | _ ->
            match ex.Data[diagnosticDataKey] with
            | :? SqlDiagnostic as diagnostic -> diagnosticCode diagnostic
            | _ ->
                match ex with
                | :? SqlParseException -> "SQL_PARSE_ERROR"
                | :? UnauthorizedAccessException -> "SQL_POLICY_DENIED"
                | :? SqlCompilationException -> "SQL_COMPILATION_ERROR"
                | :? ArgumentException -> "INVALID_ARGUMENT"
                | _ -> "SQLCORE_ERROR"

    let private attachRejectedEvidence context (ex: exn) =
        let existing = SqlCompileEvidence.TryGetFromException(ex)
        if isNull existing then
            let evidence =
                CompileEvidenceBuilder.build
                    context
                    SqlCompileVerdict.Rejected
                    (exceptionDecisionBoundary ex)
                    (exceptionDecisionCode ex)
                    null
            ex.Data[SqlCompileEvidence.DataKey] <- evidence

    let private attachReclassifiedEvidence
        (command: CompiledSqlCommand)
        decisionBoundary
        decisionCode
        (ex: exn) =

        match command.CompileEvidence with
        | null -> ()
        | translatedEvidence ->
            let rejectedEvidence =
                CompileEvidenceBuilder.reclassify
                    translatedEvidence
                    SqlCompileVerdict.Rejected
                    decisionBoundary
                    decisionCode
                    null
            ex.Data[SqlCompileEvidence.DataKey] <- rejectedEvidence

    let private compilationExceptionFrom (ex: InvalidOperationException) =
        match ex.Data[diagnosticDataKey] with
        | :? SqlDiagnostic as diagnostic ->
            SqlCompilationException(ex.Message, ex, diagnostic)
        | _ ->
            SqlCompilationException(ex.Message, ex)

    let private verifiedSource source semantics sourceProfile =
        RewritePipeline.VerifiedSource.create
            (sourceDialect source)
            semantics
            sourceProfile

    let private verifiedTarget source target targetProfile resultRowAssurance =
        RewritePipeline.VerifiedTarget.create
            (targetRuntime target targetProfile)
            targetProfile
            (targetExpressionProofs source target targetProfile)
            (targetJoinProofs target targetProfile)
            (targetNullOrdering target)
            (targetDmlProofs source target targetProfile resultRowAssurance)

    let private compileOptions source semantics target sourceProfile targetProfile conflictTargetAssurance resultRowAssurance policy allowed =
        RewritePipeline.createOptions
            (verifiedSource source semantics sourceProfile)
            (verifiedTarget source target targetProfile resultRowAssurance)
            (conflictProofs source target targetProfile conflictTargetAssurance)
            policy
            allowed

    let private compileEvidenceContext
        source
        target
        sourceProfile
        targetProfile
        conflictTargetAssurance
        resultRowAssurance
        policyVersion
        (policy: RewritePolicy.ExecutionPolicy)
        allowed =

        CompileEvidenceBuilder.create
            source
            target
            sourceProfile
            targetProfile
            conflictTargetAssurance
            resultRowAssurance
            policyVersion
            policy.QueryMaxRows
            (not policy.AllowUnboundedUpdate)
            (not policy.AllowUnboundedDelete)
            allowed

    let private run (options: RewritePipeline.CompileOptions) (sql: string) =
        try
            RewritePipeline.compileWithParsed options sql
        with
        | :? UnauthorizedAccessException -> reraise()
        | :? SqlCompilationException -> reraise()
        | :? SqlParseException -> reraise()
        | :? ArgumentException as ex when String.Equals(ex.ParamName, "sql", StringComparison.Ordinal) ->
            raise (SqlParseException(ex.Message, ex))
        | :? InvalidOperationException as ex when compilationErrorMessage ex.Message ->
            raise (compilationExceptionFrom ex)

    let private compile source target (sourceProfile: SqlProviderCapabilityProfile | null) (targetProfile: SqlProviderCapabilityProfile | null) (conflictTargetAssurance: DmlConflictTargetAssurance | null) (resultRowAssurance: DmlResultRowAssurance | null) policyVersion policy allowed sql =
        if String.IsNullOrWhiteSpace(sql) then invalidArg "sql" "SQL text cannot be empty."
        let evidenceContext =
            compileEvidenceContext
                source
                target
                sourceProfile
                targetProfile
                conflictTargetAssurance
                resultRowAssurance
                policyVersion
                policy
                allowed
        try
            let parsed, rendered =
                run
                    (compileOptions
                        source
                        (sourceSemantics source sourceProfile)
                        target
                        sourceProfile
                        targetProfile
                        conflictTargetAssurance
                        resultRowAssurance
                        policy
                        allowed)
                    sql
            let parameterValues = parameters target rendered.Parameters
            let kind = RewriteCompatibilityAstAdapter.kind parsed
            let command = CompiledSqlCommand.Create(rendered.Sql, parameterValues, kind, String.Empty, target, rendered.ReturnsRows)
            let fingerprint = DmlFingerprintService.ComputePlanFingerprint(command, policyVersion)
            let evidence =
                CompileEvidenceBuilder.build
                    evidenceContext
                    SqlCompileVerdict.Translated
                    SqlCompileDecisionBoundary.Completed
                    "SQL_COMPILE_TRANSLATED"
                    fingerprint
            CompiledSqlCommand.Create(
                rendered.Sql,
                parameterValues,
                kind,
                fingerprint,
                target,
                rendered.ReturnsRows,
                evidence)
        with ex ->
            attachRejectedEvidence evidenceContext ex
            reraise()

    let compileQueryValidated sql source target (sourceProfile: SqlProviderCapabilityProfile | null) (targetProfile: SqlProviderCapabilityProfile | null) (validationContext: SqlPlanValidationContext) (executionPolicy: SqlExecutionPlanPolicy) =
        ArgumentNullException.ThrowIfNull(validationContext)
        ArgumentNullException.ThrowIfNull(executionPolicy)
        ArgumentException.ThrowIfNullOrWhiteSpace(validationContext.PolicyVersion)
        let command = compile source target sourceProfile targetProfile null null validationContext.PolicyVersion (queryPolicy executionPolicy.QueryMaxRows) (allowedTables validationContext.AllowedTables) sql
        if command.Kind <> SqlStatementKind.Query then
            let error = ArgumentException("CompileQuery requires a SELECT statement.", "sql")
            attachReclassifiedEvidence
                command
                SqlCompileDecisionBoundary.InputValidation
                "SQL_API_STATEMENT_KIND_MISMATCH"
                error
            raise error
        command

    let compileDmlValidated sql source target (sourceProfile: SqlProviderCapabilityProfile | null) (targetProfile: SqlProviderCapabilityProfile | null) (validationContext: SqlPlanValidationContext) (policy: DmlCompilationPolicy | null) (conflictTargetAssurance: DmlConflictTargetAssurance | null) =
        ArgumentNullException.ThrowIfNull(validationContext)
        ArgumentException.ThrowIfNullOrWhiteSpace(validationContext.PolicyVersion)
        let command =
            try
                compile source target sourceProfile targetProfile conflictTargetAssurance validationContext.DmlResultRowAssurance validationContext.PolicyVersion (dmlPolicy policy) (allowedTables validationContext.AllowedTables) sql
            with
            | :? InvalidOperationException as ex when unknownQualifierError ex.Message ->
                raise (compilationExceptionFrom ex)
        if command.Kind = SqlStatementKind.Query then
            let error =
                SqlCompilationException(
                    "Unsupported DML statement: CompileDml requires INSERT, UPDATE, DELETE, or MERGE.")
            attachReclassifiedEvidence
                command
                SqlCompileDecisionBoundary.InputValidation
                "SQL_API_STATEMENT_KIND_MISMATCH"
                error
            raise error
        command


    let private legacyKind (statement: HsSqlAgent.SqlCore.Core.Ast.SqlStatement) =
        match statement with
        | :? HsSqlAgent.SqlCore.Core.Ast.SelectStatement
        | :? HsSqlAgent.SqlCore.Core.Ast.QueryStatement -> SqlStatementKind.Query
        | :? HsSqlAgent.SqlCore.Core.Ast.InsertStatement -> SqlStatementKind.Insert
        | :? HsSqlAgent.SqlCore.Core.Ast.UpdateStatement -> SqlStatementKind.Update
        | :? HsSqlAgent.SqlCore.Core.Ast.DeleteStatement -> SqlStatementKind.Delete
        | :? HsSqlAgent.SqlCore.Core.Ast.MergeStatement -> SqlStatementKind.Merge
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
            raise (compilationExceptionFrom ex)

    let private compileParsed (parsed: ParsedStatement) target (targetProfile: SqlProviderCapabilityProfile | null) (conflictTargetAssurance: DmlConflictTargetAssurance | null) (resultRowAssurance: DmlResultRowAssurance | null) policyVersion policy allowed =
        let source = parsed.SourceDialect
        let evidenceContext =
            compileEvidenceContext
                source
                target
                parsed.SourceProfile
                targetProfile
                conflictTargetAssurance
                resultRowAssurance
                policyVersion
                policy
                allowed
        try
            if parsed.EnforceSourceDialectSyntax && not (String.IsNullOrWhiteSpace(parsed.RawSql)) then
                // Preserve the source-dialect syntax check without making RawSql the executable source
                // of truth. Callers of the compatibility ParsedStatement API may intentionally replace
                // Statement after parsing; compilation must consume that statement, not silently reparse
                // the original text.
                parseSourceValidated parsed.RawSql source parsed.SourceProfile |> ignore
            let rendered =
                runParsed
                    (compileOptions
                        source
                        (parsedSourceSemantics parsed)
                        target
                        parsed.SourceProfile
                        targetProfile
                        conflictTargetAssurance
                        resultRowAssurance
                        policy
                        allowed)
                    (RewriteLegacyAstAdapter.toParsed parsed.Statement)
            let kind = legacyKind parsed.Statement
            let parameterValues = parameters target rendered.Parameters
            let command = CompiledSqlCommand.Create(rendered.Sql, parameterValues, kind, String.Empty, target, rendered.ReturnsRows)
            let fingerprint = DmlFingerprintService.ComputePlanFingerprint(command, policyVersion)
            let evidence =
                CompileEvidenceBuilder.build
                    evidenceContext
                    SqlCompileVerdict.Translated
                    SqlCompileDecisionBoundary.Completed
                    "SQL_COMPILE_TRANSLATED"
                    fingerprint
            CompiledSqlCommand.Create(
                rendered.Sql,
                parameterValues,
                kind,
                fingerprint,
                target,
                rendered.ReturnsRows,
                evidence)
        with ex ->
            attachRejectedEvidence evidenceContext ex
            reraise()

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
                null
                validationContext.PolicyVersion
                (queryPolicy executionPolicy.QueryMaxRows)
                (allowedTables validationContext.AllowedTables)
        if command.Kind <> SqlStatementKind.Query then
            let error = ArgumentException("CompileQuery requires a SELECT statement.", "parsed")
            attachReclassifiedEvidence
                command
                SqlCompileDecisionBoundary.InputValidation
                "SQL_API_STATEMENT_KIND_MISMATCH"
                error
            raise error
        command

    let compileDmlParsedValidated (parsed: ParsedStatement) target (targetProfile: SqlProviderCapabilityProfile | null) (validationContext: SqlPlanValidationContext) (policy: DmlCompilationPolicy | null) (conflictTargetAssurance: DmlConflictTargetAssurance | null) =
        ArgumentNullException.ThrowIfNull(parsed)
        ArgumentNullException.ThrowIfNull(validationContext)
        ArgumentException.ThrowIfNullOrWhiteSpace(validationContext.PolicyVersion)
        let command =
            try
                compileParsed
                    parsed
                    target
                    targetProfile
                    conflictTargetAssurance
                    validationContext.DmlResultRowAssurance
                    validationContext.PolicyVersion
                    (dmlPolicy policy)
                    (allowedTables validationContext.AllowedTables)
            with
            | :? InvalidOperationException as ex when unknownQualifierError ex.Message ->
                raise (compilationExceptionFrom ex)
        if command.Kind = SqlStatementKind.Query then
            let error =
                SqlCompilationException(
                    "Unsupported DML statement: CompileDml requires INSERT, UPDATE, DELETE, or MERGE.")
            attachReclassifiedEvidence
                command
                SqlCompileDecisionBoundary.InputValidation
                "SQL_API_STATEMENT_KIND_MISMATCH"
                error
            raise error
        command
