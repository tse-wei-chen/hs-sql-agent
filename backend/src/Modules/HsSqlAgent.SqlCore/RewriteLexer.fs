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
          HashPrefixedIdentifier: bool
          ScriptMySqlAccountSeparator: bool
          ScriptDeferUnclosedGrouping: bool }

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
              HashPrefixedIdentifier = false
              ScriptMySqlAccountSeparator = false
              ScriptDeferUnclosedGrouping = false }

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
              "FULL"; "OUTER"; "CROSS"; "NATURAL"; "JOIN"; "LATERAL"; "APPLY"; "ON"; "LIKE"; "ILIKE"; "ESCAPE"; "IS"; "IN"; "BETWEEN"; "EXISTS"; "INSERT"; "INTO"
              "VALUES"; "UPDATE"; "SET"; "DELETE"; "MERGE"; "MATCHED"; "RETURNING"; "OUTPUT"; "DEFAULT"; "UNION"; "ALL"; "INTERSECT"
              "EXCEPT"; "NULLS"; "FIRST"; "LAST"; "ROWS"; "ROW"; "FETCH"; "NEXT"; "ONLY"; "TOP"; "WITH"
              "RECURSIVE"; "CASE"; "WHEN"; "THEN"; "ELSE"; "END"; "CAST"; "EXTRACT"; "DATE"; "TIME"; "TIMESTAMP"; "WITHOUT"
              "INTERVAL"; "USING"; "CONFLICT"; "DO"; "NOTHING"; "EXCLUDED"; "MATCHING"; "DUPLICATE"; "KEY"
              "FILTER"; "OVER"; "PARTITION"; "RANGE"; "UNBOUNDED"; "PRECEDING"; "FOLLOWING"; "CURRENT"
              "SEPARATOR"; "WITHIN"; "TIES"; "PERCENT"; "ZONE"; "CURRENT_DATE"; "CURRENT_TIME"; "CURRENT_TIMESTAMP" ]

    let private isIdentifierStart c = Char.IsLetter(c) || c = '_'
    let private isIdentifierPart c = Char.IsLetterOrDigit(c) || c = '_' || c = '$'

    // Conservative hard input budgets: guard memory/lexical work before parsing,
    // regardless of dialect or optional schema validation. Deep AST recursion
    // remains a separate resource gate.
    [<Literal>]
    let MaxSqlSourceLength = 4194304 // UTF-16 code units (4 Mi characters)

    [<Literal>]
    let MaxTokens = 131072 // non-End lexical tokens

    // A generous structural safety ceiling, deliberately 4x the 2,048-deep
    // six-provider CI corpus. This is NOT an arbitrary per-expression AST
    // limit: unary/Boolean iteration is independent of parenthesis depth.
    // It bounds pathological recursive Query/Source grammar shapes that
    // still need heap/CPU work after their syntax has become stack-safe.
    [<Literal>]
    let MaxGroupingNesting = 8192

    let tokenizeWith semantics (sql: string) =
        if Object.ReferenceEquals(sql, null) then nullArg "sql"
        let length = sql.Length
        let tokens = ResizeArray<Token>()
        let mutable i = 0
        let mutable groupingDepth = 0
        let mutable outermostOpen = -1
        let mutable overNestingAt = -1
        let mutable chargedCharPosition = 0

        // Comments and literals have scanning work even without emitted
        // tokens. Amortize resource deadline checks every 4096 UTF-16 units.
        let chargeSourceProgress () =
            let delta = i - chargedCharPosition
            if delta >= 4096 then
                RewriteResourceBudget.charge (int64 delta)
                chargedCharPosition <- i

        let lexicalError code message start spanLength : 'T =
            let spanLength = max 1 spanLength
            let detail = message + " Position " + string start + "."
            raise (
                SqlParseException(
                    detail,
                    SqlDiagnostic(
                        code,
                        SqlDiagnosticStage.Lexical,
                        SqlDiagnosticCategory.Syntax,
                        detail,
                        SqlDiagnosticSpan(start, spanLength))))

        if length > MaxSqlSourceLength then
            lexicalError
                "SQL_LEXICAL_INPUT_LIMIT"
                ("SQL text exceeds the maximum " + string MaxSqlSourceLength
                 + " UTF-16 code units.")
                MaxSqlSourceLength 1

        // Distinguish resource exhaustion from malformed syntax. A
        // balanced but oversized query reports Resource with the first
        // over-limit opening delimiter's source span.
        let nestingResourceError offset : 'T =
            let detail =
                "SQL grouping nesting exceeds the maximum "
                + string MaxGroupingNesting
                + " parenthesis levels. Position " + string offset + "."
            raise (
                SqlParseException(
                    detail,
                    SqlDiagnostic(
                        "SQL_LEXICAL_NESTING_LIMIT",
                        SqlDiagnosticStage.Lexical,
                        SqlDiagnosticCategory.Resource,
                        detail,
                        SqlDiagnosticSpan(offset, 1))))

        let add kind start finish =
            RewriteResourceBudget.checkpoint ()
            if tokens.Count >= MaxTokens then
                lexicalError
                    "SQL_LEXICAL_TOKEN_LIMIT"
                    ("SQL text exceeds the maximum " + string MaxTokens + " lexical tokens.")
                    start (finish - start)

            // Preserve the original unmatched-parentheses diagnostic
            // precedence. Depth is checked after complete lexical balance,
            // while overNestingAt records the first excessive open token.
            match kind with
            | Symbol '(' ->
                if groupingDepth = 0 then outermostOpen <- start
                groupingDepth <- groupingDepth + 1
                if groupingDepth > MaxGroupingNesting && overNestingAt < 0 then
                    overNestingAt <- start
            | Symbol ')' ->
                if groupingDepth = 0 then
                    // Script dispatch defers this error to the strictly lexed
                    // statement, so a later physical SQL Server GO batch is
                    // not hidden by the malformed earlier batch.
                    if not semantics.ScriptDeferUnclosedGrouping then
                        lexicalError
                            "SQL_LEXICAL_UNBALANCED_PARENTHESIS"
                            "Unexpected closing parenthesis without a matching opening parenthesis."
                            start (finish - start)
                else
                    groupingDepth <- groupingDepth - 1
            | _ -> ()

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

        // A successful CLR Decimal.TryParse is NOT proof that the SQL number
        // survived unchanged: it can round significant digits or turn a
        // nonzero 1e-29 (or 1e-1000) into zero. Compare normalized exact
        // decimal values, not their textual spelling. Mantissa trailing zeros
        // and exponent spellings are mathematically equivalent.
        //
        // This scan is linear in the already bounded SQL token length; very
        // long exponents saturate at +/-1e9 (far outside Decimal's finite
        // representable scale) instead of overflowing a CLR integer.
        let normalizedNumeric (literal: string) =
            // Charge the entire canonical pass before allocating its digit
            // buffer; this work is separate from lexical source scanning.
            RewriteResourceBudget.charge (int64 literal.Length)
            let exponentAt = literal.IndexOfAny([| 'e'; 'E' |])
            let coefficientEnd =
                if exponentAt < 0 then literal.Length else exponentAt
            let digits = Text.StringBuilder(coefficientEnd)
            let mutable inFraction = false
            let mutable fractionalDigits = 0L
            for position = 0 to coefficientEnd - 1 do
                if (position &&& 4095) = 0 then RewriteResourceBudget.checkpoint ()
                let c = literal.[position]
                if c = '.' then
                    inFraction <- true
                elif c >= '0' && c <= '9' then
                    digits.Append(c) |> ignore
                    if inFraction then fractionalDigits <- fractionalDigits + 1L

            let mutable first = 0
            while first < digits.Length && digits.[first] = '0' do
                if (first &&& 4095) = 0 then RewriteResourceBudget.checkpoint ()
                first <- first + 1
            if first = digits.Length then
                "0", 0L
            else
                let mutable last = digits.Length
                while last > first && digits.[last - 1] = '0' do
                    if (last &&& 4095) = 0 then RewriteResourceBudget.checkpoint ()
                    last <- last - 1

                let mutable power = 0L
                if exponentAt >= 0 then
                    let mutable position = exponentAt + 1
                    let negative =
                        if position < literal.Length && literal.[position] = '-' then
                            position <- position + 1
                            true
                        else
                            if position < literal.Length && literal.[position] = '+' then
                                position <- position + 1
                            false
                    while position < literal.Length do
                        if (position &&& 4095) = 0 then RewriteResourceBudget.checkpoint ()
                        power <-
                            min 1000000000L
                                (power * 10L + int64 (int literal.[position] - int '0'))
                        position <- position + 1
                    if negative then power <- -power

                digits.ToString(first, last - first),
                power - fractionalDigits + int64 (digits.Length - last)

        // SQL numeric magnitudes are bounded by the canonical DecimalLiteral
        // model. Never leak CLR overflow or silently round/underflow
        // representable SQL syntax into a DIFFERENT numeric value.
        let decimalLiteral (text: string) start finish =
            // Account for the CLR parse pass separately from exact decimal
            // normalization, before entering an expensive long-token parse.
            RewriteResourceBudget.charge (int64 text.Length)
            match Decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture) with
            | true, value ->
                // G29 round-trips the entire CLR Decimal value with invariant
                // formatting; canonical comparison removes only insignificant
                // decimal zeroes, preserving arbitrary source exponent form.
                let rendered = value.ToString("G29", CultureInfo.InvariantCulture)
                if normalizedNumeric text <> normalizedNumeric rendered then
                    lexicalError
                        "SQL_LEXICAL_NUMERIC_PRECISION_LOSS"
                        "Numeric literal cannot be represented exactly by the supported Decimal model."
                        start (finish - start)
                add (DecimalLiteral value) start finish
            | false, _ ->
                lexicalError
                    "SQL_LEXICAL_NUMERIC_OUT_OF_RANGE"
                    "Numeric literal is outside the supported Decimal range."
                    start (finish - start)

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
                if (i &&& 4095) = 0 then chargeSourceProgress ()
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
            chargeSourceProgress ()
            let c = sql[i]
            if Char.IsWhiteSpace(c) then
                i <- i + 1
            elif c = '-' && i + 1 < length && sql[i + 1] = '-' && isDashDashCommentStart () then
                i <- i + 2
                while i < length && sql[i] <> '\r' && sql[i] <> '\n' do
                    if (i &&& 4095) = 0 then chargeSourceProgress ()
                    i <- i + 1
            elif c = '/' && i + 1 < length && sql[i + 1] = '*' then
                let start = i
                i <- i + 2
                let mutable closed = false
                while i + 1 < length && not closed do
                    if (i &&& 4095) = 0 then chargeSourceProgress ()
                    if sql[i] = '*' && sql[i + 1] = '/' then
                        i <- i + 2
                        closed <- true
                    else i <- i + 1
                if not closed then parseError "Unterminated block comment." start (length - start)
            elif c = '#' && semantics.HashLineComment then
                i <- i + 1
                while i < length && sql[i] <> '\r' && sql[i] <> '\n' do
                    if (i &&& 4095) = 0 then chargeSourceProgress ()
                    i <- i + 1
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
            elif c = '@' && semantics.ScriptMySqlAccountSeparator then
                // Script-only MySQL DCL account delimiter. The executable SQL
                // parser uses a stricter lexer and continues to reject @params.
                add (Symbol '@') i (i + 1)
                i <- i + 1
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
                    if (i &&& 4095) = 0 then chargeSourceProgress ()
                    hasDigits <- true
                    i <- i + 1

                if i < length && sql[i] = '.' then
                    hasDot <- true
                    i <- i + 1
                    while i < length && Char.IsDigit(sql[i]) do
                        if (i &&& 4095) = 0 then chargeSourceProgress ()
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
                        if (i &&& 4095) = 0 then chargeSourceProgress ()
                        i <- i + 1
                    if i = exponentStart then
                        parseError "Invalid numeric exponent." start (max 1 (i - start))

                if i < length && (sql[i] = '.' || isConfiguredIdentifierStart sql[i]) then
                    let invalidStart = i
                    while i < length && (sql[i] = '.' || isConfiguredIdentifierPart sql[i]) do
                        if (i &&& 4095) = 0 then chargeSourceProgress ()
                        i <- i + 1
                    parseError "Invalid numeric literal." start (max 1 (i - start))

                // Ensure single-token digit loops observe cancellation and
                // charge source progress before allocating their substring.
                chargeSourceProgress ()
                let text = sql.Substring(start, i - start)
                if not hasDot && not hasExponent then
                    match Int64.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture) with
                    | true, value -> add (IntegerLiteral value) start i
                    | _ -> decimalLiteral text start i
                else
                    decimalLiteral text start i
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

        chargeSourceProgress ()

        // Script-only SQL Server dispatch can defer unmatched opening
        // grouping to each statement's strict lexer, allowing a subsequent
        // physical GO batch to be analyzed independently. The executable
        // compiler always uses the original strict grouping validation.
        if groupingDepth <> 0 && not semantics.ScriptDeferUnclosedGrouping then
            lexicalError
                "SQL_LEXICAL_UNBALANCED_PARENTHESIS"
                "SQL text ends with an opening parenthesis that has no matching closing parenthesis."
                outermostOpen 1

        // Malformed grouping remains a Syntax error, even if the malformed
        // text also exceeded the resource ceiling (e.g. 25,000 unmatched
        // opens). Only balanced, excessively nested SQL is Resource.
        if overNestingAt >= 0 then nestingResourceError overNestingAt

        tokens.Add({ Kind = End; Start = length; Length = 0 })
        tokens |> Seq.toList


    let tokenize (sql: string) = tokenizeWith LexicalSemantics.standard sql
