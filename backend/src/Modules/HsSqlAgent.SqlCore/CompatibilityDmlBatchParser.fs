namespace HsSqlAgent.SqlCore.SqlParsing

open System
open System.Collections.Generic
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Core.Pipeline
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.Rewrite
open HsSqlAgent.SqlCore.Rewrite.RewriteLexer

/// CLR-friendly non-empty ordered DML batch. Statements are parsed independently by the existing
/// closed F# parser; the batch boundary is only responsible for lexer-aware statement separation.
[<Sealed>]
type ParsedDmlBatch private (statements: ParsedStatement array) =
    member _.Statements : IReadOnlyList<ParsedStatement> = statements
    member _.Count = statements.Length
    static member internal Create(statements: ParsedStatement array) = ParsedDmlBatch(statements)

/// Parses one or more semicolon-separated DML statements without ever splitting raw SQL text on
/// semicolons. The existing provider-aware lexer identifies only top-level statement terminators,
/// so semicolons inside literals, quoted identifiers and comments remain part of the statement.
[<AbstractClass; Sealed>]
type CoreDmlBatchTextParser private () =

    static member private ValidateSourceProfile(
        sourceDialect: SqlAgentToolType,
        sourceProfile: SqlProviderCapabilityProfile | null) =
        match sourceProfile with
        | null -> ()
        | profile ->
            match SqlProviderCapabilityProfileRules.ValidationIssue(profile, sourceDialect) with
            | SqlProviderCapabilityProfileValidationIssue.None -> ()
            | SqlProviderCapabilityProfileValidationIssue.ProviderMismatch ->
                raise (ArgumentException(
                    "Source capability profile declares provider "
                    + string profile.Provider
                    + ", but parser source dialect is "
                    + string sourceDialect
                    + ".",
                    "sourceProfile"))
            | SqlProviderCapabilityProfileValidationIssue.NegativeCompatibilityLevel ->
                raise (ArgumentOutOfRangeException(
                    "sourceProfile",
                    profile.CompatibilityLevel,
                    "Provider compatibility level must be non-negative."))
            | value ->
                raise (InvalidOperationException(
                    "Unsupported source capability profile validation issue '" + string value + "'."))

    static member private LexicalSemantics(
        sourceDialect: SqlAgentToolType,
        sourceProfile: SqlProviderCapabilityProfile | null) =
        let grammar = SqlSourceDialectGrammarRules.For(sourceDialect)
        let delimiter feature =
            if grammar.SupportsLexicalFeature(feature) then
                IdentifierDelimiterSemantics.AllowIdentifierDelimiter
            else
                IdentifierDelimiterSemantics.RejectIdentifierDelimiter
        let doubleQuote =
            if grammar.SupportsLexicalFeature(SqlSourceLexicalFeatures.DoubleQuotedIdentifierRequiresAnsiMode)
               && not (SqlSourceDialectGrammarRules.UsesMySqlAnsiQuotedIdentifiers(sourceDialect, sourceProfile)) then
                DoubleQuoteSemantics.RejectMySqlDoubleQuoteAmbiguity
            else
                DoubleQuoteSemantics.AllowDoubleQuotedIdentifier
        let backslash =
            if grammar.SupportsLexicalFeature(SqlSourceLexicalFeatures.BackslashSensitiveQuotedText)
               && not (SqlSourceDialectGrammarRules.UsesMySqlNoBackslashEscapes(sourceDialect, sourceProfile)) then
                BackslashSemantics.RejectMySqlBackslashAmbiguity
            else
                BackslashSemantics.BackslashIsLiteral
        { DoubleQuote = doubleQuote
          Backtick = delimiter SqlSourceLexicalFeatures.BacktickQuotedIdentifier
          Bracket = delimiter SqlSourceLexicalFeatures.BracketQuotedIdentifier
          Backslash = backslash
          HashLineComment = grammar.SupportsLexicalFeature(SqlSourceLexicalFeatures.HashLineComment)
          DashDashCommentRequiresSeparator =
            grammar.SupportsLexicalFeature(SqlSourceLexicalFeatures.DashDashCommentRequiresSeparator)
          PostgresEscapeString = grammar.SupportsLexicalFeature(SqlSourceLexicalFeatures.PostgresEscapeString)
          PostgresDollarQuotedString = grammar.SupportsLexicalFeature(SqlSourceLexicalFeatures.PostgresDollarQuotedString)
          OracleQuotedString = grammar.SupportsLexicalFeature(SqlSourceLexicalFeatures.OracleQuotedString)
          HashPrefixedIdentifier = grammar.SupportsLexicalFeature(SqlSourceLexicalFeatures.HashPrefixedIdentifier) }

    static member ParseDmlBatch(sql: string, sourceDialect: SqlAgentToolType) =
        CoreDmlBatchTextParser.ParseDmlBatch(sql, sourceDialect, null)

    static member ParseDmlBatch(
        sql: string,
        sourceDialect: SqlAgentToolType,
        sourceProfile: SqlProviderCapabilityProfile | null) =
        ArgumentNullException.ThrowIfNull(sql)
        if String.IsNullOrWhiteSpace(sql) then
            raise (SqlParseException("DML SQL text cannot be empty."))

        CoreDmlBatchTextParser.ValidateSourceProfile(sourceDialect, sourceProfile)
        let lexical = CoreDmlBatchTextParser.LexicalSemantics(sourceDialect, sourceProfile)
        let tokens = RewriteLexer.tokenizeWith lexical sql
        let statements = ResizeArray<ParsedStatement>()
        let mutable segmentStart = 0
        let mutable segmentTokenCount = 0

        let addSegment finish =
            let raw = sql.Substring(segmentStart, finish - segmentStart)
            if String.IsNullOrWhiteSpace(raw) then
                raise (SqlParseException("DML batch contains an empty statement."))
            let parsed = CoreSqlTextParser.ParseDml(raw.Trim(), sourceDialect, sourceProfile)
            statements.Add(parsed)

        for token in tokens do
            match token.Kind with
            | Symbol ';' ->
                if segmentTokenCount = 0 then
                    raise (SqlParseException("DML batch contains an empty statement."))
                addSegment token.Start
                segmentStart <- token.Start + token.Length
                segmentTokenCount <- 0
            | End ->
                if segmentTokenCount > 0 then
                    addSegment token.Start
            | _ ->
                segmentTokenCount <- segmentTokenCount + 1

        if statements.Count = 0 then
            raise (SqlParseException("DML batch must contain at least one INSERT, UPDATE, or DELETE statement."))

        ParsedDmlBatch.Create(statements.ToArray())
