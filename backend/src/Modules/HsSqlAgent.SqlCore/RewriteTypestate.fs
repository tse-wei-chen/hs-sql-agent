namespace HsSqlAgent.SqlCore.Rewrite

open System
open System.Runtime.CompilerServices
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Rewrite.CoreModel

/// Unforgeable compiler-stage wrappers. Construction is intentionally centralized here.
module internal Typestate =

    type CapabilitySide =
        | SourceCapability
        | TargetCapability

    type CapabilityRejection =
        private
            { Side: CapabilitySide
              Message: string }

    module CapabilityRejection =
        let internal create side message =
            if String.IsNullOrWhiteSpace(message) then
                invalidArg "message" "Capability rejection message cannot be empty."
            { Side = side
              Message = message }

        let internal side rejection = rejection.Side
        let internal message rejection = rejection.Message

    type CapabilityProof =
        | ProvenCapability
        | RejectedCapability of CapabilityRejection

    type JoinProofs =
        { RightJoin: CapabilityProof
          FullJoin: CapabilityProof }

    type SourceOrderingProofs =
        { NullsFirst: CapabilityProof
          NullsLast: CapabilityProof }

    type TargetNullOrdering =
        | NativeNullOrdering
        | RewriteNullOrdering

    type FilterPredicateProofs =
        { OuterReference: CapabilityProof
          Subquery: CapabilityProof
          WindowFunction: CapabilityProof }

    type ExpressionProofs =
        { ILike: CapabilityProof
          DistinctFrom: CapabilityProof
          IntervalLiteral: CapabilityProof
          RegexMatch: CapabilityProof
          AggregateFilter: CapabilityProof
          QuotedFunction: CapabilityProof
          QualifiedFunction: CapabilityProof
          OffsetTimestamp: CapabilityProof
          FirebirdTimeZoneType: CapabilityProof
          FirebirdExtendedDecimal: CapabilityProof
          StandaloneTime: CapabilityProof
          FilterPredicate: FilterPredicateProofs }

    type DmlProofs =
        { Returning: CapabilityProof
          ReturningExpression: CapabilityProof
          TargetAlias: CapabilityProof
          UpdateFrom: CapabilityProof
          DeleteUsing: CapabilityProof }

    type ColumnSetAssurance =
        | MissingAssurance
        | AssuredColumns of string list

    type MySqlUniqueKeyAssurance =
        | MissingMySqlUniqueKeyAssurance
        | AssuredMySqlUniqueKey of columns: string list * isSoleEnforcedUniqueKey: bool

    type ConflictProofs =
        { SourceProvider: SqlAgentToolType
          DirectTarget: CapabilityProof
          MySqlConditionalTarget: CapabilityProof
          FirebirdPrimaryKey: ColumnSetAssurance
          MySqlUniqueKey: MySqlUniqueKeyAssurance
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

    module TargetRuntime =
        let provider = function
            | PostgreSqlRuntime -> SqlAgentToolType.Postgres
            | MySqlRuntime -> SqlAgentToolType.MySQL
            | SqlServerRuntime _ -> SqlAgentToolType.MsSqlServer
            | SQLiteRuntime -> SqlAgentToolType.Sqlite
            | OracleRuntime -> SqlAgentToolType.Oracle
            | FirebirdRuntime -> SqlAgentToolType.Firebird

    type ParsedSql = private ParsedSql of Document
    type BoundSql = private BoundSql of Document
    type CanonicalSql = private CanonicalSql of Document

    module Parsed =
        let private sourceSpans = ConditionalWeakTable<obj, StrongBox<Span>>()

        let internal rememberSpan (node: obj | null) span =
            match node with
            | null -> ()
            | value ->
                sourceSpans.Remove(value) |> ignore
                sourceSpans.Add(value, StrongBox<Span>(span))

        let internal trySpan (node: obj | null) =
            match node with
            | null -> None
            | value ->
                match sourceSpans.TryGetValue(value) with
                | true, span -> Some span.Value
                | _ -> None

        let internal create document = ParsedSql document
        let internal value (ParsedSql document) = document

    module Bound =
        let internal create document = BoundSql document
        let internal value (BoundSql document) = document

    module Canonical =
        let internal create document = CanonicalSql document
        let internal value (CanonicalSql document) = document

    /// Stage transitions accept only the immediately preceding typestate.
    module Transition =
        let bind transform parsed =
            parsed |> Parsed.value |> transform |> Bound.create

        let normalize transform bound =
            bound |> Bound.value |> transform |> Canonical.create
