namespace HsSqlAgent.SqlCore.Rewrite

open System
open System.Globalization

module internal RewriteLexer =

    type TokenKind =
        | Identifier of string * bool
        | StringLiteral of string
        | IntegerLiteral of int64
        | DecimalLiteral of decimal
        | Keyword of string
        | Symbol of char
        | Operator of string
        | End

    type Token =
        { Kind: TokenKind
          Start: int
          Length: int }

    let private keywords =
        set [ "SELECT"; "DISTINCT"; "FROM"; "WHERE"; "AS"; "AND"; "OR"; "NOT"; "NULL"; "TRUE"; "FALSE"
              "GROUP"; "BY"; "HAVING"; "ORDER"; "ASC"; "DESC"; "LIMIT"; "OFFSET"; "INNER"; "LEFT"; "RIGHT"
              "FULL"; "CROSS"; "JOIN"; "ON"; "LIKE"; "ILIKE"; "ESCAPE"; "IS"; "IN"; "BETWEEN"; "INSERT"; "INTO"
              "VALUES"; "UPDATE"; "SET"; "DELETE"; "RETURNING"; "DEFAULT"; "UNION"; "ALL"; "INTERSECT"
              "EXCEPT"; "NULLS"; "FIRST"; "LAST"; "ROWS"; "ROW"; "FETCH"; "NEXT"; "ONLY"; "TOP"; "WITH"
              "RECURSIVE"; "CASE"; "WHEN"; "THEN"; "ELSE"; "END"; "CAST"; "EXTRACT"; "DATE"; "TIME"; "TIMESTAMP"
              "INTERVAL"; "USING"; "CONFLICT"; "DO"; "NOTHING"; "EXCLUDED"; "MATCHING"; "DUPLICATE"; "KEY"
              "FILTER"; "OVER"; "PARTITION"; "RANGE"; "UNBOUNDED"; "PRECEDING"; "FOLLOWING"; "CURRENT"
              "SEPARATOR"; "WITHIN" ]

    let private isIdentifierStart c = Char.IsLetter(c) || c = '_'
    let private isIdentifierPart c = Char.IsLetterOrDigit(c) || c = '_' || c = '$'

    let tokenize (sql: string) =
        if isNull sql then nullArg "sql"
        let length = sql.Length
        let tokens = ResizeArray<Token>()
        let mutable i = 0

        let add kind start finish =
            tokens.Add({ Kind = kind; Start = start; Length = finish - start })

        while i < length do
            let c = sql[i]
            if Char.IsWhiteSpace(c) then
                i <- i + 1
            elif c = '-' && i + 1 < length && sql[i + 1] = '-' then
                i <- i + 2
                while i < length && sql[i] <> '\n' do i <- i + 1
            elif c = '/' && i + 1 < length && sql[i + 1] = '*' then
                let start = i
                i <- i + 2
                let mutable closed = false
                while i + 1 < length && not closed do
                    if sql[i] = '*' && sql[i + 1] = '/' then
                        i <- i + 2
                        closed <- true
                    else i <- i + 1
                if not closed then invalidArg "sql" ("Unterminated comment at offset " + string start + ".")
            elif c = '\'' then
                let start = i
                i <- i + 1
                let buffer = Text.StringBuilder()
                let mutable closed = false
                while i < length && not closed do
                    if sql[i] = '\'' then
                        if i + 1 < length && sql[i + 1] = '\'' then
                            buffer.Append('\'') |> ignore
                            i <- i + 2
                        else
                            i <- i + 1
                            closed <- true
                    else
                        buffer.Append(sql[i]) |> ignore
                        i <- i + 1
                if not closed then invalidArg "sql" ("Unterminated string literal at offset " + string start + ".")
                add (StringLiteral(buffer.ToString())) start i
            elif c = '"' then
                let start = i
                i <- i + 1
                let buffer = Text.StringBuilder()
                let mutable closed = false
                while i < length && not closed do
                    if sql[i] = '"' then
                        if i + 1 < length && sql[i + 1] = '"' then
                            buffer.Append('"') |> ignore
                            i <- i + 2
                        else
                            i <- i + 1
                            closed <- true
                    else
                        buffer.Append(sql[i]) |> ignore
                        i <- i + 1
                if not closed then invalidArg "sql" ("Unterminated quoted identifier at offset " + string start + ".")
                add (Identifier(buffer.ToString(), true)) start i
            elif Char.IsDigit(c) then
                let start = i
                let mutable hasDot = false
                while i < length && (Char.IsDigit(sql[i]) || (sql[i] = '.' && not hasDot && i + 1 < length && Char.IsDigit(sql[i + 1]))) do
                    if sql[i] = '.' then hasDot <- true
                    i <- i + 1
                let text = sql.Substring(start, i - start)
                if hasDot then add (DecimalLiteral(Decimal.Parse(text, CultureInfo.InvariantCulture))) start i
                else add (IntegerLiteral(Int64.Parse(text, CultureInfo.InvariantCulture))) start i
            elif isIdentifierStart c then
                let start = i
                i <- i + 1
                while i < length && isIdentifierPart sql[i] do i <- i + 1
                let text = sql.Substring(start, i - start)
                let upper = text.ToUpperInvariant()
                if keywords.Contains upper then add (Keyword upper) start i
                else add (Identifier(text, false)) start i
            elif i + 1 < length then
                let pair = sql.Substring(i, 2)
                match pair with
                | "<>" | "!=" | ">=" | "<=" | "||" | "::" ->
                    add (Operator pair) i (i + 2)
                    i <- i + 2
                | _ ->
                    match c with
                    | '+' | '-' | '*' | '/' | '%' | '=' | '>' | '<' -> add (Operator(string c)) i (i + 1)
                    | '(' | ')' | ',' | '.' | ';' -> add (Symbol c) i (i + 1)
                    | _ -> invalidArg "sql" ("Unexpected character '" + string c + "' at offset " + string i + ".")
                    i <- i + 1
            else
                match c with
                | '+' | '-' | '*' | '/' | '%' | '=' | '>' | '<' -> add (Operator(string c)) i (i + 1)
                | '(' | ')' | ',' | '.' | ';' -> add (Symbol c) i (i + 1)
                | _ -> invalidArg "sql" ("Unexpected character '" + string c + "' at offset " + string i + ".")
                i <- i + 1

        tokens.Add({ Kind = End; Start = length; Length = 0 })
        tokens |> Seq.toList
