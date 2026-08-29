namespace HsSqlAgent.SqlCore.Core.Normalization

open System
open System.Text
open HsSqlAgent.SqlCore.Enums

[<RequireQualifiedAccess>]
type private TemporalFormatToken =
    | Year4
    | Year2
    | Month2
    | MonthName
    | MonthShortName
    | Day2
    | DayNoPadding
    | Hour24
    | Hour12
    | Minute
    | Second
    | AmPm

[<RequireQualifiedAccess>]
type private TemporalFormatPart =
    | Literal of string
    | Token of TemporalFormatToken

module private FunctionalTemporalFormatTranslation =

    let private unsupportedParse (dialect: SqlAgentToolType) =
        raise (NotSupportedException($"Date format parsing is not supported for {dialect}."))

    let private unsupportedRender (dialect: SqlAgentToolType) =
        raise (NotSupportedException($"Date format rendering is not supported for {dialect}."))

    let private flushLiteral (result: ResizeArray<TemporalFormatPart>) (literal: StringBuilder) =
        if literal.Length > 0 then
            result.Add(TemporalFormatPart.Literal(literal.ToString()))
            literal.Clear() |> ignore

    let private parsePercentToken (dialect: SqlAgentToolType) (token: char) =
        match token with
        | 'Y' -> Some TemporalFormatToken.Year4
        | 'y' -> Some TemporalFormatToken.Year2
        | 'm' -> Some TemporalFormatToken.Month2
        | 'b' -> Some TemporalFormatToken.MonthShortName
        | 'M' when dialect = SqlAgentToolType.MySQL -> Some TemporalFormatToken.MonthName
        | 'M' when dialect = SqlAgentToolType.Sqlite -> Some TemporalFormatToken.Minute
        | 'd' -> Some TemporalFormatToken.Day2
        | 'e' -> Some TemporalFormatToken.DayNoPadding
        | 'H' -> Some TemporalFormatToken.Hour24
        | 'h'
        | 'I' -> Some TemporalFormatToken.Hour12
        | 'i' when dialect = SqlAgentToolType.MySQL -> Some TemporalFormatToken.Minute
        | 'S'
        | 's' -> Some TemporalFormatToken.Second
        | 'p' -> Some TemporalFormatToken.AmPm
        | _ -> None

    let private parsePercent (dialect: SqlAgentToolType) (value: string) =
        let result = ResizeArray<TemporalFormatPart>()
        let literal = StringBuilder()
        let mutable index = 0

        while index < value.Length do
            if value[index] <> '%' || index + 1 >= value.Length then
                literal.Append(value[index]) |> ignore
                index <- index + 1
            else
                let specifier = value[index + 1]
                index <- index + 2
                if specifier = '%' then
                    literal.Append('%') |> ignore
                else
                    match parsePercentToken dialect specifier with
                    | Some token ->
                        flushLiteral result literal
                        result.Add(TemporalFormatPart.Token token)
                    | None ->
                        raise (FormatException($"Unsupported {dialect} date-format token '%{specifier}'."))

        flushLiteral result literal
        result |> Seq.toList

    let private renderPercentPart (dialect: SqlAgentToolType) (part: TemporalFormatPart) =
        match part with
        | TemporalFormatPart.Literal value -> value.Replace("%", "%%")
        | TemporalFormatPart.Token token ->
            match dialect, token with
            | _, TemporalFormatToken.Year4 -> "%Y"
            | _, TemporalFormatToken.Year2 -> "%y"
            | _, TemporalFormatToken.Month2 -> "%m"
            | SqlAgentToolType.MySQL, TemporalFormatToken.MonthName -> "%M"
            | _, TemporalFormatToken.MonthName -> "%m"
            | SqlAgentToolType.MySQL, TemporalFormatToken.MonthShortName -> "%b"
            | _, TemporalFormatToken.MonthShortName -> "%m"
            | _, TemporalFormatToken.Day2 -> "%d"
            | SqlAgentToolType.MySQL, TemporalFormatToken.DayNoPadding -> "%e"
            | _, TemporalFormatToken.DayNoPadding -> "%d"
            | _, TemporalFormatToken.Hour24 -> "%H"
            | _, TemporalFormatToken.Hour12 -> "%I"
            | SqlAgentToolType.MySQL, TemporalFormatToken.Minute -> "%i"
            | _, TemporalFormatToken.Minute -> "%M"
            | _, TemporalFormatToken.Second -> "%S"
            | _, TemporalFormatToken.AmPm -> "%p"
            | _ -> raise (ArgumentOutOfRangeException(nameof part))

    let private sqlServerTokens =
        [ "yyyy", TemporalFormatToken.Year4
          "yy", TemporalFormatToken.Year2
          "MMMM", TemporalFormatToken.MonthName
          "MMM", TemporalFormatToken.MonthShortName
          "MM", TemporalFormatToken.Month2
          "dd", TemporalFormatToken.Day2
          "d", TemporalFormatToken.DayNoPadding
          "HH", TemporalFormatToken.Hour24
          "hh", TemporalFormatToken.Hour12
          "mm", TemporalFormatToken.Minute
          "ss", TemporalFormatToken.Second
          "tt", TemporalFormatToken.AmPm ]

    let private oracleStyleTokens =
        [ "YYYY", TemporalFormatToken.Year4
          "HH24", TemporalFormatToken.Hour24
          "HH12", TemporalFormatToken.Hour12
          "MONTH", TemporalFormatToken.MonthName
          "MON", TemporalFormatToken.MonthShortName
          "YY", TemporalFormatToken.Year2
          "MM", TemporalFormatToken.Month2
          "FMDD", TemporalFormatToken.DayNoPadding
          "DD", TemporalFormatToken.Day2
          "MI", TemporalFormatToken.Minute
          "SS", TemporalFormatToken.Second
          "AM", TemporalFormatToken.AmPm
          "PM", TemporalFormatToken.AmPm ]

    let private sourceNamedTokens (dialect: SqlAgentToolType) =
        if dialect = SqlAgentToolType.MsSqlServer then sqlServerTokens else oracleStyleTokens

    let private tryNamedToken (tokens: (string * TemporalFormatToken) list) (value: string) position =
        tokens
        |> List.tryFind (fun (text, _) ->
            position + text.Length <= value.Length
            && value.AsSpan(position, text.Length).SequenceEqual(text.AsSpan()))

    let private parseNamed (dialect: SqlAgentToolType) (value: string) =
        let tokens = sourceNamedTokens dialect
        let result = ResizeArray<TemporalFormatPart>()
        let literal = StringBuilder()
        let mutable position = 0

        while position < value.Length do
            match tryNamedToken tokens value position with
            | Some (text, token) ->
                flushLiteral result literal
                result.Add(TemporalFormatPart.Token token)
                position <- position + text.Length
            | None ->
                if Char.IsLetter(value[position]) then
                    raise (FormatException($"Unsupported {dialect} date-format token near '{value.Substring(position)}'."))
                literal.Append(value[position]) |> ignore
                position <- position + 1

        flushLiteral result literal
        result |> Seq.toList

    let private renderNamedToken (dialect: SqlAgentToolType) (token: TemporalFormatToken) =
        match dialect, token with
        | SqlAgentToolType.MsSqlServer, TemporalFormatToken.Year4 -> "yyyy"
        | SqlAgentToolType.MsSqlServer, TemporalFormatToken.Year2 -> "yy"
        | SqlAgentToolType.MsSqlServer, TemporalFormatToken.Month2 -> "MM"
        | SqlAgentToolType.MsSqlServer, TemporalFormatToken.MonthName -> "MMMM"
        | SqlAgentToolType.MsSqlServer, TemporalFormatToken.MonthShortName -> "MMM"
        | SqlAgentToolType.MsSqlServer, TemporalFormatToken.Day2 -> "dd"
        | SqlAgentToolType.MsSqlServer, TemporalFormatToken.DayNoPadding -> "d"
        | SqlAgentToolType.MsSqlServer, TemporalFormatToken.Hour24 -> "HH"
        | SqlAgentToolType.MsSqlServer, TemporalFormatToken.Hour12 -> "hh"
        | SqlAgentToolType.MsSqlServer, TemporalFormatToken.Minute -> "mm"
        | SqlAgentToolType.MsSqlServer, TemporalFormatToken.Second -> "ss"
        | SqlAgentToolType.MsSqlServer, TemporalFormatToken.AmPm -> "tt"
        | _, TemporalFormatToken.Year4 -> "YYYY"
        | _, TemporalFormatToken.Year2 -> "YY"
        | _, TemporalFormatToken.Month2 -> "MM"
        | _, TemporalFormatToken.MonthName -> "MONTH"
        | _, TemporalFormatToken.MonthShortName -> "MON"
        | _, TemporalFormatToken.Day2 -> "DD"
        | _, TemporalFormatToken.DayNoPadding -> "FMDD"
        | _, TemporalFormatToken.Hour24 -> "HH24"
        | _, TemporalFormatToken.Hour12 -> "HH12"
        | _, TemporalFormatToken.Minute -> "MI"
        | _, TemporalFormatToken.Second -> "SS"
        | _, TemporalFormatToken.AmPm -> "AM"
        | _ -> raise (ArgumentOutOfRangeException(nameof token))

    let private renderNamed (dialect: SqlAgentToolType) (format: TemporalFormatPart list) =
        format
        |> List.map (function
            | TemporalFormatPart.Literal value -> value
            | TemporalFormatPart.Token token -> renderNamedToken dialect token)
        |> String.concat String.Empty

    let private parse (value: string) (source: SqlAgentToolType) =
        match source with
        | SqlAgentToolType.Sqlite
        | SqlAgentToolType.MySQL -> parsePercent source value
        | SqlAgentToolType.MsSqlServer
        | SqlAgentToolType.Postgres
        | SqlAgentToolType.Oracle -> parseNamed source value
        | _ -> unsupportedParse source

    let private render (format: TemporalFormatPart list) (target: SqlAgentToolType) =
        match target with
        | SqlAgentToolType.Sqlite
        | SqlAgentToolType.MySQL ->
            format |> List.map (renderPercentPart target) |> String.concat String.Empty
        | SqlAgentToolType.MsSqlServer
        | SqlAgentToolType.Postgres
        | SqlAgentToolType.Oracle -> renderNamed target format
        | _ -> unsupportedRender target

    let translate (sourceFormat: string) (source: SqlAgentToolType) (target: SqlAgentToolType) =
        parse sourceFormat source |> fun format -> render format target

[<AbstractClass; Sealed>]
type internal CoreTemporalFormatNormalizer private () =
    static member internal Translate(
        sourceFormat: string,
        sourceDialect: SqlAgentToolType,
        targetProvider: SqlAgentToolType) =
        FunctionalTemporalFormatTranslation.translate sourceFormat sourceDialect targetProvider
