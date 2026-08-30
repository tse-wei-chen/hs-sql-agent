namespace HsSqlAgent.SqlCore.Rewrite

open HsSqlAgent.SqlCore.Rewrite.CoreModel

/// Unforgeable compiler-stage wrappers. Construction is intentionally centralized here.
module internal Typestate =

    type CapabilityProof =
        | ProvenCapability
        | RejectedCapability of string

    type JoinProofs =
        { RightJoin: CapabilityProof
          FullJoin: CapabilityProof }

    type ExpressionProofs =
        { ILike: CapabilityProof }

    type ColumnSetAssurance =
        | MissingAssurance
        | AssuredColumns of string list

    type ConflictProofs =
        { FirebirdPrimaryKey: ColumnSetAssurance
          SourceRowsUniqueByInsertColumns: ColumnSetAssurance }

    type SqlServerConcatLowering =
        | NativePipes
        | PlusOperator

    type SqlServerConcatCapability =
        | Proven of SqlServerConcatLowering
        | Unproven of string

    type TargetRuntime =
        | PostgreSqlRuntime
        | MySqlRuntime
        | SqlServerRuntime of SqlServerConcatCapability
        | SQLiteRuntime
        | OracleRuntime
        | FirebirdRuntime

    type ParsedSql = private ParsedSql of Document
    type BoundSql = private BoundSql of Document
    type CanonicalSql = private CanonicalSql of Document
    type ValidatedSql = private ValidatedSql of Document * TargetRuntime
    type ExecutableSql = private ExecutableSql of Document * TargetRuntime

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
        let internal create document targetRuntime = ValidatedSql(document, targetRuntime)
        let internal value (ValidatedSql(document, _)) = document
        let internal targetRuntime (ValidatedSql(_, targetRuntime)) = targetRuntime

    module Executable =
        let internal create document targetRuntime = ExecutableSql(document, targetRuntime)
        let internal value (ExecutableSql(document, _)) = document
        let internal targetRuntime (ExecutableSql(_, targetRuntime)) = targetRuntime

    /// Stage transitions accept only the immediately preceding typestate.
    module Transition =
        let bind transform parsed =
            parsed |> Parsed.value |> transform |> Bound.create

        let normalize transform bound =
            bound |> Bound.value |> transform |> Canonical.create

        let validate targetRuntime transform canonical =
            canonical
            |> Canonical.value
            |> transform
            |> fun document -> Validated.create document targetRuntime

        let authorize transform validated =
            let targetRuntime = Validated.targetRuntime validated
            validated
            |> Validated.value
            |> transform
            |> fun document -> Executable.create document targetRuntime
