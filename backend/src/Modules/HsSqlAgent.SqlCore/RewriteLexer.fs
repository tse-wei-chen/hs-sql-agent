namespace HsSqlAgent.SqlCore.Rewrite

open System
open System.Globalization
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.SqlParsing

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

    type DoubleQuoteSemantics =
        | AllowDoubleQuotedIdentifier
        | RejectMySqlDoubleQuoteAmbiguity

    type IdentifierDelimiterSemantics =
        | AllowIdentifierDelimiter
        | RejectIdentifierDelimiter

    type BackslashSemantics =
        | BackslashIsLiteral
        | RejectMySqlBackslashAmbiguity

    type LexicalSemantics =
        { DoubleQuote: DoubleQuoteSemantics
          Backtick: IdentifierDelimiterSemantics
          Bracket: IdentifierDelimiterSemantics
          Backslash: BackslashSemantics
          HashLineComment: bool
          DashDashCommentRequiresSeparator: bool
          PostgresEscapeString: bool
          PostgresDollarQuotedString: bool
          OracleQuotedString: bool
          HashPrefixedIdentifier: bool }

    module LexicalSemantics =
        let standard =
            { DoubleQuote = AllowDoubleQuotedIdentifier
              Backtick = RejectIdentifierDelimiter
              Bracket = RejectIdentifierDelimiter
              Backslash = BackslashIsLiteral
              HashLineComment = false
              DashDashCommentRequiresSeparator = false
              PostgresEscapeString = false
              PostgresDollarQuotedString = false
              OracleQuotedString = false
              HashPrefixedIdentifier = false }

        let mysql ansiQuotes noBackslashEscapes =
            { standard with
                DoubleQuote =
                    if ansiQuotes then AllowDoubleQuotedIdentifier
                    else RejectMySqlDoubleQuoteAmbiguity
                Backtick = AllowIdentifierDelimiter
                Backslash =
                    if noBackslashEscapes then BackslashIsLiteral
                    else RejectMySqlBackslashAmbiguity
                HashLineComment = true
                DashDashCommentRequiresSeparator = true }

        let sqlServer =
            { standard with
                Bracket = AllowIdentifierDelimiter
                HashPrefixedIdentifier = true }

        let sqlite =
            { standard with
                Backtick = AllowIdentifierDelimiter
                Bracket = AllowIdentifierDelimiter }

    let private keywords =
        set [ "SELECT"; "DISTINCT"; "FROM"; "WHERE"; "AS"; "AND"; "OR"; "NOT"; "NULL"; "TRUE"; "FALSE"
              "GROUP"; "BY"; "HAVING"; "ORDER"; "ASC"; "DESC"; "LIMIT"; "OFFSET"; "INNER"; "LEFT"; "RIGHT"
              "FULL"; "OUTER"; "CROSS"; "NATURAL"; "JOIN"; "LATERAL"; "ON"; "LIKE"; "ILIKE"; "ESCAPE"; "IS"; "IN"; "BETWEEN"; "EXISTS"; "INSERT"; "INTO"
              "VALUES"; "UPDATE"; "SET"; "DELETE"; "RETURNING"; "OUTPUT"; "DEFAULT"; "UNION"; "ALL"; "INTERSECT"
              "EXCEPT"; "NULLS"; "FIRST"; "LAST"; "ROWS"; "ROW"; "FETCH"; "NEXT"; "ONLY"; "TOP"; "WITH"
              "RECURSIVE"; "CASE"; "WHEN"; "THEN"; "ELSE"; "END"; "CAST"; "EXTRACT"; "DATE"; "TIME"; "TIMESTAMP"; "WITHOUT"
              "INTERVAL"; "USING"; "CONFLICT"; "DO"; "NOTHING"; "EXCLUDED"; "MATCHING"; "DUPLICATE"; "KEY"
              "FILTER"; "OVER"; "PARTITION"; "RANGE"; "UNBOUNDED"; "PRECEDING"; "FOLLOWING"; "CURRENT"
              "SEPARATOR"; "WITHIN"; "TIES"; "PERCENT"; "ZONE"; "CURRENT_DATE"; "CURRENT_TIME"; "CURRENT_TIMESTAMP" ]

    let private isIdentifierStart c = Char.IsLetter(c) || c = '_'
    let private isIdentifierPart c = Char.IsLetterOrDigit(c) || c = '_' || c = '$'

    let tokenizeWith semantics (sql: string) =
        if Object.ReferenceEquals(sql, null) then nullArg "sql"
        let length = sql.Length
        let tokens = ResizeArray<Token>()
        let mutable i = 0

        let add kind start finish =
            tokens.Add({ Kind = kind; Start = start; Length = finish - start })

        let parseError message start spanLength : 'T =
            let actualLength = max spanLength 1
            let finish = start + actualLength
            let detail =
                message
                + " Position "
                + string start
                + ", span ["
                + string start
                + ".."
                + string finish
                + ")."
            raise (
                SqlParseException(
                    detail,
                    SqlDiagnostic(
                        "SQL_LEXICAL_ERROR",
                        SqlDiagnosticStage.Lexical,
                        SqlDiagnosticCategory.Syntax,
                        detail,
                        SqlDiagnosticSpan(start, actualLength))))

        let rejectBackslashIfAmbiguous kind start current =
            match semantics.Backslash with
            | BackslashIsLiteral -> ()
            | RejectMySqlBackslashAmbiguity ->
                let message =
                    match kind with
                    | StringLiteral _ ->
                        "MySQL backslash escape semantics depend on NO_BACKSLASH_ESCAPES sql_mode; Core rejects single-quoted strings containing backslashes unless the source profile explicitly declares NO_BACKSLASH_ESCAPES."
                    | Identifier _ ->
                        "MySQL backslash escape semantics inside quoted identifiers depend on NO_BACKSLASH_ESCAPES sql_mode; Core rejects this identifier unless the source profile explicitly declares NO_BACKSLASH_ESCAPES."
                    | _ -> invalidOp "Backslash ambiguity guard requires quoted text."
                parseError message start (current - start + 1)

        let isConfiguredIdentifierStart c =
            isIdentifierStart c || (c = '#' && semantics.HashPrefixedIdentifier)

        let isConfiguredIdentifierPart c =
            isIdentifierPart c || (c = '#' && semantics.HashPrefixedIdentifier)

        let isDashDashCommentStart () =
            if not semantics.DashDashCommentRequiresSeparator then true
            elif i + 2 >= length then true
            else Char.IsWhiteSpace(sql[i + 2]) || Char.IsControl(sql[i + 2])

        let isValidDollarTag (tag: string) =
            tag.Length = 0
            || (isIdentifierStart tag[0]
                && (tag |> Seq.skip 1 |> Seq.forall isIdentifierPart))

        let tryDollarDelimiter start =
            if sql[start] <> '$' then None
            else
                let tagEnd = sql.IndexOf('$', start + 1)
                if tagEnd < 0 then None
                else
                    let tag = sql.Substring(start + 1, tagEnd - start - 1)
                    if isValidDollarTag tag then
                        Some(sql.Substring(start, tagEnd - start + 1), tagEnd)
                    else None

        let readStandardString start quoteStart description =
            i <- quoteStart + 1
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
                    if sql[i] = '\\' then
                        rejectBackslashIfAmbiguous (StringLiteral String.Empty) start i
                    buffer.Append(sql[i]) |> ignore
                    i <- i + 1
            if not closed then parseError ("Unterminated " + description + ".") start (length - start)
            add (StringLiteral(buffer.ToString())) start i

        while i < length do
            let c = sql[i]
            if Char.IsWhiteSpace(c) then
                i <- i + 1
            elif c = '-' && i + 1 < length && sql[i + 1] = '-' && isDashDashCommentStart () then
                i <- i + 2
                while i < length && sql[i] <> '\r' && sql[i] <> '\n' do i <- i + 1
            elif c = '/' && i + 1 < length && sql[i + 1] = '*' then
                let start = i
                i <- i + 2
                let mutable closed = false
                while i + 1 < length && not closed do
                    if sql[i] = '*' && sql[i + 1] = '/' then
                        i <- i + 2
                        closed <- true
                    else i <- i + 1
                if not closed then parseError "Unterminated block comment." start (length - start)
            elif c = '#' && semantics.HashLineComment then
                i <- i + 1
                while i < length && sql[i] <> '\r' && sql[i] <> '\n' do i <- i + 1
            elif (c = 'q' || c = 'Q') && i + 2 < length && sql[i + 1] = '\'' then
                let start = i
                if not semantics.OracleQuotedString then
                    parseError "Oracle q-quoted string is not valid for the configured provider." start 2
                let opening = sql[i + 2]
                let closing =
                    match opening with
                    | '[' -> ']'
                    | '{' -> '}'
                    | '(' -> ')'
                    | '<' -> '>'
                    | value -> value
                i <- i + 3
                let buffer = Text.StringBuilder()
                let mutable closed = false
                while i + 1 < length && not closed do
                    if sql[i] = closing && sql[i + 1] = '\'' then
                        i <- i + 2
                        closed <- true
                    else
                        buffer.Append(sql[i]) |> ignore
                        i <- i + 1
                if not closed then parseError "Unterminated Oracle q-quoted string." start (length - start)
                add (StringLiteral(buffer.ToString())) start i
            elif (c = 'N' || c = 'n') && i + 1 < length && sql[i + 1] = '\'' then
                let start = i
                readStandardString start (i + 1) "national string literal"
            elif (c = 'E' || c = 'e') && i + 1 < length && sql[i + 1] = '\'' then
                let start = i
                if not semantics.PostgresEscapeString then
                    parseError "PostgreSQL E-string is not valid for the configured provider." start 2
                i <- i + 2
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
                    elif sql[i] = '\\' then
                        let escapeStart = i
                        i <- i + 1
                        if i >= length then
                            parseError "Unterminated PostgreSQL E-string." start (length - start)
                        let decoded =
                            match sql[i] with
                            | '\\' -> '\\'
                            | '\'' -> '\''
                            | 'n' -> '\n'
                            | 'r' -> '\r'
                            | 't' -> '\t'
                            | 'b' -> '\b'
                            | 'f' -> '\f'
                            | unsupported ->
                                parseError
                                    ("Unsupported PostgreSQL E-string escape '\\" + string unsupported + "'.")
                                    escapeStart
                                    2
                        buffer.Append(decoded) |> ignore
                        i <- i + 1
                    else
                        buffer.Append(sql[i]) |> ignore
                        i <- i + 1
                if not closed then parseError "Unterminated PostgreSQL E-string." start (length - start)
                add (StringLiteral(buffer.ToString())) start i
            elif (c = 'X' || c = 'x' || c = 'B' || c = 'b') && i + 1 < length && sql[i + 1] = '\'' then
                parseError "Typed hex/bit literals are not yet represented by the AST." i 2
            elif c = '$' && Option.isSome (tryDollarDelimiter i) then
                let start = i
                if not semantics.PostgresDollarQuotedString then
                    parseError "PostgreSQL dollar-quoted string is not valid for the configured provider." start 1
                let delimiter, tagEnd = Option.get (tryDollarDelimiter i)
                let contentStart = tagEnd + 1
                let close = sql.IndexOf(delimiter, contentStart, StringComparison.Ordinal)
                if close < 0 then
                    parseError "Unterminated PostgreSQL dollar-quoted string." start (length - start)
                add (StringLiteral(sql.Substring(contentStart, close - contentStart))) start (close + delimiter.Length)
                i <- close + delimiter.Length
            elif c = '\'' then
                let start = i
                readStandardString start i "string literal"
            elif c = '"' then
                let start = i
                match semantics.DoubleQuote with
                | AllowDoubleQuotedIdentifier -> ()
                | RejectMySqlDoubleQuoteAmbiguity ->
                    parseError
                        "MySQL double-quote semantics depend on ANSI_QUOTES sql_mode; Core rejects this delimiter unless the source profile explicitly declares ANSI_QUOTES or ANSI."
                        start
                        1
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
                        if sql[i] = '\\' then
                            rejectBackslashIfAmbiguous (Identifier(String.Empty, true)) start i
                        buffer.Append(sql[i]) |> ignore
                        i <- i + 1
                if not closed then parseError "Unterminated quoted identifier." start (length - start)
                add (Identifier(buffer.ToString(), true)) start i
            elif c = '`' then
                let start = i
                match semantics.Backtick with
                | AllowIdentifierDelimiter -> ()
                | RejectIdentifierDelimiter ->
                    parseError "Backtick-quoted identifiers are not valid for the configured provider." start 1
                i <- i + 1
                let buffer = Text.StringBuilder()
                let mutable closed = false
                while i < length && not closed do
                    if sql[i] = '`' then
                        if i + 1 < length && sql[i + 1] = '`' then
                            buffer.Append('`') |> ignore
                            i <- i + 2
                        else
                            i <- i + 1
                            closed <- true
                    else
                        if sql[i] = '\\' then
                            rejectBackslashIfAmbiguous (Identifier(String.Empty, true)) start i
                        buffer.Append(sql[i]) |> ignore
                        i <- i + 1
                if not closed then parseError "Unterminated quoted identifier." start (length - start)
                add (Identifier(buffer.ToString(), true)) start i
            elif c = '[' then
                let start = i
                match semantics.Bracket with
                | AllowIdentifierDelimiter -> ()
                | RejectIdentifierDelimiter ->
                    parseError "Bracket-quoted identifiers are not valid for the configured provider." start 1
                i <- i + 1
                let buffer = Text.StringBuilder()
                let mutable closed = false
                while i < length && not closed do
                    if sql[i] = ']' then
                        if i + 1 < length && sql[i + 1] = ']' then
                            buffer.Append(']') |> ignore
                            i <- i + 2
                        else
                            i <- i + 1
                            closed <- true
                    else
                        buffer.Append(sql[i]) |> ignore
                        i <- i + 1
                if not closed then parseError "Unterminated quoted identifier." start (length - start)
                add (Identifier(buffer.ToString(), true)) start i
            elif i + 1 < length && c = '{' && sql[i + 1] = '{' then
                let start = i
                let close = sql.IndexOf("}}", i + 2, StringComparison.Ordinal)
                if close < 0 then
                    parseError "Unterminated template parameter." start (length - start)
                let name = sql.Substring(i + 2, close - i - 2).Trim()
                let validName =
                    name.Length > 0
                    && isIdentifierStart name[0]
                    && (name |> Seq.skip 1 |> Seq.forall isIdentifierPart)
                if not validName then
                    parseError "Invalid template parameter name." start (close + 2 - start)
                i <- close + 2
                let parameterText = sql.Substring(start, i - start)
                parseError ("Unbound SQL parameter '" + parameterText + "'.") start (max 1 (i - start))
            elif c = '?' then
                let start = i
                i <- i + 1
                parseError "Unbound SQL parameter '?'." start 1
            elif c = ':' && not (i + 1 < length && sql[i + 1] = ':') then
                let start = i
                i <- i + 1
                if i >= length || not (isIdentifierStart sql[i]) then
                    parseError "Invalid parameter beginning with ':'." start 1
                while i < length && isIdentifierPart sql[i] do i <- i + 1
                let parameterText = sql.Substring(start, i - start)
                parseError ("Unbound SQL parameter '" + parameterText + "'.") start (max 1 (i - start))
            elif c = '@' then
                let start = i
                i <- i + 1
                if i >= length || not (isIdentifierStart sql[i]) then
                    parseError "Invalid parameter beginning with '@'." start 1
                while i < length && isIdentifierPart sql[i] do i <- i + 1
                let parameterText = sql.Substring(start, i - start)
                parseError ("Unbound SQL parameter '" + parameterText + "'.") start (max 1 (i - start))
            elif c = char 36 then
                let start = i
                i <- i + 1
                if i >= length || not (Char.IsDigit(sql[i])) then
                    parseError "Invalid positional parameter. Expected dollar sign followed by digits." start 1
                while i < length && Char.IsDigit(sql[i]) do i <- i + 1
                let parameterText = sql.Substring(start, i - start)
                parseError ("Unbound SQL parameter '" + parameterText + "'.") start (max 1 (i - start))
            elif Char.IsDigit(c) || (c = '.' && i + 1 < length && Char.IsDigit(sql[i + 1])) then
                let start = i
                let mutable hasDigits = false
                let mutable hasDot = false
                let mutable hasExponent = false

                while i < length && Char.IsDigit(sql[i]) do
                    hasDigits <- true
                    i <- i + 1

                if i < length && sql[i] = '.' then
                    hasDot <- true
                    i <- i + 1
                    while i < length && Char.IsDigit(sql[i]) do
                        hasDigits <- true
                        i <- i + 1

                if not hasDigits then
                    parseError "Invalid numeric literal." start (max 1 (i - start))

                if i < length && (sql[i] = 'e' || sql[i] = 'E') then
                    hasExponent <- true
                    i <- i + 1
                    if i < length && (sql[i] = '+' || sql[i] = '-') then
                        i <- i + 1
                    let exponentStart = i
                    while i < length && Char.IsDigit(sql[i]) do
                        i <- i + 1
                    if i = exponentStart then
                        parseError "Invalid numeric exponent." start (max 1 (i - start))

                if i < length && (sql[i] = '.' || isConfiguredIdentifierStart sql[i]) then
                    let invalidStart = i
                    while i < length && (sql[i] = '.' || isConfiguredIdentifierPart sql[i]) do
                        i <- i + 1
                    parseError "Invalid numeric literal." start (max 1 (i - start))

                let text = sql.Substring(start, i - start)
                if not hasDot && not hasExponent then
                    match Int64.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture) with
                    | true, value -> add (IntegerLiteral value) start i
                    | _ -> add (DecimalLiteral(Decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture))) start i
                else
                    add (DecimalLiteral(Decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture))) start i
            elif isConfiguredIdentifierStart c then
                let start = i
                i <- i + 1
                while i < length && isConfiguredIdentifierPart sql[i] do i <- i + 1
                let text = sql.Substring(start, i - start)
                let upper = text.ToUpperInvariant()
                if keywords.Contains upper then add (Keyword upper) start i
                else add (Identifier(text, false)) start i
            elif i + 2 < length then
                let triple = sql.Substring(i, 3)
                match triple with
                | "<=>"
                | "->>" ->
                    add (Operator triple) i (i + 3)
                    i <- i + 3
                | _ ->
                    let pair = sql.Substring(i, 2)
                    match pair with
                    | "<>" | "!=" | ">=" | "<=" | "||" | "::" | "!~" | "->" ->
                        add (Operator pair) i (i + 2)
                        i <- i + 2
                    | _ ->
                        match c with
                        | '+' | '-' | '*' | '/' | '%' | '=' | '>' | '<' | '~' -> add (Operator(string c)) i (i + 1)
                        | '(' | ')' | ',' | '.' | ';' -> add (Symbol c) i (i + 1)
                        | _ -> parseError ("Unexpected character '" + string c + "'.") i 1
                        i <- i + 1
            elif i + 1 < length then
                let pair = sql.Substring(i, 2)
                match pair with
                | "<>" | "!=" | ">=" | "<=" | "||" | "::" | "!~" | "->" ->
                    add (Operator pair) i (i + 2)
                    i <- i + 2
                | _ ->
                    match c with
                    | '+' | '-' | '*' | '/' | '%' | '=' | '>' | '<' | '~' -> add (Operator(string c)) i (i + 1)
                    | '(' | ')' | ',' | '.' | ';' -> add (Symbol c) i (i + 1)
                    | _ -> parseError ("Unexpected character '" + string c + "'.") i 1
                    i <- i + 1
            else
                match c with
                | '+' | '-' | '*' | '/' | '%' | '=' | '>' | '<' | '~' -> add (Operator(string c)) i (i + 1)
                | '(' | ')' | ',' | '.' | ';' -> add (Symbol c) i (i + 1)
                | _ -> parseError ("Unexpected character '" + string c + "'.") i 1
                i <- i + 1

        tokens.Add({ Kind = End; Start = length; Length = 0 })
        tokens |> Seq.toList


    let tokenize (sql: string) = tokenizeWith LexicalSemantics.standard sql
