namespace HsSqlAgent.SqlCore.Script

open System
open System.Collections.Generic
open HsSqlAgent.SqlCore.Core.Compilation

/// Statement families recognized by the shared SQL script frontend.
/// These are syntax classifications, not execution permissions or capability proofs.
type SqlScriptStatementKind =
    | Query = 0
    | Mutation = 1
    | Schema = 2
    | Security = 3
    | Transaction = 4
    | Session = 5
    | Utility = 6
    | Procedural = 7
    | Unknown = 8

/// Parsing is intentionally weaker than semantic validation or compilation.
type SqlScriptParseStatus =
    | Parsed = 0
    | Unsupported = 1
    | Invalid = 2

/// CLR-only projection of a single source statement. The compiler never accepts this
/// type as evidence of semantic validation or authorization.
[<Sealed>]
type SqlScriptStatementSyntax internal (
    kind: SqlScriptStatementKind,
    status: SqlScriptParseStatus,
    batchIndex: int,
    span: SqlDiagnosticSpan,
    text: string,
    diagnostic: SqlDiagnostic | null) =

    member _.Kind = kind
    member _.Status = status
    member _.BatchIndex = batchIndex
    member _.Span = span
    member _.Text = text
    member _.Diagnostic = diagnostic

[<Sealed>]
type SqlScriptParseResult internal (
    batchCount: int,
    statements: IReadOnlyList<SqlScriptStatementSyntax>,
    diagnostics: IReadOnlyList<SqlDiagnostic>) =

    member _.BatchCount = batchCount
    member _.Statements = statements
    member _.Diagnostics = diagnostics
