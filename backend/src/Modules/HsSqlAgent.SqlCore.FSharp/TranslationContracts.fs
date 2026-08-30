#nowarn "3261" "3262"

namespace HsSqlAgent.SqlCore.SqlTranslation.Diagnostics

open System.Collections.Generic

type UnknownFunctionPolicy =
    | Passthrough = 0
    | WarnAndPassthrough = 1
    | Throw = 2

type DiagnosticSeverity =
    | Info = 0
    | Warning = 1
    | Error = 2

type FunctionPortability =
    | Native = 0
    | Equivalent = 1
    | Emulated = 2
    | Unsupported = 3
    | Unknown = 4

[<Sealed>]
type TranslationDiagnostic(code: string, severity: DiagnosticSeverity, message: string, portability: System.Nullable<FunctionPortability>) =
    new(code: string, severity: DiagnosticSeverity, message: string) =
        TranslationDiagnostic(code,severity,message,System.Nullable())
    member _.Code = code
    member _.Severity = severity
    member _.Message = message
    member _.Portability = portability

[<Sealed>]
type SqlTranslationResult(sql: string, diagnostics: IReadOnlyList<TranslationDiagnostic>) =
    member _.Sql = sql
    member _.Diagnostics = diagnostics

namespace HsSqlAgent.SqlCore.SqlTranslation.Functions

open System
open System.Collections.Generic
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.SqlTranslation.Diagnostics

type SemanticFunction =
    | StringLength = 0
    | Ceiling = 1
    | Random = 2
    | Repeat = 3
    | Lower = 4
    | Upper = 5
    | Substring = 6
    | Coalesce = 7
    | CurrentDate = 8
    | CurrentTimestamp = 9
    | DateAdd = 10
    | DateDiff = 11
    | DatePart = 12
    | DateFormat = 13
    | JsonExtract = 14
    | JsonSet = 15
    | RegexMatch = 16
    | StringAggregate = 17

type FunctionTranslationKind =
    | Identity = 0
    | Rename = 1
    | Semantic = 2
    | Template = 3
    | Specialized = 4

[<Sealed>]
type FunctionDefinition(
    dialect: SqlAgentToolType,
    name: string,
    aliases: IReadOnlyList<string>,
    semantic: Nullable<SemanticFunction>,
    minArguments: int,
    maxArguments: Nullable<int>,
    translationKind: FunctionTranslationKind,
    template: string | null,
    translator: string | null,
    portability: FunctionPortability) =
    member _.Dialect = dialect
    member _.Name = name
    member _.Aliases = aliases
    member _.Semantic = semantic
    member _.MinArguments = minArguments
    member _.MaxArguments = maxArguments
    member _.TranslationKind = translationKind
    member _.Template = template
    member _.Translator = translator
    member _.Portability = portability
    member _.AcceptsArgumentCount(count: int) =
        count >= minArguments && (not maxArguments.HasValue || count <= maxArguments.Value)

type IFunctionRegistry =
    abstract Find: SqlAgentToolType * string * int -> FunctionDefinition | null
    abstract Find: SqlAgentToolType * SemanticFunction * int -> FunctionDefinition | null

module private FunctionCatalog =
    let emptyAliases = Array.empty<string> :> IReadOnlyList<string>

    let def dialect name aliases semantic minArgs maxArgs kind template =
        FunctionDefinition(
            dialect,
            name,
            (aliases |> List.toArray :> IReadOnlyList<string>),
            Nullable(semantic),
            minArgs,
            (match maxArgs with Some value -> Nullable value | None -> Nullable()),
            kind,
            template,
            null,
            FunctionPortability.Native)

    let semantic dialect name semantic minArgs maxArgs =
        def dialect name [] semantic minArgs maxArgs FunctionTranslationKind.Semantic null
    let semanticAlias dialect name aliases semantic minArgs maxArgs =
        def dialect name aliases semantic minArgs maxArgs FunctionTranslationKind.Semantic null
    let template dialect name semantic minArgs maxArgs template =
        def dialect name [] semantic minArgs maxArgs FunctionTranslationKind.Template template

    let all =
        [ semantic SqlAgentToolType.Firebird "CHAR_LENGTH" SemanticFunction.StringLength 1 (Some 1)
          semantic SqlAgentToolType.Firebird "CEIL" SemanticFunction.Ceiling 1 (Some 1)
          semantic SqlAgentToolType.Firebird "COALESCE" SemanticFunction.Coalesce 2 None
          semantic SqlAgentToolType.Firebird "RAND" SemanticFunction.Random 0 (Some 0)
          template SqlAgentToolType.Firebird "CURRENT_TIMESTAMP" SemanticFunction.CurrentTimestamp 0 (Some 0) "@CurrentTimestamp"
          template SqlAgentToolType.Firebird "LIST" SemanticFunction.StringAggregate 1 (Some 1) "LIST($1, ',')"
          semantic SqlAgentToolType.Firebird "LIST" SemanticFunction.StringAggregate 2 (Some 2)

          semantic SqlAgentToolType.MsSqlServer "LEN" SemanticFunction.StringLength 1 (Some 1)
          semantic SqlAgentToolType.MsSqlServer "CEILING" SemanticFunction.Ceiling 1 (Some 1)
          semanticAlias SqlAgentToolType.MsSqlServer "ISNULL" ["COALESCE"] SemanticFunction.Coalesce 2 (Some 2)
          semantic SqlAgentToolType.MsSqlServer "COALESCE" SemanticFunction.Coalesce 3 None
          semantic SqlAgentToolType.MsSqlServer "RAND" SemanticFunction.Random 0 (Some 0)
          semantic SqlAgentToolType.MsSqlServer "REPLICATE" SemanticFunction.Repeat 2 (Some 2)
          template SqlAgentToolType.MsSqlServer "GETDATE" SemanticFunction.CurrentTimestamp 0 (Some 0) "@CurrentTimestamp"
          template SqlAgentToolType.MsSqlServer "STRING_AGG" SemanticFunction.StringAggregate 1 (Some 1) "STRING_AGG($1, ',')"
          semantic SqlAgentToolType.MsSqlServer "STRING_AGG" SemanticFunction.StringAggregate 2 (Some 2)

          semantic SqlAgentToolType.MySQL "CHAR_LENGTH" SemanticFunction.StringLength 1 (Some 1)
          semantic SqlAgentToolType.MySQL "CEIL" SemanticFunction.Ceiling 1 (Some 1)
          semanticAlias SqlAgentToolType.MySQL "IFNULL" ["COALESCE"] SemanticFunction.Coalesce 2 (Some 2)
          semantic SqlAgentToolType.MySQL "COALESCE" SemanticFunction.Coalesce 3 None
          semantic SqlAgentToolType.MySQL "RAND" SemanticFunction.Random 0 (Some 0)
          semantic SqlAgentToolType.MySQL "REPEAT" SemanticFunction.Repeat 2 (Some 2)
          template SqlAgentToolType.MySQL "NOW" SemanticFunction.CurrentTimestamp 0 (Some 0) "@CurrentTimestamp"
          semantic SqlAgentToolType.MySQL "GROUP_CONCAT" SemanticFunction.StringAggregate 1 (Some 1)
          semantic SqlAgentToolType.MySQL "GROUP_CONCAT" SemanticFunction.StringAggregate 2 (Some 2)

          semantic SqlAgentToolType.Oracle "LENGTH" SemanticFunction.StringLength 1 (Some 1)
          semantic SqlAgentToolType.Oracle "CEIL" SemanticFunction.Ceiling 1 (Some 1)
          semanticAlias SqlAgentToolType.Oracle "NVL" ["COALESCE"] SemanticFunction.Coalesce 2 (Some 2)
          semantic SqlAgentToolType.Oracle "COALESCE" SemanticFunction.Coalesce 3 None
          template SqlAgentToolType.Oracle "CURRENT_TIMESTAMP" SemanticFunction.CurrentTimestamp 0 (Some 0) "@CurrentTimestamp"
          template SqlAgentToolType.Oracle "LISTAGG" SemanticFunction.StringAggregate 1 (Some 1) "LISTAGG($1, ',')"
          semantic SqlAgentToolType.Oracle "LISTAGG" SemanticFunction.StringAggregate 2 (Some 2)

          semantic SqlAgentToolType.Postgres "LENGTH" SemanticFunction.StringLength 1 (Some 1)
          semantic SqlAgentToolType.Postgres "CEIL" SemanticFunction.Ceiling 1 (Some 1)
          semantic SqlAgentToolType.Postgres "COALESCE" SemanticFunction.Coalesce 2 None
          semantic SqlAgentToolType.Postgres "RANDOM" SemanticFunction.Random 0 (Some 0)
          semantic SqlAgentToolType.Postgres "REPEAT" SemanticFunction.Repeat 2 (Some 2)
          template SqlAgentToolType.Postgres "NOW" SemanticFunction.CurrentTimestamp 0 (Some 0) "@CurrentTimestamp"
          template SqlAgentToolType.Postgres "STRING_AGG" SemanticFunction.StringAggregate 1 (Some 1) "STRING_AGG($1, ',')"
          semantic SqlAgentToolType.Postgres "STRING_AGG" SemanticFunction.StringAggregate 2 (Some 2)

          semantic SqlAgentToolType.Sqlite "LENGTH" SemanticFunction.StringLength 1 (Some 1)
          semantic SqlAgentToolType.Sqlite "CEIL" SemanticFunction.Ceiling 1 (Some 1)
          semanticAlias SqlAgentToolType.Sqlite "SUBSTR" ["SUBSTRING"] SemanticFunction.Substring 2 (Some 3)
          semantic SqlAgentToolType.Sqlite "COALESCE" SemanticFunction.Coalesce 2 None
          semantic SqlAgentToolType.Sqlite "RANDOM" SemanticFunction.Random 0 (Some 0)
          template SqlAgentToolType.Sqlite "CURRENT_TIMESTAMP" SemanticFunction.CurrentTimestamp 0 (Some 0) "@CurrentTimestamp"
          semantic SqlAgentToolType.Sqlite "GROUP_CONCAT" SemanticFunction.StringAggregate 1 (Some 1)
          semantic SqlAgentToolType.Sqlite "GROUP_CONCAT" SemanticFunction.StringAggregate 2 (Some 2) ]

[<Sealed>]
type FunctionRegistry(definitions: IEnumerable<FunctionDefinition>) =
    let values = if isNull definitions then nullArg "definitions" else definitions |> Seq.toArray
    interface IFunctionRegistry with
        member _.Find(dialect, functionName, argumentCount) =
            values
            |> Array.tryFind (fun d ->
                d.Dialect = dialect
                && d.AcceptsArgumentCount(argumentCount)
                && (String.Equals(d.Name,functionName,StringComparison.OrdinalIgnoreCase)
                    || d.Aliases |> Seq.exists (fun alias -> String.Equals(alias,functionName,StringComparison.OrdinalIgnoreCase))))
            |> Option.toObj
        member _.Find(dialect, semantic, argumentCount) =
            values
            |> Array.tryFind (fun d ->
                d.Dialect = dialect
                && d.Semantic.HasValue
                && d.Semantic.Value = semantic
                && d.AcceptsArgumentCount(argumentCount))
            |> Option.toObj

[<AbstractClass; Sealed>]
type FunctionDefinitionLoader private () =
    static member LoadEmbedded() = FunctionCatalog.all :> IEnumerable<FunctionDefinition>

namespace HsSqlAgent.SqlCore.SqlTranslation.DateFormats

open System
open System.Collections.Generic
open System.Text
open HsSqlAgent.SqlCore.Enums

[<AbstractClass>]
type DateFormatPart() = class end

[<Sealed>]
type DateFormatLiteral(value: string) =
    inherit DateFormatPart()
    member _.Value = value

type DateFormatTokenKind =
    | Year4 = 0
    | Year2 = 1
    | Month2 = 2
    | MonthName = 3
    | MonthShortName = 4
    | Day2 = 5
    | DayNoPadding = 6
    | Hour24 = 7
    | Hour12 = 8
    | Minute = 9
    | Second = 10
    | AmPm = 11

[<Sealed>]
type DateFormatToken(kind: DateFormatTokenKind) =
    inherit DateFormatPart()
    member _.Kind = kind

type IDateFormatDialect =
    abstract Parse: string -> IReadOnlyList<DateFormatPart>
    abstract Render: IReadOnlyList<DateFormatPart> -> string

module private DateFormat =
    let percentToken dialect token =
        match token with
        | 'Y' -> Some DateFormatTokenKind.Year4
        | 'y' -> Some DateFormatTokenKind.Year2
        | 'm' -> Some DateFormatTokenKind.Month2
        | 'b' -> Some DateFormatTokenKind.MonthShortName
        | 'M' when dialect = SqlAgentToolType.MySQL -> Some DateFormatTokenKind.MonthName
        | 'M' when dialect = SqlAgentToolType.Sqlite -> Some DateFormatTokenKind.Minute
        | 'd' -> Some DateFormatTokenKind.Day2
        | 'e' -> Some DateFormatTokenKind.DayNoPadding
        | 'H' -> Some DateFormatTokenKind.Hour24
        | 'h' | 'I' -> Some DateFormatTokenKind.Hour12
        | 'i' when dialect = SqlAgentToolType.MySQL -> Some DateFormatTokenKind.Minute
        | 'S' | 's' -> Some DateFormatTokenKind.Second
        | 'p' -> Some DateFormatTokenKind.AmPm
        | _ -> None

    let parsePercent dialect (value: string) =
        let result = ResizeArray<DateFormatPart>()
        let literal = StringBuilder()
        let flush () =
            if literal.Length > 0 then
                result.Add(DateFormatLiteral(literal.ToString()))
                literal.Clear() |> ignore
        let mutable i = 0
        while i < value.Length do
            if value[i] <> '%' || i + 1 >= value.Length then
                literal.Append(value[i]) |> ignore
            else
                i <- i + 1
                let specifier = value[i]
                if specifier = '%' then literal.Append('%') |> ignore
                else
                    match percentToken dialect specifier with
                    | Some token -> flush(); result.Add(DateFormatToken(token))
                    | None -> raise (FormatException("Unsupported " + string dialect + " date-format token '%" + string specifier + "'."))
            i <- i + 1
        flush()
        result.ToArray() :> IReadOnlyList<DateFormatPart>

    let namedTokens dialect =
        if dialect = SqlAgentToolType.MsSqlServer then
            [ "yyyy",DateFormatTokenKind.Year4; "yy",DateFormatTokenKind.Year2
              "MMMM",DateFormatTokenKind.MonthName; "MMM",DateFormatTokenKind.MonthShortName
              "MM",DateFormatTokenKind.Month2; "dd",DateFormatTokenKind.Day2; "d",DateFormatTokenKind.DayNoPadding
              "HH",DateFormatTokenKind.Hour24; "hh",DateFormatTokenKind.Hour12
              "mm",DateFormatTokenKind.Minute; "ss",DateFormatTokenKind.Second; "tt",DateFormatTokenKind.AmPm ]
        else
            [ "YYYY",DateFormatTokenKind.Year4; "HH24",DateFormatTokenKind.Hour24
              "HH12",DateFormatTokenKind.Hour12; "MONTH",DateFormatTokenKind.MonthName
              "MON",DateFormatTokenKind.MonthShortName; "YY",DateFormatTokenKind.Year2
              "MM",DateFormatTokenKind.Month2; "FMDD",DateFormatTokenKind.DayNoPadding
              "DD",DateFormatTokenKind.Day2; "MI",DateFormatTokenKind.Minute
              "SS",DateFormatTokenKind.Second; "AM",DateFormatTokenKind.AmPm; "PM",DateFormatTokenKind.AmPm ]

    let parseNamed dialect (value: string) =
        let tokens = namedTokens dialect
        let result = ResizeArray<DateFormatPart>()
        let literal = StringBuilder()
        let flush () =
            if literal.Length > 0 then
                result.Add(DateFormatLiteral(literal.ToString()))
                literal.Clear() |> ignore
        let mutable pos = 0
        while pos < value.Length do
            match tokens |> List.tryFind (fun (text,_) -> value.AsSpan(pos).StartsWith(text, StringComparison.Ordinal)) with
            | Some(text,kind) ->
                flush()
                result.Add(DateFormatToken(kind))
                pos <- pos + text.Length
            | None ->
                if Char.IsLetter(value[pos]) then
                    raise (FormatException("Unsupported " + string dialect + " date-format token near '" + value.Substring(pos) + "'."))
                literal.Append(value[pos]) |> ignore
                pos <- pos + 1
        flush()
        result.ToArray() :> IReadOnlyList<DateFormatPart>

    let renderPercent dialect (parts: IReadOnlyList<DateFormatPart>) =
        parts
        |> Seq.map (function
            | :? DateFormatLiteral as literal -> literal.Value.Replace("%","%%")
            | :? DateFormatToken as token ->
                match dialect, token.Kind with
                | _, DateFormatTokenKind.Year4 -> "%Y"
                | _, DateFormatTokenKind.Year2 -> "%y"
                | _, DateFormatTokenKind.Month2 -> "%m"
                | SqlAgentToolType.MySQL, DateFormatTokenKind.MonthName -> "%M"
                | _, DateFormatTokenKind.MonthName -> "%m"
                | SqlAgentToolType.MySQL, DateFormatTokenKind.MonthShortName -> "%b"
                | _, DateFormatTokenKind.MonthShortName -> "%m"
                | _, DateFormatTokenKind.Day2 -> "%d"
                | SqlAgentToolType.MySQL, DateFormatTokenKind.DayNoPadding -> "%e"
                | _, DateFormatTokenKind.DayNoPadding -> "%d"
                | _, DateFormatTokenKind.Hour24 -> "%H"
                | _, DateFormatTokenKind.Hour12 -> "%I"
                | SqlAgentToolType.MySQL, DateFormatTokenKind.Minute -> "%i"
                | _, DateFormatTokenKind.Minute -> "%M"
                | _, DateFormatTokenKind.Second -> "%S"
                | _, DateFormatTokenKind.AmPm -> "%p"
                | _ -> raise (ArgumentOutOfRangeException("parts"))
            | _ -> raise (ArgumentOutOfRangeException("parts")))
        |> String.concat ""

    let renderNamed dialect (parts: IReadOnlyList<DateFormatPart>) =
        parts
        |> Seq.map (function
            | :? DateFormatLiteral as literal -> literal.Value
            | :? DateFormatToken as token ->
                match dialect, token.Kind with
                | SqlAgentToolType.MsSqlServer, DateFormatTokenKind.Year4 -> "yyyy"
                | SqlAgentToolType.MsSqlServer, DateFormatTokenKind.Year2 -> "yy"
                | SqlAgentToolType.MsSqlServer, DateFormatTokenKind.Month2 -> "MM"
                | SqlAgentToolType.MsSqlServer, DateFormatTokenKind.MonthName -> "MMMM"
                | SqlAgentToolType.MsSqlServer, DateFormatTokenKind.MonthShortName -> "MMM"
                | SqlAgentToolType.MsSqlServer, DateFormatTokenKind.Day2 -> "dd"
                | SqlAgentToolType.MsSqlServer, DateFormatTokenKind.DayNoPadding -> "d"
                | SqlAgentToolType.MsSqlServer, DateFormatTokenKind.Hour24 -> "HH"
                | SqlAgentToolType.MsSqlServer, DateFormatTokenKind.Hour12 -> "hh"
                | SqlAgentToolType.MsSqlServer, DateFormatTokenKind.Minute -> "mm"
                | SqlAgentToolType.MsSqlServer, DateFormatTokenKind.Second -> "ss"
                | SqlAgentToolType.MsSqlServer, DateFormatTokenKind.AmPm -> "tt"
                | _, DateFormatTokenKind.Year4 -> "YYYY"
                | _, DateFormatTokenKind.Year2 -> "YY"
                | _, DateFormatTokenKind.Month2 -> "MM"
                | _, DateFormatTokenKind.MonthName -> "MONTH"
                | _, DateFormatTokenKind.MonthShortName -> "MON"
                | _, DateFormatTokenKind.Day2 -> "DD"
                | _, DateFormatTokenKind.DayNoPadding -> "FMDD"
                | _, DateFormatTokenKind.Hour24 -> "HH24"
                | _, DateFormatTokenKind.Hour12 -> "HH12"
                | _, DateFormatTokenKind.Minute -> "MI"
                | _, DateFormatTokenKind.Second -> "SS"
                | _, DateFormatTokenKind.AmPm -> "AM"
                | _ -> raise (ArgumentOutOfRangeException("parts"))
            | _ -> raise (ArgumentOutOfRangeException("parts")))
        |> String.concat ""

[<Sealed>]
type DateFormatTranslator() =
    member _.Parse(value: string, source: SqlAgentToolType) =
        match source with
        | SqlAgentToolType.MySQL | SqlAgentToolType.Sqlite -> DateFormat.parsePercent source value
        | SqlAgentToolType.MsSqlServer | SqlAgentToolType.Postgres | SqlAgentToolType.Oracle -> DateFormat.parseNamed source value
        | _ -> raise (NotSupportedException("Date format parsing is not supported for " + string source + "."))
    member _.Render(format: IReadOnlyList<DateFormatPart>, target: SqlAgentToolType) =
        match target with
        | SqlAgentToolType.MySQL | SqlAgentToolType.Sqlite -> DateFormat.renderPercent target format
        | SqlAgentToolType.MsSqlServer | SqlAgentToolType.Postgres | SqlAgentToolType.Oracle -> DateFormat.renderNamed target format
        | _ -> raise (NotSupportedException("Date format rendering is not supported for " + string target + "."))
    member this.Translate(value: string, source: SqlAgentToolType, target: SqlAgentToolType) =
        this.Render(this.Parse(value,source),target)
