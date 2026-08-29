namespace HsSqlAgent.SqlCore.Rewrite

open HsSqlAgent.SqlCore.Rewrite.CoreModel

/// Unforgeable compiler-stage wrappers. Construction is intentionally centralized here.
module internal Typestate =

    type ParsedSql = private ParsedSql of Document
    type BoundSql = private BoundSql of Document
    type CanonicalSql = private CanonicalSql of Document
    type ValidatedSql = private ValidatedSql of Document
    type ExecutableSql = private ExecutableSql of Document

    module Parsed =
        let internal create document = ParsedSql document
        let internal value (ParsedSql document) = document

    module Bound =
        let internal create document = BoundSql document
        let internal value (BoundSql document) = document

    module Canonical =
        let internal create document = CanonicalSql document
        let internal value (CanonicalSql document) = document

    module Validated =
        let internal create document = ValidatedSql document
        let internal value (ValidatedSql document) = document

    module Executable =
        let internal create document = ExecutableSql document
        let internal value (ExecutableSql document) = document

    /// Stage transitions accept only the immediately preceding typestate.
    module Transition =
        let bind transform parsed =
            parsed |> Parsed.value |> transform |> Bound.create

        let normalize transform bound =
            bound |> Bound.value |> transform |> Canonical.create

        let validate transform canonical =
            canonical |> Canonical.value |> transform |> Validated.create

        let authorize transform validated =
            validated |> Validated.value |> transform |> Executable.create
