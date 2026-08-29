namespace HsSqlAgent.SqlCore.Internal

open System
open System.Collections.Immutable
open HsSqlAgent.SqlCore.Core.Ast
open HsSqlAgent.SqlCore.Core.Binding
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Core.Normalization
open HsSqlAgent.SqlCore.Core.Pipeline
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models

/// F# ownership boundary for query normalization.
///
/// Statement/source traversal, primitive expression normalization, CAST traversal, canonical
/// function families, and provider-registry semantic translation live in F#. Focused semantic
/// helpers remain for CAST types, DATEDIFF, and temporal format-token translation.
module internal FunctionalQueryNormalizer =

    type private Context =
        {
            SourceDialect: SqlAgentToolType
            TargetProvider: SqlAgentToolType
        }

    let private immutableMap mapper values =
        values
        |> Seq.map mapper
        |> ImmutableArray.CreateRange

    let private identifier (name: string) =
        SqlIdentifier(
            ImmutableArray.Create(IdentifierPart(name, false, SourceSpan.Unknown)),
            SourceSpan.Unknown)

    let private identifierText (value: SqlIdentifier) =
        value.Parts
        |> Seq.map (fun part -> part.Value)
        |> String.concat "."

    let private normalizeOperator (context: Context) (value: string) =
        let normalized =
            value.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
            |> String.concat " "
            |> fun operatorText -> operatorText.ToUpperInvariant()
            |> function
                | "!=" -> "<>"
                | "NOTIN" -> "NOT IN"
                | "NOTBETWEEN" -> "NOT BETWEEN"
                | "NOTEXISTS" -> "NOT EXISTS"
                | operatorText -> operatorText

        let failIfUnsupported (error: string | null) =
            match error with
            | null -> ()
            | message -> raise (SqlCompilationException(message))

        match normalized with
        | "ILIKE" ->
            SqlIlikeCapabilityRules.SourceValidationError(context.SourceDialect)
            |> failIfUnsupported
        | "||" ->
            SqlConcatCapabilityRules.SourceSemanticValidationError(context.SourceDialect)
            |> failIfUnsupported
        | "%" ->
            SqlModuloCapabilityRules.SourceValidationError(context.SourceDialect)
            |> failIfUnsupported
        | _ -> ()

        normalized

    let rec private normalizeStatement
        (context: Context)
        (statement: SqlStatement)
        : SqlStatement =

        match statement with
        | :? SelectStatement as select ->
            normalizeSelect context select :> SqlStatement

        | :? QueryStatement as query ->
            let head = normalizeSelect context query.Head
            let setOperations =
                query.SetOperations
                |> immutableMap (fun operation ->
                    CoreBindingAstClone.SetOperation(
                        operation,
                        normalizeStatement context operation.Query))
            let orderBy = normalizeOrderBy context query.OrderBy
            CoreBindingAstClone.Query(query, head, setOperations, orderBy) :> SqlStatement

        | :? UpdateStatement as update ->
            let assignments =
                update.Assignments
                |> immutableMap (fun assignment ->
                    CoreBindingAstClone.Assignment(
                        assignment,
                        normalizeExpression context assignment.Value))

            let predicate : SqlExpr | null =
                match update.Predicate with
                | null -> null
                | value -> normalizeExpression context value

            CoreBindingAstClone.Update(update, assignments, predicate) :> SqlStatement

        | :? DeleteStatement as delete ->
            let predicate : SqlExpr | null =
                match delete.Predicate with
                | null -> null
                | value -> normalizeExpression context value

            CoreBindingAstClone.Delete(delete, predicate) :> SqlStatement

        | other ->
            raise (SqlCompilationException(
                $"Unsupported statement during F# normalization traversal: {other.GetType().Name}"))

    and private normalizeSelect
        (context: Context)
        (select: SelectStatement) =

        let ctes =
            select.Ctes
            |> immutableMap (fun cte ->
                CoreBindingAstClone.Cte(
                    cte,
                    normalizeStatement context cte.Query))

        let selectItems =
            select.Select
            |> immutableMap (fun item ->
                CoreBindingAstClone.SelectItem(
                    item,
                    normalizeExpression context item.Expression))

        let fromSource : TableSource | null =
            match select.From with
            | null -> null
            | source -> normalizeSource context source

        let joins =
            select.Joins
            |> immutableMap (fun join ->
                let predicate : SqlExpr | null =
                    match join.Predicate with
                    | null -> null
                    | value -> normalizeExpression context value

                JoinSource(
                    join.Kind.Trim().ToUpperInvariant(),
                    normalizeSource context join.Source,
                    predicate,
                    join.Span))

        let whereExpr : SqlExpr | null =
            match select.Where with
            | null -> null
            | value -> normalizeExpression context value

        let groupBy =
            select.GroupBy
            |> immutableMap (normalizeExpression context)

        let having : SqlExpr | null =
            match select.Having with
            | null -> null
            | value -> normalizeExpression context value

        CoreBindingAstClone.Select(
            select,
            ctes,
            fromSource,
            joins,
            selectItems,
            whereExpr,
            groupBy,
            having,
            normalizeOrderBy context select.OrderBy)

    and private normalizeSource
        (context: Context)
        (source: TableSource)
        : TableSource =

        match source with
        | :? NamedTableSource as named -> named :> TableSource
        | :? DerivedTableSource as derived ->
            CoreBindingAstClone.Derived(
                derived,
                normalizeStatement context derived.Query)
            :> TableSource
        | other ->
            raise (SqlCompilationException(
                $"Unsupported table source during F# normalization traversal: {other.GetType().Name}"))

    and private normalizeOrderBy
        (context: Context)
        (orderBy: ImmutableArray<OrderByItem>) =

        orderBy
        |> immutableMap (fun item ->
            CoreBindingAstClone.OrderBy(
                item,
                normalizeExpression context item.Expression))

    and private normalizeWindow
        (context: Context)
        (window: WindowSpec) =

        CoreBindingAstClone.Window(
            window,
            window.PartitionBy |> immutableMap (normalizeExpression context),
            normalizeOrderBy context window.OrderBy)

    and private datePartUnit (expression: SqlExpr) =
        let unit =
            match expression with
            | :? BoundColumnExpr as column -> identifierText column.Name
            | :? ColumnExpr as column -> identifierText column.Name
            | :? LiteralExpr as literal ->
                match literal.Value with
                | :? string as value -> value
                | _ ->
                    raise (SqlCompilationException(
                        "DATEADD/DATEDIFF date-part unit must be an unquoted SQL keyword."))
            | _ ->
                raise (SqlCompilationException(
                    "DATEADD/DATEDIFF date-part unit must be an unquoted SQL keyword."))

        SqlDateMathCapabilityRules.NormalizeUnit(unit, "DATEADD/DATEDIFF")

    and private normalizeFunction
        (context: Context)
        (functionCall: FunctionCallExpr)
        : SqlExpr =

        let sourceName =
            identifierText functionCall.Name
            |> fun value -> value.Trim().ToUpperInvariant()

        let normalizedArguments =
            functionCall.Arguments
            |> immutableMap (normalizeExpression context)

        let normalizedFunction =
            CoreBindingAstClone.Function(
                functionCall,
                normalizedArguments,
                normalizeOrderBy context functionCall.AggregateOrderBy)

        let canonicalFunction name arguments =
            let renamed =
                CoreBindingAstClone.FunctionName(normalizedFunction, identifier name)
            CoreBindingAstClone.Function(
                renamed,
                arguments,
                normalizedFunction.AggregateOrderBy)
            :> SqlExpr

        let withName name = canonicalFunction name normalizedArguments

        let literalString (expression: SqlExpr) label =
            match expression with
            | :? LiteralExpr as literal ->
                match literal.Value with
                | :? string as value -> value
                | _ -> raise (SqlCompilationException(label + " must be a string literal."))
            | _ -> raise (SqlCompilationException(label + " must be a string literal."))

        let canonicalTemporalFormat canonicalName errorPrefix arityMessage literalLabel =
            if normalizedArguments.Length <> 2 then
                raise (SqlCompilationException(arityMessage))

            let sourceFormat = literalString normalizedArguments[1] literalLabel
            try
                let translatedFormat =
                    CoreTemporalFormatNormalizer.Translate(
                        sourceFormat,
                        context.SourceDialect,
                        context.TargetProvider)

                canonicalFunction
                    canonicalName
                    (ImmutableArray.Create<SqlExpr>(
                        normalizedArguments[0],
                        LiteralExpr(translatedFormat, functionCall.Span) :> SqlExpr))
            with
            | :? FormatException as ex ->
                raise (SqlCompilationException(
                    $"{errorPrefix} from {context.SourceDialect} to {context.TargetProvider} is not supported: {ex.Message}", ex))
            | :? NotSupportedException as ex ->
                raise (SqlCompilationException(
                    $"{errorPrefix} from {context.SourceDialect} to {context.TargetProvider} is not supported: {ex.Message}", ex))

        let canonicalStringAggregate () =
            if normalizedArguments.Length < 1 || normalizedArguments.Length > 2 then
                raise (SqlCompilationException("String aggregate requires 1 or 2 arguments."))

            if sourceName = "STRING_AGG" && normalizedArguments.Length <> 2 then
                raise (SqlCompilationException("STRING_AGG requires exactly 2 arguments."))

            let arguments =
                if sourceName = "GROUP_CONCAT" && context.SourceDialect = SqlAgentToolType.MySQL then
                    if normalizedArguments.Length <> 1 then
                        raise (SqlCompilationException(
                            "MySQL GROUP_CONCAT comma-separated arguments are multiple value expressions, not a separator. " +
                            "Core currently supports exactly one value expression; use portable STRING_AGG(value, separator) " +
                            "or native SEPARATOR 'literal' for an explicit delimiter."))

                    let separator =
                        match functionCall.AggregateSeparatorClause with
                        | null -> ","
                        | value -> value

                    ImmutableArray.Create<SqlExpr>(
                        normalizedArguments[0],
                        LiteralExpr(separator, functionCall.Span) :> SqlExpr)
                elif normalizedArguments.Length = 1 then
                    let defaultSeparator =
                        match sourceName with
                        | "LISTAGG" -> String.Empty
                        | "GROUP_CONCAT"
                        | "LIST" -> ","
                        | _ ->
                            raise (SqlCompilationException(
                                $"String aggregate '{sourceName}' requires an explicit separator."))

                    ImmutableArray.Create<SqlExpr>(
                        normalizedArguments[0],
                        LiteralExpr(defaultSeparator, functionCall.Span) :> SqlExpr)
                else
                    normalizedArguments

            FunctionCallExpr(
                identifier "CORE_STRING_AGG",
                arguments,
                normalizedFunction.IsDistinct,
                normalizedFunction.Span,
                AggregateOrderBy = normalizedFunction.AggregateOrderBy)
            :> SqlExpr

        let normalizeRegisteredFamily (contract: SqlSourceFunctionContract) =
            match contract.CanonicalizationKind with
            | SqlSourceFunctionCanonicalizationKind.DateAdd ->
                if normalizedArguments.Length <> 3 then
                    raise (SqlCompilationException("DATEADD requires exactly 3 arguments."))
                canonicalFunction
                    "CORE_DATE_ADD"
                    (ImmutableArray.Create<SqlExpr>(
                        LiteralExpr(datePartUnit normalizedArguments[0], functionCall.Span) :> SqlExpr,
                        normalizedArguments[1],
                        normalizedArguments[2]))

            | SqlSourceFunctionCanonicalizationKind.DateDiff ->
                CoreDateDiffNormalizer.Normalize(
                    normalizedFunction,
                    normalizedArguments,
                    context.SourceDialect,
                    context.TargetProvider)

            | SqlSourceFunctionCanonicalizationKind.DateFormat ->
                canonicalTemporalFormat
                    "CORE_DATE_FORMAT"
                    "portable date formatting"
                    "DATE_FORMAT/FORMAT requires exactly 2 arguments."
                    "DATE_FORMAT format"

            | SqlSourceFunctionCanonicalizationKind.DateParse ->
                canonicalTemporalFormat
                    "CORE_DATE_PARSE"
                    "formatted date parsing"
                    "TO_DATE requires exactly 2 arguments."
                    "TO_DATE format"

            | SqlSourceFunctionCanonicalizationKind.Position ->
                if normalizedArguments.Length <> 2 then
                    raise (SqlCompilationException($"{sourceName} requires exactly 2 arguments."))
                let arguments =
                    if sourceName = "STRPOS" || sourceName = "INSTR" then
                        normalizedArguments
                    else
                        ImmutableArray.Create<SqlExpr>(
                            normalizedArguments[1],
                            normalizedArguments[0])
                canonicalFunction "CORE_POSITION" arguments

            | SqlSourceFunctionCanonicalizationKind.JsonExtract ->
                canonicalFunction "CORE_JSON_EXTRACT" normalizedArguments

            | SqlSourceFunctionCanonicalizationKind.JsonSet ->
                canonicalFunction "CORE_JSON_SET" normalizedArguments

            | SqlSourceFunctionCanonicalizationKind.RegexMatch ->
                canonicalFunction "CORE_REGEX_MATCH" normalizedArguments

            | SqlSourceFunctionCanonicalizationKind.CurrentTimestamp ->
                if normalizedArguments.Length <> 0 then
                    raise (SqlCompilationException(sourceName + " does not accept arguments."))
                canonicalFunction "CORE_CURRENT_TIMESTAMP" normalizedArguments

            | SqlSourceFunctionCanonicalizationKind.StringAggregate ->
                canonicalStringAggregate ()

            | kind ->
                raise (SqlCompilationException(
                    "Unsupported source function canonicalization kind '" +
                    string kind + "' for function '" + sourceName + "'."))

        match SqlSourceFunctionRegistry.Find(sourceName) with
        | contract when not (isNull contract) ->
            normalizeRegisteredFamily contract
        | _ ->
            if SqlDatePartCapabilityRules.IsRepresentedPart(sourceName) then
                if normalizedArguments.Length <> 1 then
                    raise (SqlCompilationException($"{sourceName} requires exactly 1 argument."))

                canonicalFunction
                    "CORE_DATE_PART"
                    (ImmutableArray.Create<SqlExpr>(
                        LiteralExpr(sourceName, functionCall.Span) :> SqlExpr,
                        normalizedArguments[0]))
            else
                match sourceName with
                | "CURRENT_DATE" ->
                    if normalizedArguments.Length <> 0 then
                        raise (SqlCompilationException("CURRENT_DATE does not accept arguments."))
                    withName "CORE_CURRENT_DATE"
                | "CURRENT_TIME" ->
                    if normalizedArguments.Length <> 0 then
                        raise (SqlCompilationException("CURRENT_TIME does not accept arguments."))
                    withName "CORE_CURRENT_TIME"
                | "CURRENT_TIMESTAMP" ->
                    if normalizedArguments.Length <> 0 then
                        raise (SqlCompilationException("CURRENT_TIMESTAMP does not accept arguments."))
                    withName "CORE_CURRENT_TIMESTAMP"
                | "COALESCE" ->
                    if normalizedArguments.Length < 2 then
                        raise (SqlCompilationException("COALESCE requires at least 2 arguments."))
                    withName "COALESCE"
                | _ when SqlCanonicalFunctionRegistry.IsDirectPortable(sourceName) ->
                    withName sourceName
                | _ ->
                    FunctionalProviderFunctionNormalizer.normalize
                        context.SourceDialect
                        context.TargetProvider
                        sourceName
                        normalizedFunction
                        normalizedArguments

    and private normalizeExpression
        (context: Context)
        (expression: SqlExpr)
        : SqlExpr =

        match expression with
        | :? LiteralExpr
        | :? IntervalExpr
        | :? BoundColumnExpr
        | :? ColumnExpr -> expression

        | :? UnaryExpr as unary ->
            UnaryExpr(
                normalizeOperator context unary.Operator,
                normalizeExpression context unary.Operand,
                unary.Span)
            :> SqlExpr

        | :? BinaryExpr as binary ->
            BinaryExpr(
                normalizeExpression context binary.Left,
                normalizeOperator context binary.Operator,
                normalizeExpression context binary.Right,
                binary.Span,
                binary.LikeEscape)
            :> SqlExpr

        | :? FunctionCallExpr as functionCall ->
            normalizeFunction context functionCall

        | :? CastExpr as castExpr ->
            CastExpr(
                normalizeExpression context castExpr.Expression,
                CoreCastTypeNormalizer.Normalize(
                    castExpr.TypeName,
                    context.SourceDialect,
                    context.TargetProvider),
                castExpr.Span)
            :> SqlExpr

        | :? FilterExpr as filter ->
            CoreBindingAstClone.Filter(
                filter,
                normalizeExpression context filter.Expression,
                normalizeExpression context filter.Predicate)
            :> SqlExpr

        | :? WindowedExpr as windowed ->
            CoreBindingAstClone.Windowed(
                windowed,
                normalizeExpression context windowed.Expression,
                normalizeWindow context windowed.Window)
            :> SqlExpr

        | :? CaseExpr as caseExpr ->
            let branches =
                caseExpr.Branches
                |> immutableMap (fun branch ->
                    CaseBranch(
                        normalizeExpression context branch.Condition,
                        normalizeExpression context branch.Value))

            let elseExpression : SqlExpr | null =
                match caseExpr.ElseExpression with
                | null -> null
                | value -> normalizeExpression context value

            CoreBindingAstClone.Case(caseExpr, branches, elseExpression) :> SqlExpr

        | :? InExpr as inExpr ->
            CoreBindingAstClone.In(
                inExpr,
                normalizeExpression context inExpr.Value,
                inExpr.Items |> immutableMap (normalizeExpression context))
            :> SqlExpr

        | :? BetweenExpr as between ->
            CoreBindingAstClone.Between(
                between,
                normalizeExpression context between.Value,
                normalizeExpression context between.Lower,
                normalizeExpression context between.Upper)
            :> SqlExpr

        | :? IsNullExpr as isNullExpr ->
            CoreBindingAstClone.IsNull(
                isNullExpr,
                normalizeExpression context isNullExpr.Value)
            :> SqlExpr

        | :? SubqueryExpr as subquery ->
            CoreBindingAstClone.Subquery(
                subquery,
                normalizeStatement context subquery.Query)
            :> SqlExpr

        | :? ExistsExpr as exists ->
            CoreBindingAstClone.Exists(
                exists,
                normalizeStatement context exists.Query)
            :> SqlExpr

        | other ->
            raise (SqlCompilationException(
                $"Unsupported expression during F# normalization: {other.GetType().Name}"))

    let normalize
        (statement: BoundStatement)
        (targetProvider: SqlAgentToolType)
        : CanonicalStatement =

        ArgumentNullException.ThrowIfNull(statement)

        let context =
            {
                SourceDialect = statement.SourceDialect
                TargetProvider = targetProvider
            }

        CanonicalStatement(
            normalizeStatement context statement.Statement,
            statement.Facts,
            statement.SourceDialect,
            targetProvider)
