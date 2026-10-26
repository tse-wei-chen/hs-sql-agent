namespace HsSqlAgent.SqlCore.Script

open System
open System.Threading
open HsSqlAgent.SqlCore.Catalog
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums

/// Immutable, CLR-friendly inputs for static SQL script parsing.
/// SourceProfile is declared source capability evidence, not execution authority.
[<Sealed>]
type SqlScriptParseOptions(
    sourceDialect: SqlAgentToolType,
    sourceProfile: SqlProviderCapabilityProfile | null,
    cancellationToken: CancellationToken) =

    new(sourceDialect: SqlAgentToolType) =
        SqlScriptParseOptions(sourceDialect, null, CancellationToken.None)

    new(sourceDialect: SqlAgentToolType, sourceProfile: SqlProviderCapabilityProfile | null) =
        SqlScriptParseOptions(sourceDialect, sourceProfile, CancellationToken.None)

    new(sourceDialect: SqlAgentToolType, cancellationToken: CancellationToken) =
        SqlScriptParseOptions(sourceDialect, null, cancellationToken)

    member _.SourceDialect = sourceDialect
    member _.SourceProfile = sourceProfile
    member _.CancellationToken = cancellationToken

/// Immutable inputs for static script analysis. An unspecified Catalog is
/// intentionally UNKNOWN/open-world, never implicitly a proven empty database.
/// This is not a compilation, authorization or execution context.
[<Sealed>]
type SqlScriptAnalysisOptions(
    sourceDialect: SqlAgentToolType,
    initialCatalog: SqlCatalogState,
    sourceProfile: SqlProviderCapabilityProfile | null,
    cancellationToken: CancellationToken) =

    do ArgumentNullException.ThrowIfNull(initialCatalog)

    new(sourceDialect: SqlAgentToolType) =
        SqlScriptAnalysisOptions(
            sourceDialect, SqlCatalogState.Unknown(), null, CancellationToken.None)

    new(sourceDialect: SqlAgentToolType, cancellationToken: CancellationToken) =
        SqlScriptAnalysisOptions(
            sourceDialect, SqlCatalogState.Unknown(), null, cancellationToken)

    new(sourceDialect: SqlAgentToolType, initialCatalog: SqlCatalogState) =
        SqlScriptAnalysisOptions(
            sourceDialect, initialCatalog, null, CancellationToken.None)

    new(sourceDialect: SqlAgentToolType, initialCatalog: SqlCatalogState,
        cancellationToken: CancellationToken) =
        SqlScriptAnalysisOptions(sourceDialect, initialCatalog, null, cancellationToken)

    new(sourceDialect: SqlAgentToolType, initialCatalog: SqlCatalogState,
        sourceProfile: SqlProviderCapabilityProfile | null) =
        SqlScriptAnalysisOptions(
            sourceDialect, initialCatalog, sourceProfile, CancellationToken.None)

    member _.SourceDialect = sourceDialect
    member _.InitialCatalog = initialCatalog
    member _.SourceProfile = sourceProfile
    member _.CancellationToken = cancellationToken
