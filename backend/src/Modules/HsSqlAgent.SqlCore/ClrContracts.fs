
namespace HsSqlAgent.SqlCore.Enums

type SqlAgentToolType =
    | Sqlite = 0
    | Postgres = 1
    | MySQL = 2
    | MsSqlServer = 3
    | Oracle = 4
    | Firebird = 5

type ArithmeticOperator =
    | Add = 0
    | Subtract = 1
    | Multiply = 2
    | Divide = 3
    | Modulo = 4
    | Concat = 5
    | Equal = 6
    | NotEqual = 7
    | GreaterThan = 8
    | LessThan = 9
    | GreaterThanOrEqual = 10
    | LessThanOrEqual = 11
    | And = 12
    | Or = 13

type CombineType =
    | Union = 0
    | UnionAll = 1
    | Intersect = 2
    | Except = 3

type DmlOperation =
    | Insert = 0
    | Update = 1
    | Delete = 2

type JoinType =
    | Inner = 0
    | Left = 1
    | Right = 2
    | Full = 3
    | Cross = 4

type NullOrdering =
    | Default = 0
    | First = 1
    | Last = 2

type SortDirection =
    | Asc = 0
    | Desc = 1
    | Random = 2

type WindowFrameUnit =
    | Rows = 0
    | Range = 1

type WindowFrameBoundKind =
    | UnboundedPreceding = 0
    | Preceding = 1
    | CurrentRow = 2
    | Following = 3
    | UnboundedFollowing = 4

namespace HsSqlAgent.SqlCore.Core.Compilation

open System
open System.Data
open System.Data.Common
open System.Collections.Immutable
open HsSqlAgent.SqlCore.Enums

type SqlStatementKind =
    | Query = 0
    | Select = 0
    | Insert = 1
    | Update = 2
    | Delete = 3
    | Merge = 4

type SqlDiagnosticStage =
    | Lexical = 0
    | Parse = 1
    | Binding = 2
    | SourceValidation = 3
    | SemanticValidation = 4
    | TargetCapability = 5
    | Policy = 6
    | RenderingInvariant = 7

type SqlDiagnosticCategory =
    | Syntax = 0
    | DialectSyntax = 1
    | Binding = 2
    | Capability = 3
    | Semantic = 4
    | Policy = 5
    | Invariant = 6
    | Resource = 7

[<Sealed>]
type SqlDiagnosticSpan(start: int, length: int) =
    do
        if start < 0 then invalidArg (nameof start) "Diagnostic span start must be non-negative."
        if length < 0 then invalidArg (nameof length) "Diagnostic span length must be non-negative."
    member _.Start = start
    member _.Length = length
    member _.End = start + length

[<Sealed>]
type SqlDiagnostic(
    code: string,
    stage: SqlDiagnosticStage,
    category: SqlDiagnosticCategory,
    message: string,
    span: SqlDiagnosticSpan | null) =
    do
        if String.IsNullOrWhiteSpace(code) then invalidArg (nameof code) "Diagnostic code cannot be empty."
        if String.IsNullOrWhiteSpace(message) then invalidArg (nameof message) "Diagnostic message cannot be empty."
    member _.Code = code
    member _.Stage = stage
    member _.Category = category
    member _.Message = message
    member _.Span = span

[<Sealed>]
type SqlParameterValue(name: string, value: obj | null) =
    member _.Name = name
    member _.Value = value

type SqlCompileVerdict =
    | Translated = 0
    | Rejected = 1

type SqlCompileDecisionBoundary =
    | Completed = 0
    | InputValidation = 1
    | Lexical = 2
    | Parse = 3
    | Binding = 4
    | SourceValidation = 5
    | SemanticValidation = 6
    | TargetCapability = 7
    | Policy = 8
    | RenderingInvariant = 9

[<Sealed>]
type SqlDeterminismInspectionFacts(
    parseFingerprint: string,
    canonicalFingerprint: string,
    renormalizedCanonicalFingerprint: string,
    normalizationIdempotent: bool) =
    do
        if String.IsNullOrWhiteSpace(parseFingerprint) then invalidArg (nameof parseFingerprint) "Parse fingerprint cannot be empty."
        if String.IsNullOrWhiteSpace(canonicalFingerprint) then invalidArg (nameof canonicalFingerprint) "Canonical fingerprint cannot be empty."
        if String.IsNullOrWhiteSpace(renormalizedCanonicalFingerprint) then invalidArg (nameof renormalizedCanonicalFingerprint) "Renormalized canonical fingerprint cannot be empty."
    member _.ParseFingerprint = parseFingerprint
    member _.CanonicalFingerprint = canonicalFingerprint
    member _.RenormalizedCanonicalFingerprint = renormalizedCanonicalFingerprint
    member _.NormalizationIdempotent = normalizationIdempotent

type SqlCompileCapabilitySide =
    | Source = 0
    | Target = 1

type SqlCompileCapabilityStatus =
    | Supported = 0
    | Translated = 1
    | Rejected = 2

[<Sealed>]
type SqlCompileSettingEvidence(name: string, value: string) =
    do
        if String.IsNullOrWhiteSpace(name) then invalidArg (nameof name) "Compile-evidence setting name cannot be empty."
    member _.Name = name
    member _.Value = value

[<Sealed>]
type SqlCompileProfileEvidence(
    provider: SqlAgentToolType,
    serverVersion: string | null,
    compatibilityLevel: Nullable<int>,
    sessionModes: ImmutableArray<string>,
    sessionSettings: ImmutableArray<SqlCompileSettingEvidence>) =
    member _.Provider = provider
    member _.ServerVersion = serverVersion
    member _.CompatibilityLevel = compatibilityLevel
    member _.SessionModes = sessionModes
    member _.SessionSettings = sessionSettings

[<Sealed>]
type SqlCompileCapabilityEvidence(
    side: SqlCompileCapabilitySide,
    id: string,
    category: string,
    status: SqlCompileCapabilityStatus,
    detail: string) =
    do
        if String.IsNullOrWhiteSpace(id) then invalidArg (nameof id) "Compile-evidence capability id cannot be empty."
    member _.Side = side
    member _.Id = id
    member _.Category = category
    member _.Status = status
    member _.Detail = detail

[<Sealed>]
type SqlCompilePolicyEvidence(
    policyVersion: string,
    queryMaxRows: int,
    requireUpdatePredicate: bool,
    requireDeletePredicate: bool,
    allowedTables: ImmutableArray<string>) =
    do
        if String.IsNullOrWhiteSpace(policyVersion) then invalidArg (nameof policyVersion) "Compile-evidence policy version cannot be empty."
        if queryMaxRows < 0 then invalidArg (nameof queryMaxRows) "Compile-evidence query row cap cannot be negative."
    member _.PolicyVersion = policyVersion
    member _.QueryMaxRows = queryMaxRows
    member _.RequireUpdatePredicate = requireUpdatePredicate
    member _.RequireDeletePredicate = requireDeletePredicate
    member _.AllowedTables = allowedTables

[<Sealed>]
type SqlCompileAssuranceEvidence(
    kind: string,
    details: ImmutableArray<SqlCompileSettingEvidence>) =
    do
        if String.IsNullOrWhiteSpace(kind) then invalidArg (nameof kind) "Compile-evidence assurance kind cannot be empty."
    member _.Kind = kind
    member _.Details = details

[<Sealed>]
type SqlCompileEvidence(
    schemaVersion: string,
    capabilityMatrixVersion: string,
    sourceProfile: SqlCompileProfileEvidence,
    targetProfile: SqlCompileProfileEvidence,
    sourceCapabilities: ImmutableArray<SqlCompileCapabilityEvidence>,
    targetCapabilities: ImmutableArray<SqlCompileCapabilityEvidence>,
    policy: SqlCompilePolicyEvidence,
    assurances: ImmutableArray<SqlCompileAssuranceEvidence>,
    verdict: SqlCompileVerdict,
    decisionBoundary: SqlCompileDecisionBoundary,
    decisionCode: string,
    planFingerprint: string | null,
    evidenceFingerprint: string) =
    static let evidenceDataKey = "HsSqlAgent.SqlCore.CompileEvidence"
    do
        if String.IsNullOrWhiteSpace(schemaVersion) then invalidArg (nameof schemaVersion) "Compile-evidence schema version cannot be empty."
        if String.IsNullOrWhiteSpace(capabilityMatrixVersion) then invalidArg (nameof capabilityMatrixVersion) "Compile-evidence matrix version cannot be empty."
        if Object.ReferenceEquals(sourceProfile, null) then nullArg (nameof sourceProfile)
        if Object.ReferenceEquals(targetProfile, null) then nullArg (nameof targetProfile)
        if Object.ReferenceEquals(policy, null) then nullArg (nameof policy)
        if String.IsNullOrWhiteSpace(decisionCode) then invalidArg (nameof decisionCode) "Compile-evidence decision code cannot be empty."
        if String.IsNullOrWhiteSpace(evidenceFingerprint) then invalidArg (nameof evidenceFingerprint) "Compile-evidence fingerprint cannot be empty."
    member _.SchemaVersion = schemaVersion
    member _.CapabilityMatrixVersion = capabilityMatrixVersion
    member _.SourceProfile = sourceProfile
    member _.TargetProfile = targetProfile
    member _.SourceCapabilities = sourceCapabilities
    member _.TargetCapabilities = targetCapabilities
    member _.Policy = policy
    member _.Assurances = assurances
    member _.Verdict = verdict
    member _.DecisionBoundary = decisionBoundary
    member _.DecisionCode = decisionCode
    member _.PlanFingerprint = planFingerprint
    member _.EvidenceFingerprint = evidenceFingerprint
    static member internal DataKey = evidenceDataKey
    static member TryGetFromException(error: Exception) : SqlCompileEvidence | null =
        ArgumentNullException.ThrowIfNull(error)
        match error.Data[evidenceDataKey] with
        | :? SqlCompileEvidence as evidence -> evidence
        | _ -> null

[<Sealed>]
type CompiledSqlCommand private (
    sql: string,
    parameters: ImmutableArray<SqlParameterValue>,
    kind: SqlStatementKind,
    planFingerprint: string,
    targetProvider: SqlAgentToolType,
    returnsRows: bool,
    compileEvidence: SqlCompileEvidence | null) =

    new(
        sql: string,
        parameters: ImmutableArray<SqlParameterValue>,
        kind: SqlStatementKind,
        planFingerprint: string,
        targetProvider: SqlAgentToolType) =
        CompiledSqlCommand(
            sql,
            parameters,
            kind,
            planFingerprint,
            targetProvider,
            false,
            null)

    member _.Sql = sql
    member _.Parameters = parameters
    member _.Kind = kind
    member _.PlanFingerprint = planFingerprint
    member _.TargetProvider = targetProvider
    member _.ReturnsRows = returnsRows
    member _.CompileEvidence = compileEvidence

    static member internal Create(
        sql: string,
        parameters: ImmutableArray<SqlParameterValue>,
        kind: SqlStatementKind,
        planFingerprint: string,
        targetProvider: SqlAgentToolType,
        returnsRows: bool) =
        CompiledSqlCommand(
            sql,
            parameters,
            kind,
            planFingerprint,
            targetProvider,
            returnsRows,
            null)

    static member internal Create(
        sql: string,
        parameters: ImmutableArray<SqlParameterValue>,
        kind: SqlStatementKind,
        planFingerprint: string,
        targetProvider: SqlAgentToolType,
        returnsRows: bool,
        compileEvidence: SqlCompileEvidence | null) =
        CompiledSqlCommand(
            sql,
            parameters,
            kind,
            planFingerprint,
            targetProvider,
            returnsRows,
            compileEvidence)

type SqlCompilationException(message: string, innerException: Exception | null, diagnostic: SqlDiagnostic | null) as this =
    inherit InvalidOperationException(message, innerException)
    new(message: string) = SqlCompilationException(message, null, null)
    new(message: string, innerException: Exception) = SqlCompilationException(message, innerException, null)
    new(message: string, diagnostic: SqlDiagnostic) = SqlCompilationException(message, null, diagnostic)
    member _.Diagnostic = diagnostic
    member _.CompileEvidence = SqlCompileEvidence.TryGetFromException(this)

namespace HsSqlAgent.SqlCore.SqlParsing

open System
open HsSqlAgent.SqlCore.Core.Compilation

type SqlParseException(message: string, innerException: Exception | null, diagnostic: SqlDiagnostic | null) as this =
    inherit Exception(message, innerException)
    new(message: string) = SqlParseException(message, null, null)
    new(message: string, innerException: Exception) = SqlParseException(message, innerException, null)
    new(message: string, diagnostic: SqlDiagnostic) = SqlParseException(message, null, diagnostic)
    member _.Diagnostic = diagnostic
    member _.CompileEvidence = SqlCompileEvidence.TryGetFromException(this)

namespace HsSqlAgent.SqlCore.Models

open System
open System.Collections.Generic
open System.Data
open System.Data.Common
open HsSqlAgent.SqlCore.Enums

[<Sealed>]
type SqlProviderCapabilityProfile(
    provider: SqlAgentToolType,
    serverVersion: Version | null,
    compatibilityLevel: Nullable<int>,
    sessionModes: IReadOnlySet<string> | null,
    sessionSettings: IReadOnlyDictionary<string, string> | null) =

    new(provider: SqlAgentToolType) =
        SqlProviderCapabilityProfile(provider, null, Nullable(), null, null)

    new(provider: SqlAgentToolType, ``ServerVersion``: Version) =
        SqlProviderCapabilityProfile(provider, ``ServerVersion``, Nullable(), null, null)

    new(provider: SqlAgentToolType, ``CompatibilityLevel``: Nullable<int>) =
        SqlProviderCapabilityProfile(provider, null, ``CompatibilityLevel``, null, null)

    new(provider: SqlAgentToolType, ``ServerVersion``: Version, ``CompatibilityLevel``: Nullable<int>) =
        SqlProviderCapabilityProfile(provider, ``ServerVersion``, ``CompatibilityLevel``, null, null)

    new(provider: SqlAgentToolType, ``ServerVersion``: Version, ``SessionModes``: IReadOnlySet<string>) =
        SqlProviderCapabilityProfile(provider, ``ServerVersion``, Nullable(), ``SessionModes``, null)

    new(provider: SqlAgentToolType, ``ServerVersion``: Version, ``SessionModes``: IReadOnlySet<string>, ``SessionSettings``: IReadOnlyDictionary<string, string>) =
        SqlProviderCapabilityProfile(provider, ``ServerVersion``, Nullable(), ``SessionModes``, ``SessionSettings``)

    member _.Provider = provider
    member _.ServerVersion = serverVersion
    member _.CompatibilityLevel = compatibilityLevel
    member _.SessionModes = sessionModes
    member _.SessionSettings = sessionSettings

    member _.HasSessionMode(mode: string) =
        if String.IsNullOrWhiteSpace(mode) then false
        else
            match sessionModes with
            | null -> false
            | modes ->
                modes
                |> Seq.exists (fun candidate ->
                    String.Equals(candidate, mode, StringComparison.OrdinalIgnoreCase))

    member _.GetSessionSetting(name: string) : string | null =
        if String.IsNullOrWhiteSpace(name) then null
        else
            match sessionSettings with
            | null -> null
            | settings ->
                match
                    settings
                    |> Seq.tryPick (fun pair ->
                        if String.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase) then Some pair.Value else None)
                with
                | Some value -> value
                | None -> null

type internal SqlProviderCapabilityProfileValidationIssue =
    | None = 0
    | ProviderMismatch = 1
    | NegativeCompatibilityLevel = 2

[<AbstractClass; Sealed>]
type internal SqlProviderCapabilityProfileRules private () =
    static member ValidationIssue(profile: SqlProviderCapabilityProfile | null, expectedProvider: SqlAgentToolType) =
        match profile with
        | null -> SqlProviderCapabilityProfileValidationIssue.None
        | value when value.Provider <> expectedProvider ->
            SqlProviderCapabilityProfileValidationIssue.ProviderMismatch
        | value when value.CompatibilityLevel.HasValue && value.CompatibilityLevel.Value < 0 ->
            SqlProviderCapabilityProfileValidationIssue.NegativeCompatibilityLevel
        | _ -> SqlProviderCapabilityProfileValidationIssue.None

[<AbstractClass>]
type SqlTemporalValue() = class end

[<Sealed>]
type SqlDateValue() =
    inherit SqlTemporalValue()
    member val Value = DateOnly.MinValue with get, set
    new(value: DateOnly) as this = SqlDateValue() then this.Value <- value

[<Sealed>]
type SqlTimeValue() =
    inherit SqlTemporalValue()
    member val Value = TimeOnly.MinValue with get, set
    new(value: TimeOnly) as this = SqlTimeValue() then this.Value <- value

[<Sealed>]
type SqlLocalDateTimeValue() =
    inherit SqlTemporalValue()
    member val Value = DateTime.MinValue with get, set
    new(value: DateTime) as this = SqlLocalDateTimeValue() then
        this.Value <- DateTime.SpecifyKind(value, DateTimeKind.Unspecified)

[<Sealed>]
type SqlOffsetDateTimeValue() =
    inherit SqlTemporalValue()
    member val Value = DateTimeOffset.MinValue with get, set
    new(value: DateTimeOffset) as this = SqlOffsetDateTimeValue() then this.Value <- value

type BuildDbConnectionModelBase() =
    member val Host: string | null = null with get, set
    member val Port: string | null = null with get, set
    member val Username: string | null = null with get, set
    member val Password: string | null = null with get, set
    member val Database: string | null = null with get, set
    member val ExtraSettings: string | null = null with get, set

type BuildDbConnectionModel() =
    inherit BuildDbConnectionModelBase()
    member val Provider = String.Empty with get, set

type ColumnInfo() =
    let mutable name = String.Empty
    member _.Name with get() = name and set value = name <- value
    member _.Column with get() = name and set value = name <- value
    member val Type = String.Empty with get, set
    member val Description = String.Empty with get, set
    member val IsPrimaryKey = false with get, set
    member val PrimaryKeyOrdinal = Nullable<int>() with get, set
    new(name: string, typeName: string, isPrimaryKey: bool, primaryKeyOrdinal: Nullable<int>) as this =
        ColumnInfo()
        then
            this.Name <- name
            this.Type <- typeName
            this.IsPrimaryKey <- isPrimaryKey
            this.PrimaryKeyOrdinal <- primaryKeyOrdinal
    new(name: string, typeName: string) = ColumnInfo(name, typeName, false, Nullable())
    new(name: string, typeName: string, isPrimaryKey: bool) = ColumnInfo(name, typeName, isPrimaryKey, Nullable())

type SqlExecutionPolicy() =
    member val QueryMaxRows = 0 with get, set
    member val QueryTimeoutSeconds = 30 with get, set
    member val RequireWhereForUpdate = false with get, set
    member val RequireWhereForDelete = false with get, set
    member val AllowFullTableUpdate = false with get, set
    member val AllowFullTableDelete = false with get, set
    member val DmlMaxAffectedRows = 0 with get, set

type SqlCapabilityStatus =
    | Supported = 0
    | Translated = 1
    | Rejected = 2

[<Sealed>]
type SqlCapability(id: string, category: string, status: SqlCapabilityStatus, detail: string) =
    member _.Id = id
    member _.Category = category
    member _.Status = status
    member _.Detail = detail

[<Sealed>]
type ProviderSqlCapabilities(matrixVersion: string, provider: SqlAgentToolType, capabilities: IReadOnlyList<SqlCapability>) =
    member _.MatrixVersion = matrixVersion
    member _.Provider = provider
    member _.Capabilities = capabilities

namespace HsSqlAgent.SqlCore.Core.Pipeline

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.Data
open System.Data.Common
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Core.Compilation

[<Sealed>]
type DmlCompilationPolicy(
    requireWhereForUpdate: bool,
    requireWhereForDelete: bool,
    allowFullTableUpdate: bool,
    allowFullTableDelete: bool) =
    new() = DmlCompilationPolicy(true, true, false, false)
    new(requireWhereForUpdate: bool) = DmlCompilationPolicy(requireWhereForUpdate, true, false, false)
    new(requireWhereForUpdate: bool, requireWhereForDelete: bool) =
        DmlCompilationPolicy(requireWhereForUpdate, requireWhereForDelete, false, false)
    new(requireWhereForUpdate: bool, requireWhereForDelete: bool, allowFullTableUpdate: bool) =
        DmlCompilationPolicy(requireWhereForUpdate, requireWhereForDelete, allowFullTableUpdate, false)
    member _.RequireWhereForUpdate = requireWhereForUpdate
    member _.RequireWhereForDelete = requireWhereForDelete
    member _.AllowFullTableUpdate = allowFullTableUpdate
    member _.AllowFullTableDelete = allowFullTableDelete

[<Sealed>]
type DmlConflictTargetAssurance(primaryKeyColumns: ImmutableArray<string>) =
    let mutable matchedUniqueKeyColumns = ImmutableArray<string>.Empty
    let mutable matchedUniqueKeyName: string | null = null
    let mutable matchedUniqueKeyIsPrimaryKey = false
    let mutable enforcedUniqueKeyCount = 0
    let mutable hasUnsupportedEnforcedUniqueKeys = false
    let mutable sourceRowsUniqueByInsertColumns = ImmutableArray<string>.Empty

    member _.PrimaryKeyColumns = primaryKeyColumns
    member _.MatchedUniqueKeyColumns with get() = matchedUniqueKeyColumns and set value = matchedUniqueKeyColumns <- value
    member _.MatchedUniqueKeyName with get() = matchedUniqueKeyName and set value = matchedUniqueKeyName <- value
    member _.MatchedUniqueKeyIsPrimaryKey with get() = matchedUniqueKeyIsPrimaryKey and set value = matchedUniqueKeyIsPrimaryKey <- value
    member _.EnforcedUniqueKeyCount with get() = enforcedUniqueKeyCount and set value = enforcedUniqueKeyCount <- value
    member _.HasUnsupportedEnforcedUniqueKeys with get() = hasUnsupportedEnforcedUniqueKeys and set value = hasUnsupportedEnforcedUniqueKeys <- value
    member _.SourceRowsUniqueByInsertColumns with get() = sourceRowsUniqueByInsertColumns and set value = sourceRowsUniqueByInsertColumns <- value
    member _.IsSoleEnforcedUniqueKey =
        not matchedUniqueKeyColumns.IsDefaultOrEmpty
        && enforcedUniqueKeyCount = 1
        && not hasUnsupportedEnforcedUniqueKeys

    static member private NormalizeColumns(columns: IEnumerable<string>, assuranceName: string, parameterName: string) =
        if Object.ReferenceEquals(columns, null) then nullArg parameterName
        let normalized =
            columns
            |> Seq.map (fun column ->
                if String.IsNullOrWhiteSpace(column) then
                    raise (ArgumentException(assuranceName + " columns cannot be empty.", parameterName))
                column.Trim())
            |> Seq.toArray
        if normalized.Length = 0 then raise (ArgumentException(assuranceName + " requires at least one column.", parameterName))
        if (normalized |> Seq.distinctBy (fun value -> value.ToUpperInvariant()) |> Seq.length) <> normalized.Length then
            raise (ArgumentException(assuranceName + " columns cannot contain duplicates.", parameterName))
        ImmutableArray.CreateRange(normalized)

    member this.WithSourceRowsUniqueByInsertColumns(columns: IEnumerable<string>) =
        let clone = DmlConflictTargetAssurance(primaryKeyColumns)
        clone.MatchedUniqueKeyColumns <- matchedUniqueKeyColumns
        clone.MatchedUniqueKeyName <- matchedUniqueKeyName
        clone.MatchedUniqueKeyIsPrimaryKey <- matchedUniqueKeyIsPrimaryKey
        clone.EnforcedUniqueKeyCount <- enforcedUniqueKeyCount
        clone.HasUnsupportedEnforcedUniqueKeys <- hasUnsupportedEnforcedUniqueKeys
        clone.SourceRowsUniqueByInsertColumns <- DmlConflictTargetAssurance.NormalizeColumns(columns, "Source-row uniqueness assurance", "columns")
        clone

    static member FromPrimaryKey(columns: IEnumerable<string>) =
        DmlConflictTargetAssurance(DmlConflictTargetAssurance.NormalizeColumns(columns, "Primary-key assurance", "columns"))

    static member FromUniqueKey(
        columns: IEnumerable<string>,
        keyName: string,
        isPrimaryKey: bool,
        enforcedUniqueKeyCount: int,
        hasUnsupportedEnforcedUniqueKeys: bool) =
        if String.IsNullOrWhiteSpace(keyName) then invalidArg "keyName" "Unique-key assurance key name cannot be empty."
        if enforcedUniqueKeyCount < 1 then
            raise (ArgumentOutOfRangeException("enforcedUniqueKeyCount", enforcedUniqueKeyCount, "Unique-key assurance requires at least one enforced unique key in the provider inventory."))
        let value = DmlConflictTargetAssurance(ImmutableArray<string>.Empty)
        value.MatchedUniqueKeyColumns <- DmlConflictTargetAssurance.NormalizeColumns(columns, "Unique-key assurance", "columns")
        value.MatchedUniqueKeyName <- keyName.Trim()
        value.MatchedUniqueKeyIsPrimaryKey <- isPrimaryKey
        value.EnforcedUniqueKeyCount <- enforcedUniqueKeyCount
        value.HasUnsupportedEnforcedUniqueKeys <- hasUnsupportedEnforcedUniqueKeys
        value

[<Sealed>]
type DmlResultRowAssurance private (targetTable: string, operation: DmlOperation) =
    member _.TargetTable = targetTable
    member _.Operation = operation

    static member NoEnabledTriggers(targetTable: string, operation: DmlOperation) =
        if String.IsNullOrWhiteSpace(targetTable) then
            invalidArg "targetTable" "DML result-row assurance requires a non-empty target table."
        DmlResultRowAssurance(targetTable.Trim(), operation)

/// Portable, opt-in type families, never provider-native SQL type declarations.
/// Unknown is intentional: name-only catalogs preserve earlier checking behavior.
type SqlSchemaValueKind =
    | Unknown = 0
    | Boolean = 1
    | Numeric = 2
    | Text = 3
    | Temporal = 4
    | Binary = 5
    | Json = 6

[<Sealed>]
type SqlSchemaColumn(name: string, kind: SqlSchemaValueKind) =
    do
        if String.IsNullOrWhiteSpace(name) || name <> name.Trim() then
            invalidArg "name" "Schema column names must be non-empty and trimmed."
        if not (Enum.IsDefined(kind)) then
            invalidArg "kind" "Unknown schema value-kind enum value."
    member _.Name = name
    member _.Kind = kind

/// Host-provided immutable catalog table; both typed and legacy name-only paths
/// take defensive copies, so callers cannot mutate a compilation's proof inputs.
[<Sealed>]
type SqlSchemaTable private (name: string, definitions: ImmutableArray<SqlSchemaColumn>) =
    do ArgumentException.ThrowIfNullOrWhiteSpace(name)
    do
        if name.Split('.') |> Array.exists String.IsNullOrWhiteSpace then
            invalidArg "name" "Table identifier parts must be non-empty."
        if definitions.IsDefaultOrEmpty then
            invalidArg "columns" "Schema tables require at least one column."
        if definitions |> Seq.exists (fun c -> Object.ReferenceEquals(c, null)) then
            invalidArg "columns" "Schema columns cannot be null."
        if (definitions |> Seq.map (fun c -> c.Name) |> Seq.distinct |> Seq.length) <> definitions.Length then
            invalidArg "columns" "Schema tables may not declare duplicate column names."

    let names =
        definitions |> Seq.map (fun c -> c.Name) |> ImmutableArray.CreateRange<string>

    new(name: string, columns: IEnumerable<string>) =
        SqlSchemaTable(
            name,
            if isNull (box columns) then nullArg "columns"
            else
                columns
                |> Seq.map (fun column -> SqlSchemaColumn(column, SqlSchemaValueKind.Unknown))
                |> ImmutableArray.CreateRange<SqlSchemaColumn>)

    static member WithTypes(name: string, columns: IEnumerable<SqlSchemaColumn>) =
        ArgumentNullException.ThrowIfNull(columns)
        SqlSchemaTable(name, ImmutableArray.CreateRange<SqlSchemaColumn>(columns))

    member _.Name = name
    member _.Columns : IReadOnlyList<string> = names
    member _.TypedColumns : IReadOnlyList<SqlSchemaColumn> = definitions

/// Metadata is copied on construction. Version identifies the supplied snapshot;
/// it does not imply catalog freshness or transaction-level execution guarantees.
[<Sealed>]
type SqlSchemaSnapshot(provider: SqlAgentToolType, version: string, tables: IEnumerable<SqlSchemaTable>) =
    do ArgumentException.ThrowIfNullOrWhiteSpace(version)
    do ArgumentNullException.ThrowIfNull(tables)
    let copies =
        tables
        |> Seq.map (fun item ->
            ArgumentNullException.ThrowIfNull(item)
            item)
        |> ImmutableArray.CreateRange<SqlSchemaTable>
    do
        if copies.IsEmpty then invalidArg "tables" "Schema snapshots must contain at least one table."
        if (copies |> Seq.map (fun item -> item.Name) |> Seq.distinct |> Seq.length) <> copies.Length then
            invalidArg "tables" "Schema snapshots may not declare duplicate table names."
    member _.Provider = provider
    member _.Version = version
    member _.Tables : IReadOnlyList<SqlSchemaTable> = copies

    /// Exact immutable catalog proof identity, shared by compilation and runtime hosts.
    member _.Sha256 =
        let builder = StringBuilder()
        let append (text: string) =
            builder.Append(text.Length).Append(':').Append(text).Append(';') |> ignore
        append "sqlcore-schema-v1"
        append (string provider)
        append version
        copies
        |> Seq.sortWith (fun a b -> StringComparer.Ordinal.Compare(a.Name, b.Name))
        |> Seq.iter (fun table ->
            append table.Name
            append (string table.Columns.Count)
            table.TypedColumns
            |> Seq.sortWith (fun left right -> StringComparer.Ordinal.Compare(left.Name, right.Name))
            |> Seq.iter (fun column ->
                append column.Name
                append (string (int column.Kind))))
        builder.ToString()
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> fun digest -> digest.ToLowerInvariant()

/// Snapshot-bound evidence minted from actual SQLite PRAGMA metadata.
/// Caller must reuse an unchanged live schema through execution; the
/// compiler has no connection and cannot prove absence of later DDL.
[<Sealed>]
type SqliteUpdateFromKeyAssurance private (
    pinnedSnapshot: SqlSchemaSnapshot,
    schemaSha256: string,
    targetTable: string,
    targetPrimaryKey: string,
    sourceTable: string,
    sourcePrimaryKey: string,
    schemaVersion: int) =

    member _.SchemaSha256 = schemaSha256
    member _.TargetTable = targetTable
    member _.TargetPrimaryKey = targetPrimaryKey
    member _.SourceTable = sourceTable
    member _.SourcePrimaryKey = sourcePrimaryKey
    member _.DatabaseSchemaVersion = schemaVersion

    /// The compiler does not contact a database. Immediately before a host
    /// executes a compiled command, re-read the live PK/index metadata and
    /// SQLite schema cookie on the *execution connection*.
    member this.IsCurrent(connection: DbConnection) =
        try
            let refreshed = SqliteUpdateFromKeyAssurance.VerifyLive(
                connection, pinnedSnapshot, targetTable, targetPrimaryKey,
                sourceTable, sourcePrimaryKey)
            refreshed.DatabaseSchemaVersion = schemaVersion
            && StringComparer.Ordinal.Equals(refreshed.SchemaSha256, schemaSha256)
        with
        | :? ArgumentException
        | :? InvalidOperationException
        | :? DbException -> false

    /// The signature binds the attested PK metadata to compiler evidence,
    /// independent of parameter values or SQL text.
    member _.ProofIdentity =
        String.Join("|", [| "sqlite-live-pk-v1"; schemaSha256; targetTable; targetPrimaryKey;
                            sourceTable; sourcePrimaryKey; string schemaVersion |])

    static member VerifyLive(
        connection: DbConnection, snapshot: SqlSchemaSnapshot,
        targetTable: string, targetPrimaryKey: string,
        sourceTable: string, sourcePrimaryKey: string) =

        ArgumentNullException.ThrowIfNull(connection)
        ArgumentNullException.ThrowIfNull(snapshot)
        if connection.State <> ConnectionState.Open then
            invalidArg "connection" "SQLite metadata assurance requires an open connection."
        if snapshot.Provider <> SqlAgentToolType.Sqlite then
            invalidArg "snapshot" "SQLite metadata assurance requires a SQLite schema snapshot."

        let identifier (name: string) (value: string) =
            let ascii c =
                (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
                || (c >= '0' && c <= '9') || c = '_'
            if String.IsNullOrEmpty value
               || not ((value.[0] >= 'A' && value.[0] <= 'Z')
                       || (value.[0] >= 'a' && value.[0] <= 'z') || value.[0] = '_')
               || not (value |> Seq.forall ascii) then
                invalidArg name "SQLite live PK evidence supports only simple ASCII identifiers."
            value

        let target = identifier "targetTable" targetTable
        let targetPk = identifier "targetPrimaryKey" targetPrimaryKey
        let source = identifier "sourceTable" sourceTable
        let sourcePk = identifier "sourcePrimaryKey" sourcePrimaryKey
        if String.Equals(target, source, StringComparison.OrdinalIgnoreCase) then
            invalidArg "sourceTable" "Self-joined SQLite PK assurance is outside the proven subset."

        let ensureSnapshot table key =
            let found =
                snapshot.Tables
                |> Seq.filter (fun item ->
                    String.Equals(item.Name, table, StringComparison.OrdinalIgnoreCase))
                |> Seq.toList
            match found with
            | [tableInfo] ->
                let cols =
                    tableInfo.TypedColumns
                    |> Seq.filter (fun column ->
                        String.Equals(column.Name, key, StringComparison.OrdinalIgnoreCase)
                        && column.Kind = SqlSchemaValueKind.Numeric)
                    |> Seq.length
                if cols <> 1 then
                    invalidArg "snapshot" "Live SQLite INTEGER PK must have a matching Numeric schema column."
            | _ -> invalidArg "snapshot" "Live SQLite PK table must exist exactly once in snapshot."

        let scalar sql =
            use command = connection.CreateCommand()
            command.CommandText <- sql
            command.ExecuteScalar()

        let version = Convert.ToString(scalar "SELECT sqlite_version()")
        if String.IsNullOrWhiteSpace(version)
           || Version.Parse(version).CompareTo(Version(3, 33, 0)) < 0 then
            invalidArg "connection" "SQLite 3.33+ is required for UPDATE FROM."

        let verifyPrimaryKey table key =
            use columns = connection.CreateCommand()
            columns.CommandText <- "PRAGMA table_info(" + table + ")"
            use reader = columns.ExecuteReader()
            let mutable pkCount = 0
            let mutable isExactIntegerPk = false
            while reader.Read() do
                let ordinal = Convert.ToInt32(reader.GetValue(5))
                if ordinal > 0 then
                    pkCount <- pkCount + 1
                    if ordinal = 1
                       && String.Equals(reader.GetString(1), key, StringComparison.OrdinalIgnoreCase)
                       && String.Equals(reader.GetString(2), "INTEGER", StringComparison.OrdinalIgnoreCase) then
                        isExactIntegerPk <- true
            if pkCount <> 1 || not isExactIntegerPk then
                invalidArg "connection" "SQLite PK assurance requires one exact INTEGER PRIMARY KEY column."
            // A rowid alias has no separate PK index. An ordinary indexed
            // PRIMARY KEY (including INTEGER PRIMARY KEY DESC / WITHOUT
            // ROWID) does not meet this contract.
            use indexes = connection.CreateCommand()
            indexes.CommandText <- "PRAGMA index_list(" + table + ")"
            use indexReader = indexes.ExecuteReader()
            while indexReader.Read() do
                if String.Equals(indexReader.GetString(3), "pk", StringComparison.OrdinalIgnoreCase) then
                    invalidArg "connection" "SQLite PK assurance needs a rowid-alias INTEGER PRIMARY KEY."

        ensureSnapshot target targetPk
        ensureSnapshot source sourcePk
        verifyPrimaryKey target targetPk
        verifyPrimaryKey source sourcePk
        let schemaVersion = Convert.ToInt32(scalar "PRAGMA schema_version")
        SqliteUpdateFromKeyAssurance(
            snapshot, snapshot.Sha256, target, targetPk, source, sourcePk, schemaVersion)

[<Sealed>]
type SqlPlanValidationContext private (
    policyVersion: string,
    allowedTables: IReadOnlySet<string> | null,
    dmlResultRowAssurance: DmlResultRowAssurance | null,
    schemaSnapshot: SqlSchemaSnapshot | null,
    sqliteUpdateFromKeyAssurance: SqliteUpdateFromKeyAssurance | null) =

    new(policyVersion: string) =
        SqlPlanValidationContext(policyVersion, null, null, null, null)

    new(policyVersion: string, allowedTables: IReadOnlySet<string> | null) =
        SqlPlanValidationContext(policyVersion, allowedTables, null, null, null)

    member _.PolicyVersion = policyVersion
    member _.AllowedTables = allowedTables
    member _.DmlResultRowAssurance = dmlResultRowAssurance
    member _.SchemaSnapshot = schemaSnapshot
    member _.SqliteUpdateFromKeyAssurance = sqliteUpdateFromKeyAssurance

    member _.WithDmlResultRowAssurance(assurance: DmlResultRowAssurance) =
        ArgumentNullException.ThrowIfNull(assurance)
        SqlPlanValidationContext(policyVersion, allowedTables, assurance, schemaSnapshot,
                                 sqliteUpdateFromKeyAssurance)

    member _.WithSchemaSnapshot(snapshot: SqlSchemaSnapshot) =
        ArgumentNullException.ThrowIfNull(snapshot)
        SqlPlanValidationContext(policyVersion, allowedTables, dmlResultRowAssurance, snapshot,
                                 sqliteUpdateFromKeyAssurance)

    member _.WithSqliteUpdateFromKeyAssurance(assurance: SqliteUpdateFromKeyAssurance) =
        ArgumentNullException.ThrowIfNull(assurance)
        if isNull schemaSnapshot then
            invalidOp "SQLite live PK assurance requires a schema snapshot first."
        SqlPlanValidationContext(policyVersion, allowedTables, dmlResultRowAssurance,
                                 schemaSnapshot, assurance)

[<Sealed>]
type SqlExecutionPlanPolicy(queryMaxRows: int) =
    new() = SqlExecutionPlanPolicy(0)
    member _.QueryMaxRows = queryMaxRows

[<Sealed>]
type QueryExecutionResult(
    rows: IReadOnlyList<IReadOnlyDictionary<string, obj | null>>,
    rowCount: int,
    duration: TimeSpan,
    diagnostics: IReadOnlyList<string>) =
    member _.Rows = rows
    member _.RowCount = rowCount
    member _.Duration = duration
    member _.Diagnostics = diagnostics


type ISqlCommandExecutor =
    abstract ExecuteQueryAsync:
        CompiledSqlCommand *
        DbConnection *
        int *
        CancellationToken -> Task<QueryExecutionResult>
