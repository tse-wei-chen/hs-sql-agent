namespace HsSqlAgent.SqlCore.Internal

open System
open System.Collections.Immutable
open HsSqlAgent.SqlCore.Core.Ast
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Core.Normalization
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.SqlTranslation.Functions

/// F# ownership boundary for provider-registry function translation.
///
/// This module preserves the legacy registry-driven semantic mapping and its cross-dialect
/// rejection rules without delegating to CoreSqlNormalizer.
module internal FunctionalProviderFunctionNormalizer =

    let private functionRegistry =
        lazy (FunctionRegistry(FunctionDefinitionLoader.LoadEmbedded()) :> IFunctionRegistry)

    let private identifier (name: string) =
        SqlIdentifier(
            ImmutableArray.Create(IdentifierPart(name, false, SourceSpan.Unknown)),
            SourceSpan.Unknown)

    let private renameFunction
        (original: FunctionCallExpr)
        (arguments: ImmutableArray<SqlExpr>)
        (name: string) =

        let renamed =
            CoreBindingAstClone.FunctionName(
                original,
                identifier (name.Trim().ToUpperInvariant()))

        CoreBindingAstClone.Function(
            renamed,
            arguments,
            original.AggregateOrderBy)
        :> SqlExpr

    let private normalizeSqlServerLen
        (original: FunctionCallExpr)
        (arguments: ImmutableArray<SqlExpr>)
        (targetProvider: SqlAgentToolType) =

        if arguments.Length <> 1 then
            raise (SqlCompilationException("SQL Server LEN requires exactly 1 argument."))

        let targetLength =
            match targetProvider with
            | SqlAgentToolType.Postgres
            | SqlAgentToolType.Oracle
            | SqlAgentToolType.Sqlite -> "LENGTH"
            | SqlAgentToolType.MySQL
            | SqlAgentToolType.Firebird -> "CHAR_LENGTH"
            | _ ->
                raise (SqlCompilationException(
                    $"SQL Server LEN has no Core cross-dialect lowering for target provider {targetProvider}."))

        let trimmed =
            FunctionCallExpr(
                identifier "RTRIM",
                ImmutableArray.Create(arguments[0]),
                false,
                original.Span)
            :> SqlExpr

        renameFunction original (ImmutableArray.Create<SqlExpr>(trimmed)) targetLength

    let normalize
        (sourceDialect: SqlAgentToolType)
        (targetProvider: SqlAgentToolType)
        (sourceName: string)
        (original: FunctionCallExpr)
        (arguments: ImmutableArray<SqlExpr>)
        : SqlExpr =

        let registry = functionRegistry.Value
        let sourceDefinition =
            registry.Find(sourceDialect, sourceName, arguments.Length)

        match sourceDefinition with
        | null ->
            raise (SqlCompilationException(
                $"Function '{sourceName}' is not registered for source dialect {sourceDialect}; normalization was rejected."))
        | sourceDefinition ->
            if not sourceDefinition.Semantic.HasValue then
                raise (SqlCompilationException(
                    $"Function '{sourceName}' has no portable semantic mapping from {sourceDialect}."))

            let semantic = sourceDefinition.Semantic.Value

            if sourceDialect <> targetProvider then
                match semantic with
                | SemanticFunction.Random ->
                    raise (SqlCompilationException(
                        $"Random function '{sourceName}' is not translated across dialects because providers differ in value range and evaluation frequency."))
                | SemanticFunction.StringLength when sourceDialect = SqlAgentToolType.MsSqlServer ->
                    normalizeSqlServerLen original arguments targetProvider
                | SemanticFunction.StringLength when targetProvider = SqlAgentToolType.MsSqlServer ->
                    raise (SqlCompilationException(
                        "Portable string length cannot be translated losslessly to SQL Server LEN because LEN excludes trailing spaces."))
                | SemanticFunction.Repeat
                    when sourceDialect = SqlAgentToolType.MsSqlServer
                         || targetProvider = SqlAgentToolType.MsSqlServer ->
                    raise (SqlCompilationException(
                        "REPLICATE/REPEAT is not translated across SQL Server because SQL Server REPLICATE can truncate non-MAX inputs."))
                | SemanticFunction.Coalesce
                    when not (sourceName.Equals("COALESCE", StringComparison.OrdinalIgnoreCase)) ->
                    raise (SqlCompilationException(
                        $"Provider-specific null function '{sourceName}' is not translated across dialects because its type-conversion rules differ from COALESCE."))
                | _ -> ()

            let targetDefinition =
                registry.Find(targetProvider, semantic, arguments.Length)

            match targetDefinition with
            | null ->
                raise (SqlCompilationException(
                    $"Semantic function '{sourceDefinition.Semantic}' with {arguments.Length} argument(s) is not supported by {targetProvider}."))
            | targetDefinition ->
                if targetDefinition.TranslationKind = FunctionTranslationKind.Template
                   || targetDefinition.TranslationKind = FunctionTranslationKind.Specialized then
                    raise (SqlCompilationException(
                        $"Function '{sourceName}' requires Core {targetDefinition.TranslationKind} translation for target provider {targetProvider}; no lossless Core translator is registered yet."))

                renameFunction original arguments targetDefinition.Name
