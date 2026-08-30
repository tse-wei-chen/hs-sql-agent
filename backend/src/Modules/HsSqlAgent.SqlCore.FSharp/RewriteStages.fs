namespace HsSqlAgent.SqlCore.Rewrite

open System
open System.Collections.Generic
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.SqlTranslation.DateFormats
open HsSqlAgent.SqlCore.SqlTranslation.Functions
open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.Typestate

module internal RewriteStages =

    let private functionRegistry : IFunctionRegistry =
        FunctionRegistry(FunctionDefinitionLoader.LoadEmbedded()) :> IFunctionRegistry

    let private dateFormats = DateFormatTranslator()

    let private sourceProvider = function
        | RewriteParser.SourceDialect.PostgreSql -> SqlAgentToolType.Postgres
        | RewriteParser.SourceDialect.MySql -> SqlAgentToolType.MySQL
        | RewriteParser.SourceDialect.SqlServer -> SqlAgentToolType.MsSqlServer
        | RewriteParser.SourceDialect.SQLite -> SqlAgentToolType.Sqlite
        | RewriteParser.SourceDialect.Oracle -> SqlAgentToolType.Oracle
        | RewriteParser.SourceDialect.Firebird -> SqlAgentToolType.Firebird

    let private targetProvider = function
        | TargetRuntime.PostgreSqlRuntime -> SqlAgentToolType.Postgres
        | TargetRuntime.MySqlRuntime -> SqlAgentToolType.MySQL
        | TargetRuntime.SqlServerRuntime _ -> SqlAgentToolType.MsSqlServer
        | TargetRuntime.SQLiteRuntime -> SqlAgentToolType.Sqlite
        | TargetRuntime.OracleRuntime -> SqlAgentToolType.Oracle
        | TargetRuntime.FirebirdRuntime -> SqlAgentToolType.Firebird

    let private compilationError message =
        raise (SqlCompilationException(message))

    let private requireSourceRegexCapability = function
        | ProvenCapability -> ()
        | RejectedCapability message -> raise (SqlCompilationException(message))

    let rec private verifySourceRegexExpr regexProof expression =
        match expression with
        | Column _ | BoundColumn _ | Wildcard _ | OrderOrdinal _ | Literal _ | Interval _ -> ()
        | RawRegexCall(arguments, _) ->
            arguments |> List.iter (verifySourceRegexExpr regexProof)
            requireSourceRegexCapability regexProof
        | RegexMatch(value, pattern) ->
            verifySourceRegexExpr regexProof value
            verifySourceRegexExpr regexProof pattern
        | Unary(_, operand) -> verifySourceRegexExpr regexProof operand
        | Binary(_, left, right) ->
            verifySourceRegexExpr regexProof left
            verifySourceRegexExpr regexProof right
        | Like(value, pattern, _, _, _) ->
            verifySourceRegexExpr regexProof value
            verifySourceRegexExpr regexProof pattern
        | FunctionCall call ->
            call.Arguments |> List.iter (verifySourceRegexExpr regexProof)
        | FilteredAggregate(value, predicate) ->
            verifySourceRegexExpr regexProof value
            verifySourceRegexExpr regexProof predicate
        | Windowed(value, window) ->
            verifySourceRegexExpr regexProof value
            window.PartitionBy |> List.iter (verifySourceRegexExpr regexProof)
            window.OrderBy |> List.iter (fun order -> verifySourceRegexExpr regexProof order.Expression)
        | Cast(value, _) | Extract(_, value) ->
            verifySourceRegexExpr regexProof value
        | SimpleCase(input, branches, fallback) ->
            verifySourceRegexExpr regexProof input
            branches |> NonEmpty.iter (fun branch ->
                verifySourceRegexExpr regexProof branch.Match
                verifySourceRegexExpr regexProof branch.Result)
            fallback |> Option.iter (verifySourceRegexExpr regexProof)
        | SearchedCase(branches, fallback) ->
            branches |> NonEmpty.iter (fun branch ->
                verifySourceRegexExpr regexProof branch.Condition
                verifySourceRegexExpr regexProof branch.Result)
            fallback |> Option.iter (verifySourceRegexExpr regexProof)
        | InList(value, items, _) ->
            verifySourceRegexExpr regexProof value
            items |> NonEmpty.iter (verifySourceRegexExpr regexProof)
        | InSubquery(value, query, _) ->
            verifySourceRegexExpr regexProof value
            verifySourceRegexQuery regexProof query
        | Between(value, lower, upper, _) ->
            verifySourceRegexExpr regexProof value
            verifySourceRegexExpr regexProof lower
            verifySourceRegexExpr regexProof upper
        | IsNull(value, _) ->
            verifySourceRegexExpr regexProof value
        | ScalarSubquery query | Exists(query, _) ->
            verifySourceRegexQuery regexProof query

    and private verifySourceRegexSource regexProof source =
        match source with
        | NamedTable _ | CteTable _ -> ()
        | DerivedTable(query, _) -> verifySourceRegexQuery regexProof query

    and private verifySourceRegexSelect regexProof select =
        select.Ctes |> List.iter (fun cte -> verifySourceRegexQuery regexProof cte.Query)
        select.Projection |> List.iter (fun item -> verifySourceRegexExpr regexProof item.Expression)
        select.From |> Option.iter (verifySourceRegexSource regexProof)
        select.Joins |> List.iter (fun join ->
            verifySourceRegexSource regexProof join.Source
            join.Predicate |> Option.iter (verifySourceRegexExpr regexProof))
        select.Where |> Option.iter (verifySourceRegexExpr regexProof)
        select.GroupBy |> List.iter (verifySourceRegexExpr regexProof)
        select.Having |> Option.iter (verifySourceRegexExpr regexProof)

    and private verifySourceRegexQuery regexProof query =
        verifySourceRegexSelect regexProof query.Head
        query.SetOperations |> List.iter (fun branch -> verifySourceRegexQuery regexProof branch.Query)
        query.OrderBy |> List.iter (fun order -> verifySourceRegexExpr regexProof order.Expression)

    let private verifySourceRegexDocument regexProof document =
        match document.Statement with
        | QueryStatement query -> verifySourceRegexQuery regexProof query
        | InsertStatement insert ->
            match insert.Input with
            | Values rows -> rows |> NonEmpty.iter (NonEmpty.iter (verifySourceRegexExpr regexProof))
            | QuerySource query -> verifySourceRegexQuery regexProof query
            | DefaultValues -> ()
            insert.Returning |> List.iter (fun item -> verifySourceRegexExpr regexProof item.Expression)
        | UpdateStatement update ->
            update.AssignmentItems |> NonEmpty.iter (fun assignment -> verifySourceRegexExpr regexProof assignment.Value)
            update.From |> List.iter (verifySourceRegexSource regexProof)
            update.Where |> Option.iter (verifySourceRegexExpr regexProof)
            update.Returning |> List.iter (fun item -> verifySourceRegexExpr regexProof item.Expression)
        | DeleteStatement delete ->
            delete.Using |> List.iter (verifySourceRegexSource regexProof)
            delete.Where |> Option.iter (verifySourceRegexExpr regexProof)
            delete.Returning |> List.iter (fun item -> verifySourceRegexExpr regexProof item.Expression)

    let private emptyFunction name arguments =
        { FunctionCall.Name = FunctionName.create name
          Arguments = arguments
          IsDistinct = false
          AggregateOrderBy = []
          AggregateOrderSyntax = AggregateOrderSyntax.NoAggregateOrder
          AggregateSeparator = None }

    let private textLiteral label = function
        | Literal(ScalarValue.Text value) -> value
        | _ -> compilationError (label + " must be a string literal.")

    let private keywordValue label = function
        | Column identifier
        | BoundColumn(identifier, _) ->
            let parts = Identifier.parts identifier
            if parts.Length <> 1 then compilationError (label + " must be an unquoted SQL keyword.")
            parts.Head.Value
        | Literal(ScalarValue.Text value) -> value
        | _ -> compilationError (label + " must be an unquoted SQL keyword.")

    let private dateOnlyOperand target expression =
        match target with
        | SqlAgentToolType.Oracle ->
            FunctionCall(emptyFunction "TRUNC" [ Cast(expression, CastType.create "DATE") ])
        | SqlAgentToolType.MySQL
        | SqlAgentToolType.Sqlite ->
            FunctionCall(emptyFunction "DATE" [ expression ])
        | SqlAgentToolType.Postgres
        | SqlAgentToolType.MsSqlServer
        | SqlAgentToolType.Firebird ->
            Cast(expression, CastType.create "DATE")
        | value ->
            compilationError ("Unsupported target provider '" + string value + "' for portable DATEDIFF DAY normalization.")

    let private canonicalCall (call: FunctionCall) name arguments =
        FunctionCall
            { call with
                Name = FunctionName.create name
                Arguments = arguments
                AggregateOrderSyntax = AggregateOrderSyntax.NoAggregateOrder
                AggregateSeparator = None }

    let rec private normalizeExpr source target expression =
        match expression with
        | Column _ | BoundColumn _ | Wildcard _ | OrderOrdinal _ | Literal _ | Interval _ -> expression
        | Unary(Positive, operand) -> normalizeExpr source target operand
        | Unary(op, operand) -> Unary(op, normalizeExpr source target operand)
        | Binary(op, left, right) -> Binary(op, normalizeExpr source target left, normalizeExpr source target right)
        | Like(value, pattern, escape, negated, insensitive) ->
            Like(normalizeExpr source target value, normalizeExpr source target pattern, escape, negated, insensitive)
        | RawRegexCall(arguments, _) ->
            let normalized = arguments |> List.map (normalizeExpr source target)
            match normalized with
            | [ value; pattern ] -> RegexMatch(value, pattern)
            | values ->
                compilationError (
                    "Function 'CORE_REGEX_MATCH' requires 2 argument(s); received "
                    + string values.Length
                    + ".")
        | RegexMatch(value, pattern) ->
            RegexMatch(normalizeExpr source target value, normalizeExpr source target pattern)
        | FunctionCall call ->
            normalizeFunction source target call
        | FilteredAggregate(value, predicate) ->
            FilteredAggregate(normalizeExpr source target value, normalizeExpr source target predicate)
        | Windowed(value, window) ->
            Windowed(normalizeExpr source target value, normalizeWindow source target window)
        | Cast(value, targetType) -> Cast(normalizeExpr source target value, targetType)
        | Extract(field, value) -> Extract(field, normalizeExpr source target value)
        | SimpleCase(input, branches, fallback) ->
            SimpleCase(
                normalizeExpr source target input,
                branches
                |> NonEmpty.map (fun (branch: SimpleCaseBranch) ->
                    { Match = normalizeExpr source target branch.Match
                      Result = normalizeExpr source target branch.Result }),
                fallback |> Option.map (normalizeExpr source target))
        | SearchedCase(branches, fallback) ->
            SearchedCase(
                branches
                |> NonEmpty.map (fun (branch: SearchedCaseBranch) ->
                    { Condition = normalizeExpr source target branch.Condition
                      Result = normalizeExpr source target branch.Result }),
                fallback |> Option.map (normalizeExpr source target))
        | InList(value, items, negated) ->
            InList(
                normalizeExpr source target value,
                items |> NonEmpty.map (normalizeExpr source target),
                negated)
        | InSubquery(value, query, negated) ->
            InSubquery(
                normalizeExpr source target value,
                normalizeQuery source target query,
                negated)
        | Between(value, lower, upper, negated) ->
            Between(
                normalizeExpr source target value,
                normalizeExpr source target lower,
                normalizeExpr source target upper,
                negated)
        | IsNull(value, negated) -> IsNull(normalizeExpr source target value, negated)
        | ScalarSubquery query -> ScalarSubquery(normalizeQuery source target query)
        | Exists(query, negated) -> Exists(normalizeQuery source target query, negated)

    and private normalizeFunction source target (call: FunctionCall) =
        let sourceTool = sourceProvider source
        let targetTool = targetProvider target
        let arguments = call.Arguments |> List.map (normalizeExpr source target)
        let orderBy = call.AggregateOrderBy |> List.map (normalizeOrderBy source target)
        let call = { call with Arguments = arguments; AggregateOrderBy = orderBy }
        let sourceName = FunctionName.value call.Name |> fun value -> value.Trim().ToUpperInvariant()

        let sourceContract = SqlSourceFunctionRegistry.Find(sourceName)
        let requireSourceContract () =
            match Option.ofObj sourceContract with
            | Some contract -> contract
            | None -> invalidOp ("Source function contract '" + sourceName + "' was unexpectedly absent.")

        if not (isNull sourceContract) then
            match (requireSourceContract ()).ValidationError(sourceTool, arguments.Length) with
            | null -> ()
            | message -> compilationError message

        let currentKind =
            match sourceName with
            | "CURRENT_DATE" -> Some SqlCurrentTemporalKind.Date
            | "CURRENT_TIME" -> Some SqlCurrentTemporalKind.Time
            | "CURRENT_TIMESTAMP" -> Some SqlCurrentTemporalKind.Timestamp
            | _ -> None

        match currentKind with
        | Some kind ->
            match SqlCurrentTemporalCapabilityRules.SourceValidationError(kind, sourceTool) with
            | null ->
                let canonical =
                    match kind with
                    | SqlCurrentTemporalKind.Date -> "CORE_CURRENT_DATE"
                    | SqlCurrentTemporalKind.Time -> "CORE_CURRENT_TIME"
                    | SqlCurrentTemporalKind.Timestamp -> "CORE_CURRENT_TIMESTAMP"
                    | value -> compilationError ("Unsupported current temporal kind '" + string value + "'.")
                if not arguments.IsEmpty then
                    compilationError (sourceName + " does not accept arguments.")
                canonicalCall call canonical []
            | message -> compilationError message
        | None when SqlDatePartCapabilityRules.IsRepresentedPart(sourceName) ->
            if arguments.Length <> 1 then
                compilationError (sourceName + " requires exactly 1 argument.")
            canonicalCall call "CORE_DATE_PART" [ Literal(ScalarValue.Text sourceName); arguments.Head ]
        | None when not (isNull sourceContract) ->
            match (requireSourceContract ()).CanonicalizationKind with
            | SqlSourceFunctionCanonicalizationKind.DateAdd ->
                if arguments.Length <> 3 then compilationError "DATEADD requires exactly 3 arguments."
                let unit =
                    keywordValue "DATEADD date-part unit" arguments[0]
                    |> fun value -> SqlDateMathCapabilityRules.NormalizeUnit(value, "DATEADD")
                canonicalCall call "CORE_DATE_ADD" [ Literal(ScalarValue.Text unit); arguments[1]; arguments[2] ]

            | SqlSourceFunctionCanonicalizationKind.DateDiff ->
                let portableDay startValue endValue =
                    let canonical =
                        canonicalCall
                            call
                            "CORE_DATE_DIFF"
                            [ Literal(ScalarValue.Text "DAY")
                              dateOnlyOperand targetTool startValue
                              dateOnlyOperand targetTool endValue ]
                    if targetTool = SqlAgentToolType.Sqlite then
                        Cast(canonical, CastType.create "INTEGER")
                    else canonical
                match arguments with
                | [ finish; startValue ] ->
                    portableDay startValue finish
                | [ unitExpr; startValue; finish ] ->
                    let unit =
                        keywordValue "DATEDIFF date-part unit" unitExpr
                        |> fun value -> SqlDateMathCapabilityRules.NormalizeUnit(value, "DATEDIFF")
                    if (sourceTool = SqlAgentToolType.MsSqlServer || sourceTool = SqlAgentToolType.Firebird)
                       && sourceTool = targetTool then
                        canonicalCall call "CORE_DATE_DIFF" [ Literal(ScalarValue.Text unit); startValue; finish ]
                    elif unit <> "DAY" then
                        compilationError (
                            "Cross-dialect DATEDIFF unit '" + unit + "' from " + string sourceTool + " to " + string targetTool
                            + " is not translated: SQL capability 'core_date_diff.unit." + unit.ToLowerInvariant()
                            + "' is not modeled losslessly. DAY is the currently modeled portable intersection.")
                    else
                        portableDay startValue finish
                | values ->
                    compilationError (
                        "DATEDIFF requires either the portable 2-argument (end, start) shape or the "
                        + "3-argument (unit, start, end) shape; received " + string values.Length + " arguments.")

            | SqlSourceFunctionCanonicalizationKind.DateFormat ->
                if arguments.Length <> 2 then compilationError "DATE_FORMAT/FORMAT requires exactly 2 arguments."
                let rawFormat = textLiteral "DATE_FORMAT format" arguments[1]
                let translated =
                    try dateFormats.Translate(rawFormat, sourceTool, targetTool)
                    with
                    | :? FormatException as ex ->
                        raise (SqlCompilationException(
                            "portable date formatting from " + string sourceTool + " to " + string targetTool + " is not supported: " + ex.Message,
                            ex))
                    | :? NotSupportedException as ex ->
                        raise (SqlCompilationException(
                            "portable date formatting from " + string sourceTool + " to " + string targetTool + " is not supported: " + ex.Message,
                            ex))
                canonicalCall call "CORE_DATE_FORMAT" [ arguments[0]; Literal(ScalarValue.Text translated) ]

            | SqlSourceFunctionCanonicalizationKind.DateParse ->
                if arguments.Length <> 2 then compilationError "TO_DATE requires exactly 2 arguments."
                let rawFormat = textLiteral "TO_DATE format" arguments[1]
                let translated =
                    try dateFormats.Translate(rawFormat, sourceTool, targetTool)
                    with
                    | :? FormatException as ex ->
                        raise (SqlCompilationException(
                            "formatted date parsing from " + string sourceTool + " to " + string targetTool + " is not supported: " + ex.Message,
                            ex))
                    | :? NotSupportedException as ex ->
                        raise (SqlCompilationException(
                            "formatted date parsing from " + string sourceTool + " to " + string targetTool + " is not supported: " + ex.Message,
                            ex))
                canonicalCall call "CORE_DATE_PARSE" [ arguments[0]; Literal(ScalarValue.Text translated) ]

            | SqlSourceFunctionCanonicalizationKind.Position ->
                if arguments.Length <> 2 then compilationError (sourceName + " requires exactly 2 arguments.")
                let canonicalArguments =
                    if sourceName = "STRPOS" || sourceName = "INSTR" then
                        [ arguments[0]; arguments[1] ]
                    else
                        [ arguments[1]; arguments[0] ]
                canonicalCall call "CORE_POSITION" canonicalArguments

            | SqlSourceFunctionCanonicalizationKind.JsonExtract ->
                if arguments.Length <> 2 then compilationError "JSON_EXTRACT requires exactly 2 arguments."
                canonicalCall call "CORE_JSON_EXTRACT" arguments

            | SqlSourceFunctionCanonicalizationKind.JsonSet ->
                if arguments.Length <> 3 then compilationError "JSON_SET requires exactly 3 arguments."
                canonicalCall call "CORE_JSON_SET" arguments

            | SqlSourceFunctionCanonicalizationKind.RegexMatch ->
                if arguments.Length <> 2 then
                    compilationError ("Function 'CORE_REGEX_MATCH' requires 2 argument(s); received " + string arguments.Length + ".")
                RegexMatch(arguments[0], arguments[1])

            | SqlSourceFunctionCanonicalizationKind.CurrentTimestamp ->
                if not arguments.IsEmpty then compilationError (sourceName + " does not accept arguments.")
                canonicalCall call "CORE_CURRENT_TIMESTAMP" []

            | SqlSourceFunctionCanonicalizationKind.StringAggregate ->
                if sourceName = "STRING_AGG" && arguments.Length <> 2 then
                    compilationError "STRING_AGG requires exactly 2 arguments."
                let normalizedArguments =
                    if sourceName = "GROUP_CONCAT" && sourceTool = SqlAgentToolType.MySQL then
                        if arguments.Length <> 1 then
                            compilationError (
                                "MySQL GROUP_CONCAT comma-separated arguments are multiple value expressions, not a separator. "
                                + "Core currently supports exactly one value expression; use portable STRING_AGG(value, separator) "
                                + "or native SEPARATOR 'literal' for an explicit delimiter.")
                        let separator = call.AggregateSeparator |> Option.defaultValue ","
                        [ arguments.Head; Literal(ScalarValue.Text separator) ]
                    elif arguments.Length = 1 then
                        let separator =
                            match sourceName with
                            | "LISTAGG" -> ""
                            | "GROUP_CONCAT"
                            | "LIST" -> ","
                            | _ -> compilationError ("String aggregate '" + sourceName + "' requires an explicit separator.")
                        [ arguments.Head; Literal(ScalarValue.Text separator) ]
                    else arguments
                canonicalCall call "CORE_STRING_AGG" normalizedArguments

            | value ->
                compilationError (
                    "Unsupported source function canonicalization kind '" + string value + "' for function '" + sourceName + "'.")

        | None when sourceName = "COALESCE" ->
            if arguments.Length < 2 then compilationError "COALESCE requires at least 2 arguments."
            FunctionCall { call with Name = FunctionName.create "COALESCE"; Arguments = arguments }

        | None when SqlCanonicalFunctionRegistry.IsDirectPortable(sourceName) ->
            FunctionCall { call with Name = FunctionName.create sourceName; Arguments = arguments }

        | None ->
            let sourceDefinition =
                match functionRegistry.Find(sourceTool, sourceName, arguments.Length) |> Option.ofObj with
                | Some definition -> definition
                | None ->
                    compilationError (
                        "Function '" + sourceName + "' is not registered for source dialect "
                        + string sourceTool + "; normalization was rejected.")
            if not sourceDefinition.Semantic.HasValue then
                compilationError ("Function '" + sourceName + "' has no portable semantic mapping from " + string sourceTool + ".")

            let semantic = sourceDefinition.Semantic.Value
            if sourceTool <> targetTool then
                match semantic with
                | SemanticFunction.Random ->
                    compilationError (
                        "Random function '" + sourceName + "' is not translated across dialects because providers differ in value range and evaluation frequency.")
                | SemanticFunction.StringLength when sourceTool = SqlAgentToolType.MsSqlServer ->
                    if arguments.Length <> 1 then compilationError "SQL Server LEN requires exactly 1 argument."
                    let targetLength =
                        match targetTool with
                        | SqlAgentToolType.Postgres
                        | SqlAgentToolType.Oracle
                        | SqlAgentToolType.Sqlite -> "LENGTH"
                        | SqlAgentToolType.MySQL
                        | SqlAgentToolType.Firebird -> "CHAR_LENGTH"
                        | value -> compilationError ("SQL Server LEN has no Core cross-dialect lowering for target provider " + string value + ".")
                    let trimmed = FunctionCall(emptyFunction "RTRIM" [ arguments.Head ])
                    FunctionCall { call with Name = FunctionName.create targetLength; Arguments = [ trimmed ] }
                | SemanticFunction.StringLength when targetTool = SqlAgentToolType.MsSqlServer ->
                    compilationError "Portable string length cannot be translated losslessly to SQL Server LEN because LEN excludes trailing spaces."
                | SemanticFunction.Repeat when sourceTool = SqlAgentToolType.MsSqlServer || targetTool = SqlAgentToolType.MsSqlServer ->
                    compilationError "REPLICATE/REPEAT is not translated across SQL Server because SQL Server REPLICATE can truncate non-MAX inputs."
                | SemanticFunction.Coalesce when sourceName <> "COALESCE" ->
                    compilationError (
                        "Provider-specific null function '" + sourceName
                        + "' is not translated across dialects because its type-conversion rules differ from COALESCE.")
                | _ ->
                    let targetDefinition =
                        match functionRegistry.Find(targetTool, semantic, arguments.Length) |> Option.ofObj with
                        | Some definition -> definition
                        | None ->
                            compilationError (
                                "Semantic function '" + string semantic + "' with " + string arguments.Length
                                + " argument(s) is not supported by " + string targetTool + ".")
                    if targetDefinition.TranslationKind = FunctionTranslationKind.Template
                       || targetDefinition.TranslationKind = FunctionTranslationKind.Specialized then
                        compilationError (
                            "Function '" + sourceName + "' requires Core " + string targetDefinition.TranslationKind
                            + " translation for target provider " + string targetTool
                            + "; no lossless Core translator is registered yet.")
                    FunctionCall { call with Name = FunctionName.create (targetDefinition.Name.Trim().ToUpperInvariant()); Arguments = arguments }
            else
                FunctionCall { call with Name = FunctionName.create sourceName; Arguments = arguments }

    and private normalizeWindow source target (window: WindowSpec) =
        { window with
            PartitionBy = window.PartitionBy |> List.map (normalizeExpr source target)
            OrderBy = window.OrderBy |> List.map (normalizeOrderBy source target) }

    and private normalizeOrderBy source target (orderBy: OrderBy) =
        { orderBy with Expression = normalizeExpr source target orderBy.Expression }

    and private normalizeSource sourceDialect target (source: TableSource) =
        match source with
        | NamedTable _ | CteTable _ -> source
        | DerivedTable(query, alias) -> DerivedTable(normalizeQuery sourceDialect target query, alias)

    and private normalizeJoin sourceDialect target (join: Join) =
        match join with
        | CrossJoin source -> CrossJoin(normalizeSource sourceDialect target source)
        | OnJoin(kind, source, predicate) ->
            OnJoin(
                kind,
                normalizeSource sourceDialect target source,
                normalizeExpr sourceDialect target predicate)

    and private normalizeCte sourceDialect target (cte: Cte) =
        let query = normalizeQuery sourceDialect target cte.Query
        if cte.ColumnAliases.IsEmpty then { cte with Query = query }
        else
            let projection = query.Head.Projection
            if projection |> List.exists (fun item -> match item.Expression with Wildcard _ -> true | _ -> false) then
                invalidOp ("CTE '" + cte.Name.Value + "' column aliases cannot be lowered safely when the CTE projection contains a wildcard.")
            if projection.Length <> cte.ColumnAliases.Length then
                invalidOp ("CTE '" + cte.Name.Value + "' declares " + string cte.ColumnAliases.Length + " column alias(es) but its statically modeled projection has " + string projection.Length + " column(s).")
            let rewritten =
                (projection, cte.ColumnAliases)
                ||> List.map2 (fun item alias -> { item with Alias = Some alias })
                |> NonEmpty.ofList "CTE projection"
            { cte with ColumnAliases = []; Query = { query with Head = { query.Head with ProjectionItems = rewritten } } }

    and private normalizeSelect sourceDialect target (select: Select) =
        { select with
            Ctes = select.Ctes |> List.map (normalizeCte sourceDialect target)
            ProjectionItems =
                select.ProjectionItems
                |> NonEmpty.map (fun (item: SelectItem) ->
                    { item with Expression = normalizeExpr sourceDialect target item.Expression })
            From = select.From |> Option.map (normalizeSource sourceDialect target)
            Joins = select.Joins |> List.map (normalizeJoin sourceDialect target)
            Where = select.Where |> Option.map (normalizeExpr sourceDialect target)
            GroupBy = select.GroupBy |> List.map (normalizeExpr sourceDialect target)
            Having = select.Having |> Option.map (normalizeExpr sourceDialect target) }

    and private normalizeQuery sourceDialect target (query: Query) =
        { query with
            Head = normalizeSelect sourceDialect target query.Head
            SetOperations =
                query.SetOperations
                |> List.map (fun (branch: SetBranch) ->
                    { branch with Query = normalizeQuery sourceDialect target branch.Query })
            OrderBy = query.OrderBy |> List.map (normalizeOrderBy sourceDialect target) }

    let private normalizeReturning source target items =
        items
        |> List.map (fun (item: SelectItem) ->
            { item with Expression = normalizeExpr source target item.Expression })

    let private normalizeDocument source target document =
        let statement =
            match document.Statement with
            | QueryStatement query -> QueryStatement(normalizeQuery source target query)
            | InsertStatement insert ->
                let input =
                    match insert.Input with
                    | Values rows -> Values(rows |> NonEmpty.map (NonEmpty.map (normalizeExpr source target)))
                    | QuerySource query -> QuerySource(normalizeQuery source target query)
                    | DefaultValues -> DefaultValues
                InsertStatement { insert with Input = input; Returning = normalizeReturning source target insert.Returning }
            | UpdateStatement update ->
                UpdateStatement
                    { update with
                        AssignmentItems =
                            update.AssignmentItems
                            |> NonEmpty.map (fun assignment ->
                                { assignment with Value = normalizeExpr source target assignment.Value })
                        From = update.From |> List.map (normalizeSource source target)
                        Where = update.Where |> Option.map (normalizeExpr source target)
                        Returning = normalizeReturning source target update.Returning }
            | DeleteStatement delete ->
                DeleteStatement
                    { delete with
                        Using = delete.Using |> List.map (normalizeSource source target)
                        Where = delete.Where |> Option.map (normalizeExpr source target)
                        Returning = normalizeReturning source target delete.Returning }
        { document with Statement = statement }

    let normalize sourceDialect targetRuntime sourceRegexProof bound =
        Transition.normalize
            (fun document ->
                verifySourceRegexDocument sourceRegexProof document
                normalizeDocument sourceDialect targetRuntime document)
            bound

    let private identifierText = Identifier.text

    let private ensureTableAllowed allowedTables identifier =
        match allowedTables with
        | None | Some [] -> ()
        | Some allowed ->
            let table = identifierText identifier
            if not (allowed |> List.exists (fun value -> StringComparer.OrdinalIgnoreCase.Equals(value, table))) then
                raise (UnauthorizedAccessException("SQL plan is not authorized to access table(s): " + table))

    let private isWildcard = function Wildcard _ -> true | _ -> false

    let private ensureNoDistinctWildcard (call: FunctionCall) =
        if call.IsDistinct && call.Arguments |> List.exists isWildcard then
            invalidOp "COUNT(DISTINCT *) is not a valid Core aggregate shape."

    let rec private validateExpr allowedTables expression =
        match expression with
        | Column _ | BoundColumn _ | Wildcard _ | OrderOrdinal _ | Literal _ | Interval _ -> ()
        | Unary(_, operand) -> validateExpr allowedTables operand
        | Binary(_, left, right) -> validateExpr allowedTables left; validateExpr allowedTables right
        | Like(value, pattern, _, _, _) ->
            validateExpr allowedTables value
            validateExpr allowedTables pattern
        | RawRegexCall _ ->
            invalidOp "Raw REGEXP_LIKE reached plan validation before canonicalization."
        | RegexMatch(value, pattern) ->
            validateExpr allowedTables value
            validateExpr allowedTables pattern
        | FunctionCall call ->
            ensureNoDistinctWildcard call
            call.Arguments |> List.iter (validateExpr allowedTables)
            call.AggregateOrderBy |> List.iter (fun order -> validateExpr allowedTables order.Expression)
        | FilteredAggregate(value, predicate) -> validateExpr allowedTables value; validateExpr allowedTables predicate
        | Windowed(value, window) ->
            validateExpr allowedTables value
            window.PartitionBy |> List.iter (validateExpr allowedTables)
            window.OrderBy |> List.iter (fun order -> validateExpr allowedTables order.Expression)
        | Cast(value, _) -> validateExpr allowedTables value
        | Extract(_, value) -> validateExpr allowedTables value
        | SimpleCase(input, branches, fallback) ->
            validateExpr allowedTables input
            branches |> NonEmpty.iter (fun branch -> validateExpr allowedTables branch.Match; validateExpr allowedTables branch.Result)
            fallback |> Option.iter (validateExpr allowedTables)
        | SearchedCase(branches, fallback) ->
            branches |> NonEmpty.iter (fun branch -> validateExpr allowedTables branch.Condition; validateExpr allowedTables branch.Result)
            fallback |> Option.iter (validateExpr allowedTables)
        | InList(value, items, _) -> validateExpr allowedTables value; items |> NonEmpty.iter (validateExpr allowedTables)
        | InSubquery(value, query, _) -> validateExpr allowedTables value; validateQuery allowedTables query
        | Between(value, lower, upper, _) -> validateExpr allowedTables value; validateExpr allowedTables lower; validateExpr allowedTables upper
        | IsNull(value, _) -> validateExpr allowedTables value
        | ScalarSubquery query -> validateQuery allowedTables query
        | Exists(query, _) -> validateQuery allowedTables query

    and private validateSource allowedTables source =
        match source with
        | NamedTable(identifier, _) -> ensureTableAllowed allowedTables identifier
        | CteTable _ -> ()
        | DerivedTable(query, _) -> validateQuery allowedTables query

    and private validateSelect allowedTables select =
        for cte in select.Ctes do validateQuery allowedTables cte.Query
        if select.From.IsNone && select.Joins.IsEmpty && select.Projection |> List.exists (fun item -> isWildcard item.Expression) then
            invalidOp "Column reference '*' requires a FROM source in the portable Core query model."
        select.From |> Option.iter (validateSource allowedTables)
        select.ProjectionItems |> NonEmpty.iter (fun item -> validateExpr allowedTables item.Expression)
        select.Where |> Option.iter (validateExpr allowedTables)
        select.GroupBy |> List.iter (validateExpr allowedTables)
        select.Having |> Option.iter (validateExpr allowedTables)
        select.Joins
        |> List.iter (function
            | CrossJoin source -> validateSource allowedTables source
            | OnJoin(_, source, predicate) -> validateSource allowedTables source; validateExpr allowedTables predicate)

    and private validateQuery allowedTables query =
        validateSelect allowedTables query.Head
        let duplicateAliases = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        let seenAliases = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        for item in query.Head.Projection do
            match item.Alias with
            | Some alias when not (seenAliases.Add alias.Value) -> duplicateAliases.Add alias.Value |> ignore
            | _ -> ()
        if duplicateAliases.Count > 0 then
            for order in query.OrderBy do
                match order.Expression with
                | Column identifier
                | BoundColumn(identifier, ProjectionAlias)
                    when Identifier.parts identifier |> List.length = 1 ->
                    let name = identifierText identifier
                    if duplicateAliases.Contains name then
                        if query.Head.From.IsNone && query.Head.Joins.IsEmpty then
                            invalidOp ("ORDER BY projection alias '" + name + "' is ambiguous in a no-FROM query.")
                        else
                            invalidOp ("ORDER BY alias '" + name + "' is ambiguous.")
                | _ -> ()
        query.SetOperations |> List.iter (fun branch -> validateQuery allowedTables branch.Query)
        query.OrderBy |> List.iter (fun order -> validateExpr allowedTables order.Expression)

    let rec private validateInsertValueScope expression =
        match expression with
        | Literal _ | Interval _ -> ()
        | ScalarSubquery _ | Exists _ -> ()
        | Column identifier
        | BoundColumn(identifier, _) ->
            invalidOp ("INSERT VALUES scalar expression cannot reference column '" + identifierText identifier + "' outside a scalar subquery; use INSERT ... SELECT when the inserted value depends on a source row.")
        | Wildcard _ | OrderOrdinal _ -> invalidOp "INSERT VALUES scalar expression cannot contain a wildcard or ORDER BY ordinal."
        | Unary(_, operand) -> validateInsertValueScope operand
        | Binary(_, left, right) -> validateInsertValueScope left; validateInsertValueScope right
        | Like(value, pattern, _, _, _) -> validateInsertValueScope value; validateInsertValueScope pattern
        | RawRegexCall _ -> invalidOp "Raw REGEXP_LIKE reached INSERT validation before canonicalization."
        | RegexMatch(value, pattern) -> validateInsertValueScope value; validateInsertValueScope pattern
        | FunctionCall call ->
            call.Arguments |> List.iter validateInsertValueScope
            call.AggregateOrderBy |> List.iter (fun order -> validateInsertValueScope order.Expression)
        | FilteredAggregate(value, predicate) -> validateInsertValueScope value; validateInsertValueScope predicate
        | Windowed(value, window) ->
            validateInsertValueScope value
            window.PartitionBy |> List.iter validateInsertValueScope
            window.OrderBy |> List.iter (fun order -> validateInsertValueScope order.Expression)
        | Cast(value, _) | Extract(_, value) -> validateInsertValueScope value
        | SimpleCase(input, branches, fallback) ->
            validateInsertValueScope input
            branches |> NonEmpty.iter (fun branch -> validateInsertValueScope branch.Match; validateInsertValueScope branch.Result)
            fallback |> Option.iter validateInsertValueScope
        | SearchedCase(branches, fallback) ->
            branches |> NonEmpty.iter (fun branch -> validateInsertValueScope branch.Condition; validateInsertValueScope branch.Result)
            fallback |> Option.iter validateInsertValueScope
        | InList(value, items, _) -> validateInsertValueScope value; items |> NonEmpty.iter validateInsertValueScope
        | InSubquery(value, _, _) -> validateInsertValueScope value
        | Between(value, lower, upper, _) -> validateInsertValueScope value; validateInsertValueScope lower; validateInsertValueScope upper
        | IsNull(value, _) -> validateInsertValueScope value

    let private projectionWidth query =
        if query.Head.Projection |> List.exists (fun item -> isWildcard item.Expression) then None
        else Some query.Head.Projection.Length

    let private validateInsertShape insert =
        match insert.Input with
        | DefaultValues -> ()
        | Values rows ->
            if insert.Columns.IsEmpty then invalidOp "INSERT VALUES requires explicit target columns."
            rows
            |> NonEmpty.iter (fun row ->
                if NonEmpty.length row <> insert.Columns.Length then invalidOp "INSERT VALUES row width does not match target column count."
                row |> NonEmpty.iter validateInsertValueScope)
        | QuerySource query ->
            if insert.Columns.IsEmpty then invalidOp "INSERT ... SELECT requires explicit target columns."
            match projectionWidth query with
            | None -> invalidOp "INSERT ... SELECT requires a statically known source projection width; wildcard projections are rejected at the Core validation boundary."
            | Some width when width <> insert.Columns.Length ->
                invalidOp ("INSERT ... SELECT projection width " + string width + " does not match target column count " + string insert.Columns.Length + ".")
            | _ -> ()

    let private validateReturning allowedTables items = items |> List.iter (fun item -> validateExpr allowedTables item.Expression)

    let private validateDocument allowedTables document =
        match document.Statement with
        | QueryStatement query -> validateQuery allowedTables query
        | InsertStatement insert ->
            ensureTableAllowed allowedTables insert.Target
            validateInsertShape insert
            match insert.Input with
            | Values rows -> rows |> NonEmpty.iter (NonEmpty.iter (validateExpr allowedTables))
            | QuerySource query -> validateQuery allowedTables query
            | DefaultValues -> ()
            validateReturning allowedTables insert.Returning
        | UpdateStatement update ->
            ensureTableAllowed allowedTables update.Target
            update.From |> List.iter (validateSource allowedTables)
            update.AssignmentItems |> NonEmpty.iter (fun assignment -> validateExpr allowedTables assignment.Value)
            update.Where |> Option.iter (validateExpr allowedTables)
            validateReturning allowedTables update.Returning
        | DeleteStatement delete ->
            ensureTableAllowed allowedTables delete.Target
            delete.Using |> List.iter (validateSource allowedTables)
            delete.Where |> Option.iter (validateExpr allowedTables)
            validateReturning allowedTables delete.Returning
        document

    let private proveTargetLiteral targetRuntime value =
        match targetRuntime, value with
        | FirebirdRuntime, ScalarValue.Text text when text.Length > 8191 ->
            raise (SqlCompilationException(
                "Firebird string literal exceeds the safe UTF8 VARCHAR limit of 8191 characters."))
        | _ -> ()

    let private proveSqlServerConcat targetRuntime =
        match targetRuntime with
        | SqlServerRuntime(Proven _) -> ()
        | SqlServerRuntime(Unproven message) -> invalidOp message
        | _ -> ()

    let private requireExpressionCapability = function
        | ProvenCapability -> ()
        | RejectedCapability message -> invalidOp message

    let private requireFilterCapability = function
        | ProvenCapability -> ()
        | RejectedCapability message -> raise (SqlCompilationException(message))

    let rec private proveFilterPredicate (proofs: FilterPredicateProofs) expression =
        match expression with
        | BoundColumn(_, OuterRowSource) ->
            requireFilterCapability proofs.OuterReference
        | Column _
        | BoundColumn(_, LocalRowSource)
        | BoundColumn(_, ProjectionAlias)
        | Wildcard _
        | OrderOrdinal _
        | Literal _
        | Interval _ -> ()
        | Unary(_, operand) ->
            proveFilterPredicate proofs operand
        | Binary(_, left, right) ->
            proveFilterPredicate proofs left
            proveFilterPredicate proofs right
        | Like(value, pattern, _, _, _) ->
            proveFilterPredicate proofs value
            proveFilterPredicate proofs pattern
        | RawRegexCall(arguments, _) ->
            arguments |> List.iter (proveFilterPredicate proofs)
        | RegexMatch(value, pattern) ->
            proveFilterPredicate proofs value
            proveFilterPredicate proofs pattern
        | FunctionCall call ->
            call.Arguments |> List.iter (proveFilterPredicate proofs)
            call.AggregateOrderBy |> List.iter (fun order -> proveFilterPredicate proofs order.Expression)
        | FilteredAggregate(value, predicate) ->
            proveFilterPredicate proofs value
            proveFilterPredicate proofs predicate
        | Windowed(value, window) ->
            requireFilterCapability proofs.WindowFunction
            proveFilterPredicate proofs value
            window.PartitionBy |> List.iter (proveFilterPredicate proofs)
            window.OrderBy |> List.iter (fun order -> proveFilterPredicate proofs order.Expression)
        | Cast(value, _)
        | Extract(_, value) ->
            proveFilterPredicate proofs value
        | SimpleCase(input, branches, fallback) ->
            proveFilterPredicate proofs input
            branches |> NonEmpty.iter (fun branch ->
                proveFilterPredicate proofs branch.Match
                proveFilterPredicate proofs branch.Result)
            fallback |> Option.iter (proveFilterPredicate proofs)
        | SearchedCase(branches, fallback) ->
            branches |> NonEmpty.iter (fun branch ->
                proveFilterPredicate proofs branch.Condition
                proveFilterPredicate proofs branch.Result)
            fallback |> Option.iter (proveFilterPredicate proofs)
        | InList(value, items, _) ->
            proveFilterPredicate proofs value
            items |> NonEmpty.iter (proveFilterPredicate proofs)
        | InSubquery(value, _, _) ->
            proveFilterPredicate proofs value
            requireFilterCapability proofs.Subquery
        | Between(value, lower, upper, _) ->
            proveFilterPredicate proofs value
            proveFilterPredicate proofs lower
            proveFilterPredicate proofs upper
        | IsNull(value, _) ->
            proveFilterPredicate proofs value
        | ScalarSubquery _
        | Exists _ ->
            requireFilterCapability proofs.Subquery

    let rec private proveSourceFilterExpr (expressionProofs: ExpressionProofs) expression =
        match expression with
        | Column _
        | BoundColumn _
        | Wildcard _
        | OrderOrdinal _
        | Literal _ -> ()
        | Interval _ ->
            requireFilterCapability expressionProofs.IntervalLiteral
        | Unary(_, operand) ->
            proveSourceFilterExpr expressionProofs operand
        | Binary(_, left, right) ->
            proveSourceFilterExpr expressionProofs left
            proveSourceFilterExpr expressionProofs right
        | Like(value, pattern, _, _, caseInsensitive) ->
            if caseInsensitive then requireFilterCapability expressionProofs.ILike
            proveSourceFilterExpr expressionProofs value
            proveSourceFilterExpr expressionProofs pattern
        | RawRegexCall(arguments, _) ->
            arguments |> List.iter (proveSourceFilterExpr expressionProofs)
        | RegexMatch(value, pattern) ->
            proveSourceFilterExpr expressionProofs value
            proveSourceFilterExpr expressionProofs pattern
        | FunctionCall call ->
            call.Arguments |> List.iter (proveSourceFilterExpr expressionProofs)
            call.AggregateOrderBy |> List.iter (fun order -> proveSourceFilterExpr expressionProofs order.Expression)
        | FilteredAggregate(value, predicate) ->
            requireFilterCapability expressionProofs.AggregateFilter
            proveFilterPredicate expressionProofs.FilterPredicate predicate
            proveSourceFilterExpr expressionProofs value
            proveSourceFilterExpr expressionProofs predicate
        | Windowed(value, window) ->
            proveSourceFilterExpr expressionProofs value
            window.PartitionBy |> List.iter (proveSourceFilterExpr expressionProofs)
            window.OrderBy |> List.iter (fun order -> proveSourceFilterExpr expressionProofs order.Expression)
        | Cast(value, _)
        | Extract(_, value) ->
            proveSourceFilterExpr expressionProofs value
        | SimpleCase(input, branches, fallback) ->
            proveSourceFilterExpr expressionProofs input
            branches |> NonEmpty.iter (fun branch ->
                proveSourceFilterExpr expressionProofs branch.Match
                proveSourceFilterExpr expressionProofs branch.Result)
            fallback |> Option.iter (proveSourceFilterExpr expressionProofs)
        | SearchedCase(branches, fallback) ->
            branches |> NonEmpty.iter (fun branch ->
                proveSourceFilterExpr expressionProofs branch.Condition
                proveSourceFilterExpr expressionProofs branch.Result)
            fallback |> Option.iter (proveSourceFilterExpr expressionProofs)
        | InList(value, items, _) ->
            proveSourceFilterExpr expressionProofs value
            items |> NonEmpty.iter (proveSourceFilterExpr expressionProofs)
        | InSubquery(value, query, _) ->
            proveSourceFilterExpr expressionProofs value
            proveSourceFilterQuery expressionProofs query
        | Between(value, lower, upper, _) ->
            proveSourceFilterExpr expressionProofs value
            proveSourceFilterExpr expressionProofs lower
            proveSourceFilterExpr expressionProofs upper
        | IsNull(value, _) ->
            proveSourceFilterExpr expressionProofs value
        | ScalarSubquery query
        | Exists(query, _) ->
            proveSourceFilterQuery expressionProofs query

    and private proveSourceFilterSource expressionProofs source =
        match source with
        | NamedTable _
        | CteTable _ -> ()
        | DerivedTable(query, _) ->
            proveSourceFilterQuery expressionProofs query

    and private proveSourceFilterSelect expressionProofs select =
        select.Ctes |> List.iter (fun cte -> proveSourceFilterQuery expressionProofs cte.Query)
        select.Projection |> List.iter (fun item -> proveSourceFilterExpr expressionProofs item.Expression)
        select.From |> Option.iter (proveSourceFilterSource expressionProofs)
        select.Joins |> List.iter (fun join ->
            proveSourceFilterSource expressionProofs join.Source
            join.Predicate |> Option.iter (proveSourceFilterExpr expressionProofs))
        select.Where |> Option.iter (proveSourceFilterExpr expressionProofs)
        select.GroupBy |> List.iter (proveSourceFilterExpr expressionProofs)
        select.Having |> Option.iter (proveSourceFilterExpr expressionProofs)

    and private proveSourceFilterQuery expressionProofs query =
        proveSourceFilterSelect expressionProofs query.Head
        query.SetOperations |> List.iter (fun branch -> proveSourceFilterQuery expressionProofs branch.Query)
        query.OrderBy |> List.iter (fun order -> proveSourceFilterExpr expressionProofs order.Expression)

    let private proveSourceFilterDocument expressionProofs document =
        match document.Statement with
        | QueryStatement query ->
            proveSourceFilterQuery expressionProofs query
        | InsertStatement insert ->
            match insert.Input with
            | Values rows -> rows |> NonEmpty.iter (NonEmpty.iter (proveSourceFilterExpr expressionProofs))
            | QuerySource query -> proveSourceFilterQuery expressionProofs query
            | DefaultValues -> ()
            insert.Returning |> List.iter (fun item -> proveSourceFilterExpr expressionProofs item.Expression)
        | UpdateStatement update ->
            update.AssignmentItems |> NonEmpty.iter (fun assignment -> proveSourceFilterExpr expressionProofs assignment.Value)
            update.From |> List.iter (proveSourceFilterSource expressionProofs)
            update.Where |> Option.iter (proveSourceFilterExpr expressionProofs)
            update.Returning |> List.iter (fun item -> proveSourceFilterExpr expressionProofs item.Expression)
        | DeleteStatement delete ->
            delete.Using |> List.iter (proveSourceFilterSource expressionProofs)
            delete.Where |> Option.iter (proveSourceFilterExpr expressionProofs)
            delete.Returning |> List.iter (fun item -> proveSourceFilterExpr expressionProofs item.Expression)

    let rec private proveTargetExpr targetRuntime (expressionProofs: ExpressionProofs) expression =
        match expression with
        | Column _ | BoundColumn _ | Wildcard _ | OrderOrdinal _ -> ()
        | Literal value -> proveTargetLiteral targetRuntime value
        | Interval _ -> requireExpressionCapability expressionProofs.IntervalLiteral
        | Unary(_, operand) -> proveTargetExpr targetRuntime expressionProofs operand
        | Binary(BinaryOperator.Concat, left, right) ->
            proveSqlServerConcat targetRuntime
            proveTargetExpr targetRuntime expressionProofs left
            proveTargetExpr targetRuntime expressionProofs right
        | Binary(_, left, right) ->
            proveTargetExpr targetRuntime expressionProofs left
            proveTargetExpr targetRuntime expressionProofs right
        | Like(value, pattern, _, _, caseInsensitive) ->
            if caseInsensitive then requireExpressionCapability expressionProofs.ILike
            proveTargetExpr targetRuntime expressionProofs value
            proveTargetExpr targetRuntime expressionProofs pattern
        | RawRegexCall _ ->
            invalidOp "Raw REGEXP_LIKE reached target validation before canonicalization."
        | RegexMatch(value, pattern) ->
            requireExpressionCapability expressionProofs.RegexMatch
            proveTargetExpr targetRuntime expressionProofs value
            proveTargetExpr targetRuntime expressionProofs pattern
        | FunctionCall call ->
            call.Arguments |> List.iter (proveTargetExpr targetRuntime expressionProofs)
            call.AggregateOrderBy |> List.iter (fun order -> proveTargetExpr targetRuntime expressionProofs order.Expression)
        | FilteredAggregate(value, predicate) ->
            proveTargetExpr targetRuntime expressionProofs value
            proveTargetExpr targetRuntime expressionProofs predicate
        | Windowed(value, window) ->
            proveTargetExpr targetRuntime expressionProofs value
            window.PartitionBy |> List.iter (proveTargetExpr targetRuntime expressionProofs)
            window.OrderBy |> List.iter (fun order -> proveTargetExpr targetRuntime expressionProofs order.Expression)
        | Cast(value, _) | Extract(_, value) ->
            proveTargetExpr targetRuntime expressionProofs value
        | SimpleCase(input, branches, fallback) ->
            proveTargetExpr targetRuntime expressionProofs input
            branches |> NonEmpty.iter (fun branch ->
                proveTargetExpr targetRuntime expressionProofs branch.Match
                proveTargetExpr targetRuntime expressionProofs branch.Result)
            fallback |> Option.iter (proveTargetExpr targetRuntime expressionProofs)
        | SearchedCase(branches, fallback) ->
            branches |> NonEmpty.iter (fun branch ->
                proveTargetExpr targetRuntime expressionProofs branch.Condition
                proveTargetExpr targetRuntime expressionProofs branch.Result)
            fallback |> Option.iter (proveTargetExpr targetRuntime expressionProofs)
        | InList(value, items, _) ->
            proveTargetExpr targetRuntime expressionProofs value
            items |> NonEmpty.iter (proveTargetExpr targetRuntime expressionProofs)
        | InSubquery(value, query, _) ->
            proveTargetExpr targetRuntime expressionProofs value
            proveTargetQuery targetRuntime expressionProofs query
        | Between(value, lower, upper, _) ->
            proveTargetExpr targetRuntime expressionProofs value
            proveTargetExpr targetRuntime expressionProofs lower
            proveTargetExpr targetRuntime expressionProofs upper
        | IsNull(value, _) ->
            proveTargetExpr targetRuntime expressionProofs value
        | ScalarSubquery query ->
            proveTargetQuery targetRuntime expressionProofs query
        | Exists(query, _) ->
            proveTargetQuery targetRuntime expressionProofs query

    and private proveTargetSource targetRuntime expressionProofs source =
        match source with
        | NamedTable _ | CteTable _ -> ()
        | DerivedTable(query, _) -> proveTargetQuery targetRuntime expressionProofs query

    and private proveTargetSelect targetRuntime expressionProofs select =
        select.Ctes |> List.iter (fun cte -> proveTargetQuery targetRuntime expressionProofs cte.Query)
        select.ProjectionItems |> NonEmpty.iter (fun item -> proveTargetExpr targetRuntime expressionProofs item.Expression)
        select.From |> Option.iter (proveTargetSource targetRuntime expressionProofs)
        select.Joins
        |> List.iter (function
            | CrossJoin source -> proveTargetSource targetRuntime expressionProofs source
            | OnJoin(_, source, predicate) ->
                proveTargetSource targetRuntime expressionProofs source
                proveTargetExpr targetRuntime expressionProofs predicate)
        select.Where |> Option.iter (proveTargetExpr targetRuntime expressionProofs)
        select.GroupBy |> List.iter (proveTargetExpr targetRuntime expressionProofs)
        select.Having |> Option.iter (proveTargetExpr targetRuntime expressionProofs)

    and private proveTargetQuery targetRuntime expressionProofs query =
        proveTargetSelect targetRuntime expressionProofs query.Head
        query.SetOperations |> List.iter (fun branch -> proveTargetQuery targetRuntime expressionProofs branch.Query)
        query.OrderBy |> List.iter (fun order -> proveTargetExpr targetRuntime expressionProofs order.Expression)

    let private proveTargetDocument targetRuntime expressionProofs document =
        match document.Statement with
        | QueryStatement query -> proveTargetQuery targetRuntime expressionProofs query
        | InsertStatement insert ->
            match insert.Input with
            | Values rows -> rows |> NonEmpty.iter (NonEmpty.iter (proveTargetExpr targetRuntime expressionProofs))
            | QuerySource query -> proveTargetQuery targetRuntime expressionProofs query
            | DefaultValues -> ()
            insert.Returning |> List.iter (fun item -> proveTargetExpr targetRuntime expressionProofs item.Expression)
        | UpdateStatement update ->
            update.AssignmentItems |> NonEmpty.iter (fun assignment -> proveTargetExpr targetRuntime expressionProofs assignment.Value)
            update.From |> List.iter (proveTargetSource targetRuntime expressionProofs)
            update.Where |> Option.iter (proveTargetExpr targetRuntime expressionProofs)
            update.Returning |> List.iter (fun item -> proveTargetExpr targetRuntime expressionProofs item.Expression)
        | DeleteStatement delete ->
            delete.Using |> List.iter (proveTargetSource targetRuntime expressionProofs)
            delete.Where |> Option.iter (proveTargetExpr targetRuntime expressionProofs)
            delete.Returning |> List.iter (fun item -> proveTargetExpr targetRuntime expressionProofs item.Expression)
        document

    let private requireCapability = function
        | ProvenCapability -> ()
        | RejectedCapability message -> invalidOp message

    let private proveJoinKind (proofs: JoinProofs) = function
        | JoinKind.Right -> requireCapability proofs.RightJoin
        | JoinKind.Full -> requireCapability proofs.FullJoin
        | JoinKind.Inner | JoinKind.Left | JoinKind.Cross -> ()

    let rec private proveTargetJoinSource proofs source =
        match source with
        | NamedTable _ | CteTable _ -> ()
        | DerivedTable(query, _) -> proveTargetJoinQuery proofs query

    and private proveTargetJoinSelect proofs select =
        select.Ctes |> List.iter (fun cte -> proveTargetJoinQuery proofs cte.Query)
        select.From |> Option.iter (proveTargetJoinSource proofs)
        select.Joins
        |> List.iter (fun join ->
            proveJoinKind proofs join.Kind
            proveTargetJoinSource proofs join.Source)

    and private proveTargetJoinQuery proofs query =
        proveTargetJoinSelect proofs query.Head
        query.SetOperations |> List.iter (fun branch -> proveTargetJoinQuery proofs branch.Query)

    let private proveTargetJoins proofs document =
        match document.Statement with
        | QueryStatement query -> proveTargetJoinQuery proofs query
        | InsertStatement insert ->
            match insert.Input with
            | QuerySource query -> proveTargetJoinQuery proofs query
            | Values _ | DefaultValues -> ()
        | UpdateStatement update ->
            update.From |> List.iter (proveTargetJoinSource proofs)
        | DeleteStatement delete ->
            delete.Using |> List.iter (proveTargetJoinSource proofs)

    let private requireDmlCapability = function
        | ProvenCapability -> ()
        | RejectedCapability message -> raise (SqlCompilationException(message))

    let private isRichReturningItem (item: SelectItem) =
        match item.Expression with
        | Column identifier
        | BoundColumn(identifier, _)
            when Identifier.parts identifier |> List.length = 1 -> false
        | Wildcard None -> false
        | _ -> true

    let private proveReturning (proofs: DmlProofs) items =
        if not (List.isEmpty items) then
            requireDmlCapability proofs.Returning
            if items |> List.exists isRichReturningItem then
                requireDmlCapability proofs.ReturningExpression

    let private proveTargetDml (proofs: DmlProofs) document =
        match document.Statement with
        | QueryStatement _ -> ()
        | InsertStatement insert ->
            proveReturning proofs insert.Returning
        | UpdateStatement update ->
            if not update.From.IsEmpty then requireDmlCapability proofs.UpdateFrom
            proveReturning proofs update.Returning
        | DeleteStatement delete ->
            if not delete.Using.IsEmpty then requireDmlCapability proofs.DeleteUsing
            proveReturning proofs delete.Returning

    let private orderingProviderName = function
        | MySqlRuntime -> "MySQL"
        | SqlServerRuntime _ -> "MsSqlServer"
        | PostgreSqlRuntime -> "Postgres"
        | SQLiteRuntime -> "Sqlite"
        | OracleRuntime -> "Oracle"
        | FirebirdRuntime -> "Firebird"

    let private nullOrderingCapabilityError targetRuntime =
        SqlCompilationException(
            "SQL capability 'ordering.nulls' is not supported by provider "
            + orderingProviderName targetRuntime
            + " for this Core plan.")

    let private targetDefaultNullOrdering (order: OrderBy) =
        match order.NullOrdering with
        | NullOrdering.Default -> true
        | NullOrdering.NullsFirst -> not order.Descending
        | NullOrdering.NullsLast -> order.Descending

    let private requireRewriteableNullOrdering targetRuntime targetOrdering isStatementTail isDistinct isSetTail (order: OrderBy) =
        match targetOrdering, order.NullOrdering with
        | NativeNullOrdering, _
        | RewriteNullOrdering, NullOrdering.Default -> ()
        | RewriteNullOrdering, _ when targetDefaultNullOrdering order -> ()
        | RewriteNullOrdering, _ ->
            if isStatementTail && (isDistinct || isSetTail) then
                raise (nullOrderingCapabilityError targetRuntime)
            match order.Expression with
            | BoundColumn(_, LocalRowSource)
            | BoundColumn(_, OuterRowSource) -> ()
            | Column _
            | BoundColumn(_, ProjectionAlias)
            | _ -> raise (nullOrderingCapabilityError targetRuntime)

    let rec private proveOrderingExpr targetRuntime targetOrdering expression =
        match expression with
        | Column _ | BoundColumn _ | Wildcard _ | OrderOrdinal _ | Literal _ | Interval _ -> ()
        | Unary(_, value) -> proveOrderingExpr targetRuntime targetOrdering value
        | Binary(_, left, right) ->
            proveOrderingExpr targetRuntime targetOrdering left
            proveOrderingExpr targetRuntime targetOrdering right
        | Like(value, pattern, _, _, _) ->
            proveOrderingExpr targetRuntime targetOrdering value
            proveOrderingExpr targetRuntime targetOrdering pattern
        | RawRegexCall(arguments, _) ->
            arguments |> List.iter (proveOrderingExpr targetRuntime targetOrdering)
        | RegexMatch(value, pattern) ->
            proveOrderingExpr targetRuntime targetOrdering value
            proveOrderingExpr targetRuntime targetOrdering pattern
        | FunctionCall call ->
            call.Arguments |> List.iter (proveOrderingExpr targetRuntime targetOrdering)
            call.AggregateOrderBy
            |> List.iter (fun order ->
                requireRewriteableNullOrdering targetRuntime targetOrdering false false false order
                proveOrderingExpr targetRuntime targetOrdering order.Expression)
        | FilteredAggregate(value, predicate) ->
            proveOrderingExpr targetRuntime targetOrdering value
            proveOrderingExpr targetRuntime targetOrdering predicate
        | Windowed(value, window) ->
            proveOrderingExpr targetRuntime targetOrdering value
            window.PartitionBy |> List.iter (proveOrderingExpr targetRuntime targetOrdering)
            window.OrderBy
            |> List.iter (fun order ->
                requireRewriteableNullOrdering targetRuntime targetOrdering false false false order
                proveOrderingExpr targetRuntime targetOrdering order.Expression)
        | Cast(value, _) | Extract(_, value) ->
            proveOrderingExpr targetRuntime targetOrdering value
        | SimpleCase(input, branches, fallback) ->
            proveOrderingExpr targetRuntime targetOrdering input
            branches |> NonEmpty.iter (fun branch ->
                proveOrderingExpr targetRuntime targetOrdering branch.Match
                proveOrderingExpr targetRuntime targetOrdering branch.Result)
            fallback |> Option.iter (proveOrderingExpr targetRuntime targetOrdering)
        | SearchedCase(branches, fallback) ->
            branches |> NonEmpty.iter (fun branch ->
                proveOrderingExpr targetRuntime targetOrdering branch.Condition
                proveOrderingExpr targetRuntime targetOrdering branch.Result)
            fallback |> Option.iter (proveOrderingExpr targetRuntime targetOrdering)
        | InList(value, items, _) ->
            proveOrderingExpr targetRuntime targetOrdering value
            items |> NonEmpty.iter (proveOrderingExpr targetRuntime targetOrdering)
        | InSubquery(value, query, _) ->
            proveOrderingExpr targetRuntime targetOrdering value
            proveOrderingQuery targetRuntime targetOrdering query
        | Between(value, lower, upper, _) ->
            proveOrderingExpr targetRuntime targetOrdering value
            proveOrderingExpr targetRuntime targetOrdering lower
            proveOrderingExpr targetRuntime targetOrdering upper
        | IsNull(value, _) ->
            proveOrderingExpr targetRuntime targetOrdering value
        | ScalarSubquery query | Exists(query, _) ->
            proveOrderingQuery targetRuntime targetOrdering query

    and private proveOrderingSource targetRuntime targetOrdering source =
        match source with
        | NamedTable _ | CteTable _ -> ()
        | DerivedTable(query, _) -> proveOrderingQuery targetRuntime targetOrdering query

    and private proveOrderingSelect targetRuntime targetOrdering select =
        select.Ctes |> List.iter (fun cte -> proveOrderingQuery targetRuntime targetOrdering cte.Query)
        select.Projection |> List.iter (fun item -> proveOrderingExpr targetRuntime targetOrdering item.Expression)
        select.From |> Option.iter (proveOrderingSource targetRuntime targetOrdering)
        select.Joins |> List.iter (fun join ->
            proveOrderingSource targetRuntime targetOrdering join.Source
            join.Predicate |> Option.iter (proveOrderingExpr targetRuntime targetOrdering))
        select.Where |> Option.iter (proveOrderingExpr targetRuntime targetOrdering)
        select.GroupBy |> List.iter (proveOrderingExpr targetRuntime targetOrdering)
        select.Having |> Option.iter (proveOrderingExpr targetRuntime targetOrdering)

    and private proveOrderingQuery targetRuntime targetOrdering query =
        proveOrderingSelect targetRuntime targetOrdering query.Head
        query.SetOperations |> List.iter (fun branch -> proveOrderingQuery targetRuntime targetOrdering branch.Query)
        let isSetTail = not query.SetOperations.IsEmpty
        query.OrderBy
        |> List.iter (fun order ->
            requireRewriteableNullOrdering targetRuntime targetOrdering true query.Head.Distinct isSetTail order
            proveOrderingExpr targetRuntime targetOrdering order.Expression)

    let private stableProjectionNames context (query: Query) =
        query.Head.Projection
        |> List.map (fun item ->
            match item.Alias, item.Expression with
            | Some alias, _ -> alias
            | None, Column identifier
            | None, BoundColumn(identifier, _) ->
                Identifier.parts identifier |> List.last
            | _ ->
                raise (SqlCompilationException(
                    context
                    + " requires every projected output to have a stable name; use explicit aliases for wildcard or computed expressions.")))

    let private ensureUniqueOutputNames context names =
        let seen = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        names
        |> List.iter (fun (name: IdentifierPart) ->
            if not (seen.Add name.Value) then
                raise (SqlCompilationException(
                    context + " requires unique set-result output names before the legacy ROW_NUMBER wrapper.")))

    let private projectionOrderIndex (projection: SelectItem list) (order: OrderBy) =
        match order.Expression with
        | OrderOrdinal ordinal ->
            let index = PositiveRowCount.value ordinal - 1
            if index >= 0 && index < projection.Length then Some index else None
        | Column identifier
        | BoundColumn(identifier, _)
            when Identifier.parts identifier |> List.length = 1 ->
            let reference = Identifier.parts identifier |> List.head |> fun part -> part.Value
            let aliasMatches =
                projection
                |> List.indexed
                |> List.choose (fun (index, item) ->
                    item.Alias
                    |> Option.bind (fun alias ->
                        if StringComparer.OrdinalIgnoreCase.Equals(alias.Value, reference) then Some index else None))
            match aliasMatches with
            | [ index ] -> Some index
            | _ :: _ :: _ ->
                raise (SqlCompilationException(
                    "SQL Server OFFSET pagination ORDER BY alias '" + reference + "' is ambiguous."))
            | [] ->
                projection |> List.tryFindIndex (fun item -> Expr.equivalent item.Expression order.Expression)
        | _ ->
            projection |> List.tryFindIndex (fun item -> Expr.equivalent item.Expression order.Expression)

    let private proveSqlServerSelectPaging (query: Query) =
        let context = "SQL Server OFFSET pagination"
        stableProjectionNames context query |> ignore
        let projection = query.Head.Projection
        for order in query.OrderBy do
            match projectionOrderIndex projection order with
            | Some _ -> ()
            | None when query.Head.Distinct ->
                raise (SqlCompilationException(
                    "SQL Server DISTINCT OFFSET pagination requires every ORDER BY expression to resolve to a projected output."))
            | None -> ()

    let private proveSqlServerSetPaging (query: Query) =
        let context = "SQL Server set-operation OFFSET pagination"
        let names = stableProjectionNames context query
        ensureUniqueOutputNames context names
        for order in query.OrderBy do
            match order.Expression with
            | OrderOrdinal ordinal ->
                let index = PositiveRowCount.value ordinal - 1
                if index < 0 || index >= names.Length then
                    raise (SqlCompilationException(
                        "SQL Server set-operation OFFSET pagination ORDER BY position is outside the projected output width."))
            | Column identifier
            | BoundColumn(identifier, _)
                when Identifier.parts identifier |> List.length = 1 ->
                let reference = Identifier.parts identifier |> List.head |> fun part -> part.Value
                let matches =
                    names
                    |> List.filter (fun name -> StringComparer.OrdinalIgnoreCase.Equals(name.Value, reference))
                if matches.Length <> 1 then
                    raise (SqlCompilationException(
                        "SQL Server set-operation OFFSET pagination ORDER BY reference '"
                        + reference
                        + "' is not a unique combined output name."))
            | _ ->
                raise (SqlCompilationException(
                    "SQL Server set-operation OFFSET pagination supports ORDER BY output names or ordinals only."))

    let rec private proveSqlServerPagingQuery query =
        query.Head.Ctes |> List.iter (fun cte -> proveSqlServerPagingQuery cte.Query)
        query.Head.From |> Option.iter (function DerivedTable(q, _) -> proveSqlServerPagingQuery q | _ -> ())
        query.Head.Joins |> List.iter (fun join ->
            match join.Source with DerivedTable(q, _) -> proveSqlServerPagingQuery q | _ -> ())
        query.SetOperations |> List.iter (fun branch -> proveSqlServerPagingQuery branch.Query)
        match query.Offset with
        | Some offset when NonNegativeRowCount.value offset > 0 ->
            if query.SetOperations.IsEmpty then proveSqlServerSelectPaging query
            else proveSqlServerSetPaging query
        | _ -> ()

    let private proveOrderingAndPaging targetRuntime targetOrdering document =
        match document.Statement with
        | QueryStatement query ->
            proveOrderingQuery targetRuntime targetOrdering query
            match targetRuntime with
            | SqlServerRuntime _ -> proveSqlServerPagingQuery query
            | _ -> ()
        | InsertStatement insert ->
            match insert.Input with
            | QuerySource query ->
                proveOrderingQuery targetRuntime targetOrdering query
                match targetRuntime with
                | SqlServerRuntime _ -> proveSqlServerPagingQuery query
                | _ -> ()
            | Values _ | DefaultValues -> ()
            insert.Returning |> List.iter (fun item -> proveOrderingExpr targetRuntime targetOrdering item.Expression)
        | UpdateStatement update ->
            update.AssignmentItems |> NonEmpty.iter (fun assignment -> proveOrderingExpr targetRuntime targetOrdering assignment.Value)
            update.From |> List.iter (proveOrderingSource targetRuntime targetOrdering)
            update.Where |> Option.iter (proveOrderingExpr targetRuntime targetOrdering)
            update.Returning |> List.iter (fun item -> proveOrderingExpr targetRuntime targetOrdering item.Expression)
        | DeleteStatement delete ->
            delete.Using |> List.iter (proveOrderingSource targetRuntime targetOrdering)
            delete.Where |> Option.iter (proveOrderingExpr targetRuntime targetOrdering)
            delete.Returning |> List.iter (fun item -> proveOrderingExpr targetRuntime targetOrdering item.Expression)

    let private exactColumnSetMatch (left: string list) (right: string list) =
        let leftSet = HashSet<string>(left, StringComparer.OrdinalIgnoreCase)
        let rightSet = HashSet<string>(right, StringComparer.OrdinalIgnoreCase)
        leftSet.Count = List.length left
        && rightSet.Count = List.length right
        && leftSet.SetEquals(rightSet)

    let private assuredColumns label = function
        | AssuredColumns columns -> columns
        | MissingAssurance -> raise (SqlCompilationException(label))

    let private validateConflictTargetColumns (insert: Insert) (conflict: InsertConflict) =
        let insertColumns =
            HashSet<string>(
                insert.Columns |> List.map (fun column -> column.Value),
                StringComparer.OrdinalIgnoreCase)
        let seen = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        for target in conflict.TargetColumns |> NonEmpty.toList do
            let name = Identifier.text target
            if not (seen.Add name) then
                raise (SqlCompilationException(
                    "INSERT conflict target column '" + name + "' is declared more than once."))
            if not (insertColumns.Contains name) then
                raise (SqlCompilationException(
                    "INSERT conflict target column '" + name + "' must be explicitly present in the INSERT column list so Core does not depend on provider-default conflict-key values."))

    let private validateInsertSelectConflictAssurance (conflict: InsertConflict) (proofs: ConflictProofs) =
        let proven =
            assuredColumns
                "PostgreSQL INSERT ... SELECT ON CONFLICT DO UPDATE remains fail-closed without explicit source-row uniqueness/cardinality assurance for the complete conflict target."
                proofs.SourceRowsUniqueByInsertColumns
        let target =
            conflict.TargetColumns
            |> NonEmpty.toList
            |> List.map Identifier.text
        if not (exactColumnSetMatch target proven) then
            raise (SqlCompilationException(
                "INSERT ... SELECT conflict DO UPDATE requires source-row uniqueness assurance to match the complete explicit conflict target exactly."))

    let private validateConflictAssignments (insert: Insert) (assignments: NonEmpty<ConflictAssignment>) =
        let insertColumns =
            HashSet<string>(
                insert.Columns |> List.map (fun column -> column.Value),
                StringComparer.OrdinalIgnoreCase)
        let assigned = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        assignments
        |> NonEmpty.iter (fun (assignment: ConflictAssignment) ->
            let target = Identifier.text assignment.Target
            let proposed = Identifier.text assignment.Proposed
            if not (assigned.Add target) then
                raise (SqlCompilationException(
                    "INSERT conflict DO UPDATE assigns column '" + target + "' more than once."))
            if not (insertColumns.Contains proposed) then
                raise (SqlCompilationException(
                    "Proposed-row column '" + proposed + "' must be explicitly present in the INSERT column list; portable upsert does not depend on target-provider default values.")))

    let private validatePortableConflict targetRuntime (proofs: ConflictProofs) (insert: Insert) (conflict: InsertConflict) =
        match insert.Input with
        | DefaultValues ->
            raise (SqlCompilationException("Unsupported INSERT source for conflict handling."))
        | QuerySource _ ->
            match targetRuntime with
            | PostgreSqlRuntime -> ()
            | _ ->
                raise (SqlCompilationException(
                    "INSERT ... SELECT conflict handling is currently proven only for PostgreSQL targets; other targets remain fail-closed."))
        | Values _ -> ()

        validateConflictTargetColumns insert conflict

        match conflict.Action with
        | DoNothing -> ()
        | UpdateProposedValues assignments ->
            match insert.Input with
            | QuerySource _ -> validateInsertSelectConflictAssurance conflict proofs
            | Values rows when NonEmpty.length rows <> 1 ->
                raise (SqlCompilationException(
                    "Portable INSERT conflict DO UPDATE currently requires exactly one proposed VALUES row. Multi-row proposed values require explicit source-row uniqueness/cardinality assurance."))
            | Values _ -> ()
            | DefaultValues -> ()
            validateConflictAssignments insert assignments

    let private validateFirebirdFullProposedRowUpdate (insert: Insert) (assignments: NonEmpty<ConflictAssignment>) =
        let assignmentList = NonEmpty.toList assignments
        if assignmentList.Length <> insert.Columns.Length then
            raise (SqlCompilationException(
                "Firebird UPDATE OR INSERT updates every supplied INSERT column on a match. Core therefore requires one same-column proposed-row assignment for every INSERT column so partial-update semantics cannot drift."))

        let insertColumns =
            HashSet<string>(
                insert.Columns |> List.map (fun column -> column.Value),
                StringComparer.OrdinalIgnoreCase)
        let assigned = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        for (assignment: ConflictAssignment) in assignmentList do
            let target = Identifier.text assignment.Target
            let proposed = Identifier.text assignment.Proposed
            if not (StringComparer.OrdinalIgnoreCase.Equals(target, proposed)) then
                raise (SqlCompilationException(
                    "Firebird UPDATE OR INSERT can mirror the portable conflict contract only when each assignment is target = proposed-row target for the same column."))
            if not (assigned.Add target) || not (insertColumns.Contains target) then
                raise (SqlCompilationException(
                    "Firebird UPDATE OR INSERT assignment column '" + target + "' must occur exactly once in the INSERT column list."))

        if not (assigned.SetEquals insertColumns) then
            raise (SqlCompilationException(
                "Firebird UPDATE OR INSERT requires conflict assignments to cover the complete INSERT column set."))

    let private requireConflictCapability = function
        | ProvenCapability -> ()
        | RejectedCapability message -> raise (SqlCompilationException(message))

    let private validateMySqlConflict (proofs: ConflictProofs) (conflict: InsertConflict) =
        match conflict.Action with
        | DoNothing ->
            raise (SqlCompilationException(
                "MySQL INSERT IGNORE is not a portable ON CONFLICT DO NOTHING equivalent because it can suppress errors beyond the explicit conflict target; MySQL DO NOTHING therefore remains fail-closed."))
        | UpdateProposedValues _ ->
            let matchedColumns =
                match proofs.MySqlUniqueKey with
                | MissingMySqlUniqueKeyAssurance ->
                    raise (SqlCompilationException(
                        "MySQL ON DUPLICATE KEY UPDATE requires metadata-backed statement assurance proving the explicit conflict target matches a complete enforced unique key and is the sole enforced native conflict source."))
                | AssuredMySqlUniqueKey(_, false) ->
                    raise (SqlCompilationException(
                        "MySQL ON DUPLICATE KEY UPDATE can react to any UNIQUE or PRIMARY KEY conflict. Core requires the matched conflict target to be the sole enforced native unique-conflict source, including no additional richer expression, prefix, partial, or otherwise unsupported enforced unique keys."))
                | AssuredMySqlUniqueKey(columns, true) -> columns
            let target =
                conflict.TargetColumns
                |> NonEmpty.toList
                |> List.map Identifier.text
            if not (exactColumnSetMatch target matchedColumns) then
                raise (SqlCompilationException(
                    "MySQL conflict lowering requires the canonical explicit conflict target to match the complete metadata-resolved unique key exactly."))
            requireConflictCapability proofs.MySqlConditionalTarget

    let private validateDirectConflict (proofs: ConflictProofs) =
        requireConflictCapability proofs.DirectTarget

    let private validateFirebirdConflict (proofs: ConflictProofs) (insert: Insert) (conflict: InsertConflict) =
        match conflict.Action with
        | DoNothing ->
            raise (SqlCompilationException(
                "Firebird UPDATE OR INSERT has update-or-insert semantics and cannot represent portable ON CONFLICT DO NOTHING; a separate MERGE no-match contract is required."))
        | UpdateProposedValues assignments ->
            let primaryKey =
                assuredColumns
                    "Firebird UPDATE OR INSERT requires metadata-backed conflict-target assurance proving MATCHING equals the resolved primary key; absent assurance remains fail-closed because non-unique MATCHING can update multiple rows."
                    proofs.FirebirdPrimaryKey
            let target =
                conflict.TargetColumns
                |> NonEmpty.toList
                |> List.map Identifier.text
            if not (exactColumnSetMatch target primaryKey) then
                raise (SqlCompilationException(
                    "Firebird UPDATE OR INSERT requires the canonical conflict target to match the complete resolved primary key exactly; general UNIQUE-key and non-unique MATCHING metadata are not represented yet."))
            validateFirebirdFullProposedRowUpdate insert assignments

    let private proveConflicts targetRuntime (proofs: ConflictProofs) document =
        match document.Statement with
        | InsertStatement insert ->
            match insert.Conflict with
            | None -> ()
            | Some conflict ->
                validatePortableConflict targetRuntime proofs insert conflict
                match targetRuntime with
                | PostgreSqlRuntime | SQLiteRuntime | SqlServerRuntime _ | OracleRuntime ->
                    validateDirectConflict proofs
                | MySqlRuntime ->
                    validateMySqlConflict proofs conflict
                | FirebirdRuntime ->
                    validateFirebirdConflict proofs insert conflict
        | QueryStatement _ | UpdateStatement _ | DeleteStatement _ -> ()

    let validate allowedTables targetRuntime sourceExpressions targetExpressions targetJoins targetOrdering targetDml conflictProofs canonical =
        Transition.validate targetRuntime (fun document ->
            proveSourceFilterDocument sourceExpressions document
            proveSourceFilterDocument targetExpressions document
            let validated = validateDocument allowedTables document
            proveTargetDocument targetRuntime targetExpressions validated |> ignore
            proveTargetJoins targetJoins validated
            proveOrderingAndPaging targetRuntime targetOrdering validated
            proveTargetDml targetDml validated
            proveConflicts targetRuntime conflictProofs validated
            validated) canonical
