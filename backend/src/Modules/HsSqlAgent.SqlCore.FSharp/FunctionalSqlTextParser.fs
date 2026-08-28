namespace HsSqlAgent.SqlCore.Internal

open System
open System.Globalization
open HsSqlAgent.SqlCore.Core.Pipeline
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.SqlParsing

/// Raw SQL parser entry orchestration implemented in F#.
///
/// Tokenization, source-profile rewriting, fail-closed token guards, SQL
/// Server TOP extraction, and final ParsedStatement construction live here.
/// The detailed query/DML grammar parsers remain C# during this migration
/// slice and are invoked only after the F# entry contract is satisfied.
module internal FunctionalSqlTextParser =

    let private requireSql (sql: string | null) =
        match sql with
        | null ->
            raise (ArgumentNullException("sql"))
        | value ->
            value

    let private profileOrFail
        (sourceProfile: SqlProviderCapabilityProfile | null) =
        match Option.ofObj sourceProfile with
        | Some profile ->
            profile
        | None ->
            raise (InvalidOperationException(
                "Source capability profile validation reported a profile-specific error without a profile."))

    let private validateSourceProfile
        sourceDialect
        (sourceProfile: SqlProviderCapabilityProfile | null) =

        match SqlProviderCapabilityProfileRules.ValidationIssue(
                sourceProfile,
                sourceDialect) with
        | SqlProviderCapabilityProfileValidationIssue.None ->
            ()

        | SqlProviderCapabilityProfileValidationIssue.ProviderMismatch ->
            let profile = profileOrFail sourceProfile
            raise (ArgumentException(
                $"Source capability profile declares provider {profile.Provider}, but parser source dialect is {sourceDialect}.",
                "sourceProfile"))

        | SqlProviderCapabilityProfileValidationIssue.NegativeCompatibilityLevel ->
            let profile = profileOrFail sourceProfile
            raise (ArgumentOutOfRangeException(
                "sourceProfile",
                profile.CompatibilityLevel,
                "Provider compatibility level must be non-negative."))

        | issue ->
            raise (InvalidOperationException(
                $"Unsupported source capability profile validation issue '{issue}'."))

    let private usesMySqlAnsiQuotes
        sourceDialect
        sourceProfile =
        SqlSourceDialectGrammarRules.UsesMySqlAnsiQuotedIdentifiers(
            sourceDialect,
            sourceProfile)

    let private usesMySqlNoBackslashEscapes
        sourceDialect
        sourceProfile =
        SqlSourceDialectGrammarRules.UsesMySqlNoBackslashEscapes(
            sourceDialect,
            sourceProfile)

    let private applySourceProfileTokens
        (tokens: Token array)
        sourceDialect
        sourceProfile =

        if not (
            SqlConcatCapabilityRules.SupportsMySqlPipesAsConcat(
                sourceDialect,
                sourceProfile)) then
            tokens
        else
            tokens
            |> Array.map (fun token ->
                if token.Type = TokenType.Operator
                   && token.Value = "||" then
                    Token(
                        TokenType.Operator,
                        CoreExpressionTextParser.MySqlPipesConcatToken,
                        token.Pos,
                        Nullable<int>(token.Length))
                else
                    token)

    let private tryTypedTemporalLiteralStart
        (tokens: Token array)
        index =

        if index + 1 >= tokens.Length then
            None
        else
            let token = tokens[index]

            let temporalType =
                if CoreTokenReader.IsWord(token, "DATE") then
                    Some "DATE"
                elif CoreTokenReader.IsWord(token, "TIME") then
                    Some "TIME"
                elif CoreTokenReader.IsWord(token, "TIMESTAMP") then
                    Some "TIMESTAMP"
                else
                    None

            match temporalType with
            | None ->
                None

            | Some temporalType ->
                let next = tokens[index + 1]
                if next.Type = TokenType.String then
                    Some(temporalType, false)
                elif temporalType = "DATE"
                     || (not (CoreTokenReader.IsWord(next, "WITH"))
                         && not (CoreTokenReader.IsWord(next, "WITHOUT"))) then
                    None
                elif index + 4 >= tokens.Length
                     || not (CoreTokenReader.IsWord(tokens[index + 2], "TIME"))
                     || not (CoreTokenReader.IsWord(tokens[index + 3], "ZONE"))
                     || tokens[index + 4].Type <> TokenType.String then
                    None
                else
                    Some(temporalType, true)

    let private validateStatementTokens
        (tokens: Token array)
        sourceDialect =

        let content =
            tokens
            |> Array.filter (fun token -> token.Type <> TokenType.EOF)

        let grammar =
            SqlSourceDialectGrammarRules.For(sourceDialect)

        for index = 0 to content.Length - 1 do
            let token = content[index]

            if token.Type = TokenType.Parameter then
                raise (SqlParseException(
                    $"Unbound SQL parameter '{token.Value}' at position {token.Pos}. Runtime SQL parameters are not accepted; use a declared Custom Tool parameter."))

            if token.Type = TokenType.Operator
               && token.Value = "::"
               && not grammar.SupportsDoubleColonCast then
                raise (SqlParseException(
                    $"PostgreSQL '::' cast syntax is not valid for source dialect {sourceDialect} at position {token.Pos}; use CAST(expression AS type) for portable raw SQL."))

            if token.Type = TokenType.Keyword
               && String.Equals(
                    token.Value,
                    "LIMIT",
                    StringComparison.OrdinalIgnoreCase)
               && not grammar.SupportsLimitKeyword then
                raise (SqlParseException(
                    $"LIMIT is not valid raw source syntax for dialect {sourceDialect} at position {token.Pos}; use the source provider's native row-limiting form or a structured Core row limit."))

            if token.Type = TokenType.Keyword
               && not grammar.SupportsBareBooleanKeywords
               && (String.Equals(
                        token.Value,
                        "TRUE",
                        StringComparison.OrdinalIgnoreCase)
                   || String.Equals(
                        token.Value,
                        "FALSE",
                        StringComparison.OrdinalIgnoreCase)) then
                raise (SqlParseException(
                    $"Bare {token.Value.ToUpperInvariant()} is not valid T-SQL boolean-literal source syntax at position {token.Pos}; SQL Server bit constants use 0 or 1, and Core does not reinterpret bare TRUE/FALSE tokens as identifiers."))

            match tryTypedTemporalLiteralStart content index with
            | Some(temporalType, hasZoneQualifier)
                when not (
                    grammar.SupportsTypedTemporalLiteral(
                        temporalType,
                        hasZoneQualifier)) ->
                raise (SqlParseException(
                    $"{temporalType} typed temporal literal spelling is not valid for raw source dialect {sourceDialect} in the Core source profile at position {token.Pos}; use source-native CAST/CONVERT/function syntax or a structured Core temporal value."))

            | _ ->
                ()

            if token.Type = TokenType.Semicolon
               && index <> content.Length - 1 then
                raise (SqlParseException(
                    $"Only one SQL statement is allowed; unexpected semicolon at position {token.Pos}."))

    let private normalizeSqlServerTop
        (tokens: Token array)
        provider =

        if not (SqlSourceDialectGrammarRules.For(provider).SupportsTop) then
            None, tokens
        else
            let mutable depth = 0
            let mutable selectIndex = -1
            let mutable index = 0

            while index < tokens.Length && selectIndex < 0 do
                let token = tokens[index]

                if token.Type = TokenType.LParen then
                    depth <- depth + 1
                elif token.Type = TokenType.RParen then
                    depth <- Math.Max(0, depth - 1)
                elif depth = 0
                     && CoreTokenReader.IsWord(token, "SELECT") then
                    selectIndex <- index

                index <- index + 1

            if selectIndex < 0 then
                None, tokens
            else
                let mutable cursor = selectIndex + 1

                if cursor < tokens.Length
                   && (CoreTokenReader.IsWord(tokens[cursor], "DISTINCT")
                       || CoreTokenReader.IsWord(tokens[cursor], "ALL")) then
                    cursor <- cursor + 1

                if cursor >= tokens.Length
                   || not (CoreTokenReader.IsWord(tokens[cursor], "TOP")) then
                    None, tokens
                else
                    let topStart = cursor
                    cursor <- cursor + 1

                    let parenthesized =
                        cursor < tokens.Length
                        && tokens[cursor].Type = TokenType.LParen

                    if parenthesized then
                        cursor <- cursor + 1

                    let mutable limit = 0
                    let validLimit =
                        cursor < tokens.Length
                        && tokens[cursor].Type = TokenType.Number
                        && Int32.TryParse(
                            tokens[cursor].Value,
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            &limit)
                        && limit >= 0

                    if not validLimit then
                        raise (SqlParseException(
                            $"SQL Server TOP requires a non-negative integer row count at position {tokens[topStart].Pos}."))

                    cursor <- cursor + 1

                    if parenthesized then
                        if cursor >= tokens.Length
                           || tokens[cursor].Type <> TokenType.RParen then
                            raise (SqlParseException(
                                $"SQL Server TOP parenthesized row count is malformed at position {tokens[topStart].Pos}."))

                        cursor <- cursor + 1

                    if cursor < tokens.Length
                       && (CoreTokenReader.IsWord(tokens[cursor], "PERCENT")
                           || CoreTokenReader.IsWord(tokens[cursor], "WITH")) then
                        raise (SqlParseException(
                            $"SQL Server TOP PERCENT/WITH TIES is not represented by the Core AST at position {tokens[cursor].Pos}."))

                    let normalized =
                        Array.append
                            (tokens |> Array.take topStart)
                            (tokens |> Array.skip cursor)

                    Some limit, normalized

    let private tokenize
        sql
        sourceDialect
        sourceProfile =

        SqlTokenizer(
            sql,
            Nullable<SqlAgentToolType>(sourceDialect),
            usesMySqlAnsiQuotes sourceDialect sourceProfile,
            usesMySqlNoBackslashEscapes sourceDialect sourceProfile)
            .Tokenize()
        |> fun tokens ->
            applySourceProfileTokens
                tokens
                sourceDialect
                sourceProfile

    let private sourceServerVersion
        (sourceProfile: SqlProviderCapabilityProfile | null) =
        match Option.ofObj sourceProfile with
        | Some profile ->
            profile.ServerVersion
        | None ->
            null

    let parseQuery
        (sql: string | null)
        sourceDialect
        (sourceProfile: SqlProviderCapabilityProfile | null)
        : ParsedStatement =

        let sql = requireSql sql
        validateSourceProfile sourceDialect sourceProfile

        let tokens =
            tokenize
                sql
                sourceDialect
                sourceProfile

        validateStatementTokens tokens sourceDialect

        let topLimit, normalizedTokens =
            normalizeSqlServerTop tokens sourceDialect

        let normalizedTokens =
            CommaFromNormalizer.Normalize(normalizedTokens)

        let nullableTop =
            match topLimit with
            | Some value -> Nullable<int>(value)
            | None -> Nullable<int>()

        let statement =
            CoreQueryTextParser(
                CoreTokenReader(normalizedTokens),
                sourceDialect,
                usesMySqlNoBackslashEscapes
                    sourceDialect
                    sourceProfile)
                .ParseComplete(nullableTop)

        ParsedStatement(
            statement,
            sourceDialect,
            true,
            sourceProfile)

    let parseDml
        (sql: string | null)
        sourceDialect
        (sourceProfile: SqlProviderCapabilityProfile | null)
        : ParsedStatement =

        let sql = requireSql sql
        validateSourceProfile sourceDialect sourceProfile

        let tokens =
            tokenize
                sql
                sourceDialect
                sourceProfile

        validateStatementTokens tokens sourceDialect

        let struct (conflictTokens, conflict) =
            CoreDmlConflictTextParser.Extract(
                tokens,
                sourceDialect,
                sourceServerVersion sourceProfile)

        let statement =
            CoreDmlTextParser(
                CoreTokenReader(conflictTokens),
                sourceDialect,
                usesMySqlNoBackslashEscapes
                    sourceDialect
                    sourceProfile,
                sourceServerVersion sourceProfile)
                .ParseComplete()

        let statement =
            CoreParserAstClone.AttachInsertConflict(
                statement,
                conflict)

        ParsedStatement(
            statement,
            sourceDialect,
            true,
            sourceProfile)
