namespace HsSqlAgent.SqlCore.Rewrite

open System
open System.Buffers
open System.Collections.Generic
open System.Collections.Immutable
open System.Globalization
open System.Security.Cryptography
open System.Text
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Core.Pipeline
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models

module internal CompileEvidenceBuilder =

    let schemaVersion = "2026-09-02.1"

    type Context =
        private
            { SourceProfile: SqlCompileProfileEvidence
              TargetProfile: SqlCompileProfileEvidence
              SourceCapabilities: ImmutableArray<SqlCompileCapabilityEvidence>
              TargetCapabilities: ImmutableArray<SqlCompileCapabilityEvidence>
              Policy: SqlCompilePolicyEvidence
              Assurances: ImmutableArray<SqlCompileAssuranceEvidence> }

    let private ordinalIgnoreCaseCompare (left: string) (right: string) =
        let insensitive = StringComparer.OrdinalIgnoreCase.Compare(left, right)
        if insensitive <> 0 then insensitive else StringComparer.Ordinal.Compare(left, right)

    let private sortedStrings (values: seq<string>) =
        values
        |> Seq.sortWith ordinalIgnoreCaseCompare
        |> ImmutableArray.CreateRange

    let private settings (values: seq<string * string>) =
        values
        |> Seq.sortWith (fun (leftName, leftValue) (rightName, rightValue) ->
            let nameComparison = ordinalIgnoreCaseCompare leftName rightName
            if nameComparison <> 0 then nameComparison
            else StringComparer.Ordinal.Compare(leftValue, rightValue))
        |> Seq.map (fun (name, value) -> SqlCompileSettingEvidence(name, value))
        |> ImmutableArray.CreateRange

    let private relevantSessionModes provider (profile: SqlProviderCapabilityProfile) =
        match provider with
        | SqlAgentToolType.MySQL ->
            [ "ANSI"; "ANSI_QUOTES"; "NO_BACKSLASH_ESCAPES"; "PIPES_AS_CONCAT" ]
            |> Seq.filter profile.HasSessionMode
            |> sortedStrings
        | _ -> ImmutableArray<string>.Empty

    let private relevantSessionSettings provider (profile: SqlProviderCapabilityProfile) =
        match provider with
        | SqlAgentToolType.MsSqlServer ->
            [ "CONCAT_NULL_YIELDS_NULL" ]
            |> Seq.choose (fun name ->
                match profile.GetSessionSetting(name) with
                | null -> None
                | value -> Some(name, value))
            |> settings
        | _ -> ImmutableArray<SqlCompileSettingEvidence>.Empty

    let private profileEvidence provider (profile: SqlProviderCapabilityProfile | null) =
        let serverVersion, compatibilityLevel, sessionModes, sessionSettings =
            match profile with
            | null ->
                null,
                Nullable<int>(),
                ImmutableArray<string>.Empty,
                ImmutableArray<SqlCompileSettingEvidence>.Empty
            | value ->
                let version =
                    match value.ServerVersion with
                    | null -> null
                    | version -> version.ToString()
                version,
                value.CompatibilityLevel,
                relevantSessionModes provider value,
                relevantSessionSettings provider value

        SqlCompileProfileEvidence(
            provider,
            serverVersion,
            compatibilityLevel,
            sessionModes,
            sessionSettings)

    let private capabilityStatus = function
        | SqlCapabilityStatus.Supported -> SqlCompileCapabilityStatus.Supported
        | SqlCapabilityStatus.Translated -> SqlCompileCapabilityStatus.Translated
        | SqlCapabilityStatus.Rejected -> SqlCompileCapabilityStatus.Rejected
        | value -> invalidOp ("Unknown capability status '" + string value + "'.")

    let private capabilityEvidence side provider (profile: SqlProviderCapabilityProfile | null) =
        SqlCapabilityMatrix.ForProvider(provider, profile).Capabilities
        |> Seq.sortWith (fun left right ->
            let idComparison = ordinalIgnoreCaseCompare left.Id right.Id
            if idComparison <> 0 then idComparison
            else ordinalIgnoreCaseCompare left.Category right.Category)
        |> Seq.map (fun capability ->
            SqlCompileCapabilityEvidence(
                side,
                capability.Id,
                capability.Category,
                capabilityStatus capability.Status,
                capability.Detail))
        |> ImmutableArray.CreateRange

    let private joinColumns (columns: ImmutableArray<string>) =
        if columns.IsDefaultOrEmpty then String.Empty
        else String.Join("|", columns)

    let private nonNullText (value: string | null) =
        match value with
        | null -> String.Empty
        | nonNull -> nonNull

    let private conflictAssuranceEvidence (assurance: DmlConflictTargetAssurance) =
        let details =
            [
                "primaryKeyColumns", joinColumns assurance.PrimaryKeyColumns
                "matchedUniqueKeyColumns", joinColumns assurance.MatchedUniqueKeyColumns
                "matchedUniqueKeyName", nonNullText assurance.MatchedUniqueKeyName
                "matchedUniqueKeyIsPrimaryKey", assurance.MatchedUniqueKeyIsPrimaryKey.ToString(CultureInfo.InvariantCulture)
                "enforcedUniqueKeyCount", assurance.EnforcedUniqueKeyCount.ToString(CultureInfo.InvariantCulture)
                "hasUnsupportedEnforcedUniqueKeys", assurance.HasUnsupportedEnforcedUniqueKeys.ToString(CultureInfo.InvariantCulture)
                "isSoleEnforcedUniqueKey", assurance.IsSoleEnforcedUniqueKey.ToString(CultureInfo.InvariantCulture)
                "sourceRowsUniqueByInsertColumns", joinColumns assurance.SourceRowsUniqueByInsertColumns
            ]
            |> settings
        SqlCompileAssuranceEvidence("dml.conflict_target", details)

    let private resultRowAssuranceEvidence (assurance: DmlResultRowAssurance) =
        let details =
            [
                "targetTable", assurance.TargetTable
                "operation", assurance.Operation.ToString()
            ]
            |> settings
        SqlCompileAssuranceEvidence("dml.result_rows", details)

    let private assuranceEvidence
        (conflictTargetAssurance: DmlConflictTargetAssurance | null)
        (resultRowAssurance: DmlResultRowAssurance | null) =

        [
            match conflictTargetAssurance with
            | null -> ()
            | value -> yield conflictAssuranceEvidence value
            match resultRowAssurance with
            | null -> ()
            | value -> yield resultRowAssuranceEvidence value
        ]
        |> Seq.sortBy (fun evidence -> evidence.Kind)
        |> ImmutableArray.CreateRange

    let create
        sourceProvider
        targetProvider
        (sourceProfile: SqlProviderCapabilityProfile | null)
        (targetProfile: SqlProviderCapabilityProfile | null)
        (conflictTargetAssurance: DmlConflictTargetAssurance | null)
        (resultRowAssurance: DmlResultRowAssurance | null)
        policyVersion
        queryMaxRows
        requireUpdatePredicate
        requireDeletePredicate
        (allowedTables: string list option) =

        let allowed =
            match allowedTables with
            | None -> ImmutableArray<string>.Empty
            | Some values -> values |> sortedStrings

        { SourceProfile = profileEvidence sourceProvider sourceProfile
          TargetProfile = profileEvidence targetProvider targetProfile
          SourceCapabilities =
            capabilityEvidence SqlCompileCapabilitySide.Source sourceProvider sourceProfile
          TargetCapabilities =
            capabilityEvidence SqlCompileCapabilitySide.Target targetProvider targetProfile
          Policy =
            SqlCompilePolicyEvidence(
                policyVersion,
                queryMaxRows,
                requireUpdatePredicate,
                requireDeletePredicate,
                allowed)
          Assurances =
            assuranceEvidence conflictTargetAssurance resultRowAssurance }

    type private Utf8HashWriter(hash: IncrementalHash) =
        let pool = ArrayPool<byte>.Shared
        let encoding = Encoding.UTF8
        let mutable buffer = pool.Rent(256)
        let mutable disposed = false

        member private _.EnsureCapacity(required: int) =
            if required > buffer.Length then
                let replacement = pool.Rent(required)
                pool.Return(buffer)
                buffer <- replacement

        member this.Append(value: string) =
            if disposed then
                raise (ObjectDisposedException("Utf8HashWriter"))
            if not (String.IsNullOrEmpty(value)) then
                let required = encoding.GetByteCount(value)
                this.EnsureCapacity(required)
                let written = encoding.GetBytes(value, 0, value.Length, buffer, 0)
                hash.AppendData(buffer, 0, written)

        member this.AppendToken(value: string | null) =
            match value with
            | null ->
                this.Append("-1:;")
            | nonNull ->
                this.Append(nonNull.Length.ToString(CultureInfo.InvariantCulture))
                this.Append(":")
                this.Append(nonNull)
                this.Append(";")

        member this.AppendInt(value: int) =
            this.AppendToken(value.ToString(CultureInfo.InvariantCulture))

        member this.AppendBool(value: bool) =
            this.AppendToken(if value then "1" else "0")

        interface IDisposable with
            member _.Dispose() =
                if not disposed then
                    disposed <- true
                    pool.Return(buffer)
                    buffer <- [||]

    let private appendProfile (writer: Utf8HashWriter) (profile: SqlCompileProfileEvidence) =
        writer.AppendInt(int profile.Provider)
        writer.AppendToken(profile.ServerVersion)
        writer.AppendToken(
            if profile.CompatibilityLevel.HasValue then
                profile.CompatibilityLevel.Value.ToString(CultureInfo.InvariantCulture)
            else (null: string | null))
        writer.AppendInt(profile.SessionModes.Length)
        profile.SessionModes |> Seq.iter writer.AppendToken
        writer.AppendInt(profile.SessionSettings.Length)
        profile.SessionSettings
        |> Seq.iter (fun item ->
            writer.AppendToken(item.Name)
            writer.AppendToken(item.Value))

    let private appendCapabilities
        (writer: Utf8HashWriter)
        (capabilities: ImmutableArray<SqlCompileCapabilityEvidence>) =
        writer.AppendInt(capabilities.Length)
        capabilities
        |> Seq.iter (fun capability ->
            writer.AppendInt(int capability.Side)
            writer.AppendToken(capability.Id)
            writer.AppendToken(capability.Category)
            writer.AppendInt(int capability.Status))

    let private appendPolicy (writer: Utf8HashWriter) (policy: SqlCompilePolicyEvidence) =
        writer.AppendToken(policy.PolicyVersion)
        writer.AppendInt(policy.QueryMaxRows)
        writer.AppendBool(policy.RequireUpdatePredicate)
        writer.AppendBool(policy.RequireDeletePredicate)
        writer.AppendInt(policy.AllowedTables.Length)
        policy.AllowedTables |> Seq.iter writer.AppendToken

    let private appendAssurances
        (writer: Utf8HashWriter)
        (assurances: ImmutableArray<SqlCompileAssuranceEvidence>) =
        writer.AppendInt(assurances.Length)
        assurances
        |> Seq.iter (fun assurance ->
            writer.AppendToken(assurance.Kind)
            writer.AppendInt(assurance.Details.Length)
            assurance.Details
            |> Seq.iter (fun detail ->
                writer.AppendToken(detail.Name)
                writer.AppendToken(detail.Value)))

    let private fingerprint context verdict decisionBoundary decisionCode =
        use hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
        use writer = new Utf8HashWriter(hash)
        writer.AppendToken(schemaVersion)
        writer.AppendToken(SqlCapabilityMatrix.Version)
        appendProfile writer context.SourceProfile
        appendProfile writer context.TargetProfile
        appendCapabilities writer context.SourceCapabilities
        appendCapabilities writer context.TargetCapabilities
        appendPolicy writer context.Policy
        appendAssurances writer context.Assurances
        writer.AppendInt(int verdict)
        writer.AppendInt(int decisionBoundary)
        writer.AppendToken(decisionCode)
        hash.GetHashAndReset()
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    let build context verdict decisionBoundary decisionCode (planFingerprint: string | null) =
        if String.IsNullOrWhiteSpace(decisionCode) then
            invalidArg "decisionCode" "Compile-evidence decision code cannot be empty."
        let normalizedPlanFingerprint =
            if String.IsNullOrWhiteSpace(planFingerprint) then null else planFingerprint
        SqlCompileEvidence(
            schemaVersion,
            SqlCapabilityMatrix.Version,
            context.SourceProfile,
            context.TargetProfile,
            context.SourceCapabilities,
            context.TargetCapabilities,
            context.Policy,
            context.Assurances,
            verdict,
            decisionBoundary,
            decisionCode,
            normalizedPlanFingerprint,
            fingerprint context verdict decisionBoundary decisionCode)

    let reclassify
        (evidence: SqlCompileEvidence)
        verdict
        decisionBoundary
        decisionCode
        (planFingerprint: string | null) =

        if Object.ReferenceEquals(evidence, null) then nullArg "evidence"
        let context =
            { SourceProfile = evidence.SourceProfile
              TargetProfile = evidence.TargetProfile
              SourceCapabilities = evidence.SourceCapabilities
              TargetCapabilities = evidence.TargetCapabilities
              Policy = evidence.Policy
              Assurances = evidence.Assurances }
        build context verdict decisionBoundary decisionCode planFingerprint
