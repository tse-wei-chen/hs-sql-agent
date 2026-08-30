namespace HsSqlAgent.SqlCore.Rewrite

open System
open System.Runtime.CompilerServices
open HsSqlAgent.SqlCore.Rewrite.CoreModel

/// Unforgeable compiler-stage wrappers. Construction is intentionally centralized here.
module internal Typestate =

    type CapabilityProof =
        | ProvenCapability
        | RejectedCapability of string

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
          IntervalLiteral: CapabilityProof
          RegexMatch: CapabilityProof
          AggregateFilter: CapabilityProof
          OffsetTimestamp: CapabilityProof
          FirebirdTimeZoneType: CapabilityProof
          FirebirdExtendedDecimal: CapabilityProof
          StandaloneTime: CapabilityProof
          FilterPredicate: FilterPredicateProofs }

    type DmlProofs =
        { Returning: CapabilityProof
          ReturningExpression: CapabilityProof
          UpdateFrom: CapabilityProof
          DeleteUsing: CapabilityProof }

    type ColumnSetAssurance =
        | MissingAssurance
        | AssuredColumns of string list

    type MySqlUniqueKeyAssurance =
        | MissingMySqlUniqueKeyAssurance
        | AssuredMySqlUniqueKey of columns: string list * isSoleEnforcedUniqueKey: bool

    type ConflictProofs =
        { DirectTarget: CapabilityProof
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

    type ParsedSql = private ParsedSql of Document
    type BoundSql = private BoundSql of Document
    type CanonicalSql = private CanonicalSql of Document

    module Parsed =
        let private sourceSpans = ConditionalWeakTable<obj, StrongBox<Span>>()

        let internal rememberSpan (node: obj) span =
            if not (isNull node) then
                sourceSpans.Remove(node) |> ignore
                sourceSpans.Add(node, StrongBox<Span>(span))

        let internal trySpan (node: obj) =
            if isNull node then None
            else
                match sourceSpans.TryGetValue(node) with
                | true, value -> Some value.Value
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
