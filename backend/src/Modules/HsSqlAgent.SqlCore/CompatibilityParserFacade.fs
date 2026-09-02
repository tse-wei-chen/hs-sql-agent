
namespace HsSqlAgent.SqlCore.SqlParsing

open System
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Core.Pipeline
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.Rewrite

/// Temporary CLR-compatible parser surface backed entirely by the F# lexer/parser.
/// The returned AST is a projection for callers that still inspect shape. Compatibility compilation
/// consumes the current ParsedStatement.Statement; RawSql is retained only to revalidate declared
/// source-dialect syntax when EnforceSourceDialectSyntax is enabled.
[<AbstractClass; Sealed>]
type CoreSqlTextParser private () =

    static member private ValidateSourceProfile(
        sourceDialect: SqlAgentToolType,
        sourceProfile: SqlProviderCapabilityProfile | null) =
        match SqlProviderCapabilityProfileRules.ValidationIssue(sourceProfile, sourceDialect) with
        | SqlProviderCapabilityProfileValidationIssue.None -> ()
        | SqlProviderCapabilityProfileValidationIssue.ProviderMismatch ->
            raise (ArgumentException(
                "Source capability profile declares provider "
                + string sourceProfile.Provider
                + ", but parser source dialect is "
                + string sourceDialect
                + ".",
                "sourceProfile"))
        | SqlProviderCapabilityProfileValidationIssue.NegativeCompatibilityLevel ->
            raise (ArgumentOutOfRangeException(
                "sourceProfile",
                sourceProfile.CompatibilityLevel,
                "Provider compatibility level must be non-negative."))
        | value ->
            raise (InvalidOperationException(
                "Unsupported source capability profile validation issue '" + string value + "'."))

    static member private Parse(
        sql: string,
        sourceDialect: SqlAgentToolType,
        sourceProfile: SqlProviderCapabilityProfile | null,
        expectQuery: bool) =
        ArgumentNullException.ThrowIfNull(sql)
        CoreSqlTextParser.ValidateSourceProfile(sourceDialect, sourceProfile)
        try
            let parsed =
                RewriteFacadeAdapter.parseSourceValidated
                    sql
                    sourceDialect
                    sourceProfile
            let kind = RewriteCompatibilityAstAdapter.kind parsed
            if expectQuery && kind <> SqlStatementKind.Query then
                raise (SqlParseException("ParseQuery requires a SELECT statement."))
            if not expectQuery && kind = SqlStatementKind.Query then
                raise (SqlParseException("ParseDml requires an INSERT, UPDATE, or DELETE statement."))
            let result =
                ParsedStatement(
                    RewriteCompatibilityAstAdapter.toStatement parsed,
                    sourceDialect,
                    true,
                    sourceProfile)
            result.RawSql <- sql
            result
        with
        | :? SqlParseException -> reraise()
        | :? ArgumentException as ex when String.Equals(ex.ParamName, "sql", StringComparison.Ordinal) ->
            raise (SqlParseException(ex.Message, ex))

    static member ParseQuery(
        sql: string,
        sourceDialect: SqlAgentToolType) =
        CoreSqlTextParser.Parse(sql, sourceDialect, null, true)

    static member ParseQuery(
        sql: string,
        sourceDialect: SqlAgentToolType,
        sourceProfile: SqlProviderCapabilityProfile | null) =
        CoreSqlTextParser.Parse(sql, sourceDialect, sourceProfile, true)

    static member ParseDml(
        sql: string,
        sourceDialect: SqlAgentToolType) =
        CoreSqlTextParser.Parse(sql, sourceDialect, null, false)

    static member ParseDml(
        sql: string,
        sourceDialect: SqlAgentToolType,
        sourceProfile: SqlProviderCapabilityProfile | null) =
        CoreSqlTextParser.Parse(sql, sourceDialect, sourceProfile, false)
