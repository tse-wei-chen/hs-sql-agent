namespace HsSqlAgent.SqlCore.Core.Normalization

open System
open System.Collections.Immutable
open HsSqlAgent.SqlCore.Core.Ast
open HsSqlAgent.SqlCore.Core.Binding
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models

/// F# ownership of canonical DATEDIFF normalization.
/// Preserves native SQL Server/Firebird three-argument behavior on same-provider lowering while
/// restricting the cross-dialect portable intersection to integral calendar-day differences.
[<AbstractClass; Sealed>]
type internal CoreDateDiffNormalizer private () =

    static member private Identifier(name: string) =
        SqlIdentifier(
            ImmutableArray.Create(IdentifierPart(name, false, SourceSpan.Unknown)),
            SourceSpan.Unknown)

    static member private IdentifierText(identifier: SqlIdentifier) =
        identifier.Parts
        |> Seq.map (fun part -> part.Value)
        |> String.concat "."

    static member private DatePartUnit(expression: SqlExpr) =
        let unit =
            match expression with
            | :? BoundColumnExpr as column ->
                CoreDateDiffNormalizer.IdentifierText(column.Name)
            | :? ColumnExpr as column ->
                CoreDateDiffNormalizer.IdentifierText(column.Name)
            | :? LiteralExpr as literal ->
                match literal.Value with
                | :? string as value -> value
                | _ ->
                    raise (SqlCompilationException(
                        "DATEDIFF date-part unit must be an unquoted SQL keyword."))
            | _ ->
                raise (SqlCompilationException(
                    "DATEDIFF date-part unit must be an unquoted SQL keyword."))

        SqlDateMathCapabilityRules.NormalizeUnit(unit, "DATEDIFF")

    static member private Canonical(
        original: FunctionCallExpr,
        arguments: ImmutableArray<SqlExpr>) =

        let renamed =
            CoreBindingAstClone.FunctionName(
                original,
                CoreDateDiffNormalizer.Identifier("CORE_DATE_DIFF"))

        CoreBindingAstClone.Function(
            renamed,
            arguments,
            renamed.AggregateOrderBy)
        :> SqlExpr

    static member private DateOnlyOperand(
        expression: SqlExpr,
        targetProvider: SqlAgentToolType,
        span: SourceSpan) =

        match targetProvider with
        | SqlAgentToolType.Oracle ->
            let cast = CastExpr(expression, "DATE", span) :> SqlExpr
            FunctionCallExpr(
                CoreDateDiffNormalizer.Identifier("TRUNC"),
                ImmutableArray.Create<SqlExpr>(cast),
                false,
                span)
            :> SqlExpr

        | SqlAgentToolType.MySQL
        | SqlAgentToolType.Sqlite ->
            FunctionCallExpr(
                CoreDateDiffNormalizer.Identifier("DATE"),
                ImmutableArray.Create<SqlExpr>(expression),
                false,
                span)
            :> SqlExpr

        | SqlAgentToolType.Postgres
        | SqlAgentToolType.MsSqlServer
        | SqlAgentToolType.Firebird ->
            CastExpr(expression, "DATE", span) :> SqlExpr

        | other ->
            raise (SqlCompilationException(
                $"Unsupported target provider '{other}' for portable DATEDIFF DAY normalization."))

    static member private PortableDayDifference(
        original: FunctionCallExpr,
        startExpression: SqlExpr,
        endExpression: SqlExpr,
        targetProvider: SqlAgentToolType) =

        let arguments =
            ImmutableArray.Create<SqlExpr>(
                LiteralExpr("DAY", original.Span) :> SqlExpr,
                CoreDateDiffNormalizer.DateOnlyOperand(
                    startExpression,
                    targetProvider,
                    original.Span),
                CoreDateDiffNormalizer.DateOnlyOperand(
                    endExpression,
                    targetProvider,
                    original.Span))

        let canonical = CoreDateDiffNormalizer.Canonical(original, arguments)
        if targetProvider = SqlAgentToolType.Sqlite then
            CastExpr(canonical, "INTEGER", original.Span) :> SqlExpr
        else
            canonical

    static member private NormalizeTwoArgumentDateDiff(
        original: FunctionCallExpr,
        arguments: ImmutableArray<SqlExpr>,
        targetProvider: SqlAgentToolType) =

        CoreDateDiffNormalizer.PortableDayDifference(
            original,
            arguments[1],
            arguments[0],
            targetProvider)

    static member private NormalizeThreeArgumentDateDiff(
        original: FunctionCallExpr,
        arguments: ImmutableArray<SqlExpr>,
        sourceDialect: SqlAgentToolType,
        targetProvider: SqlAgentToolType) =

        let unit = CoreDateDiffNormalizer.DatePartUnit(arguments[0])
        let preservesNativeBoundarySemantics =
            (sourceDialect = SqlAgentToolType.MsSqlServer
             || sourceDialect = SqlAgentToolType.Firebird)
            && sourceDialect = targetProvider

        if preservesNativeBoundarySemantics then
            CoreDateDiffNormalizer.Canonical(
                original,
                ImmutableArray.Create<SqlExpr>(
                    LiteralExpr(unit, original.Span) :> SqlExpr,
                    arguments[1],
                    arguments[2]))
        elif unit <> "DAY" then
            let capability =
                "core_date_diff.unit." + unit.ToLowerInvariant()
            raise (SqlCompilationException(
                $"Cross-dialect DATEDIFF unit '{unit}' from {sourceDialect} to {targetProvider} is not translated: " +
                $"SQL capability '{capability}' is not modeled losslessly. " +
                "DAY is the currently modeled portable intersection."))
        else
            CoreDateDiffNormalizer.PortableDayDifference(
                original,
                arguments[1],
                arguments[2],
                targetProvider)

    static member Normalize(
        original: FunctionCallExpr,
        arguments: ImmutableArray<SqlExpr>,
        sourceDialect: SqlAgentToolType,
        targetProvider: SqlAgentToolType) =

        match arguments.Length with
        | 2 ->
            CoreDateDiffNormalizer.NormalizeTwoArgumentDateDiff(
                original,
                arguments,
                targetProvider)
        | 3 ->
            CoreDateDiffNormalizer.NormalizeThreeArgumentDateDiff(
                original,
                arguments,
                sourceDialect,
                targetProvider)
        | count ->
            raise (SqlCompilationException(
                $"DATEDIFF requires either the portable 2-argument (end, start) shape or the " +
                $"3-argument (unit, start, end) shape; received {count} arguments."))
