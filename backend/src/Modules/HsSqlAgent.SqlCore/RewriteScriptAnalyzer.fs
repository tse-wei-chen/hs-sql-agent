namespace HsSqlAgent.SqlCore.Rewrite

open System
open System.Collections.Generic
open HsSqlAgent.SqlCore.Catalog
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.Script
open HsSqlAgent.SqlCore.Rewrite.RewriteSchemaAnalyzer

/// Statement-order coordinator. A Catalog is simulated, not queried or
/// executed; neither successful analysis nor a known Catalog authorizes SQL.
module internal RewriteScriptAnalyzer =

    let private readOnly (items: seq<'T>) : IReadOnlyList<'T> =
        items |> Seq.toArray |> Array.AsReadOnly

    let private diagnostic (syntax: SqlScriptStatementSyntax) code category message =
        SqlDiagnostic(code, SqlDiagnosticStage.SemanticValidation, category, message, syntax.Span)

    let private changed (syntax: SqlScriptStatementSyntax) name operation certainty =
        SqlCatalogChange(name, operation, certainty, syntax.Span)

    let private analyzePostgresDdl (state: SqlCatalogState) (syntax: SqlScriptStatementSyntax) =
        let tokens =
            RewriteResourceBudget.withStage "SchemaLexical" SqlDiagnosticStage.Lexical (fun () ->
                RewriteFacadeAdapter.tokenizeSourceValidated syntax.Text SqlAgentToolType.Postgres null)
            |> List.toArray
        let parsed =
            RewriteResourceBudget.withStage "SchemaParsing" SqlDiagnosticStage.Parse (fun () ->
                RewriteSchemaAnalyzer.parse tokens)
        let fail message =
            let error = diagnostic syntax "SQL_SCHEMA_SEMANTIC_INVALID" SqlDiagnosticCategory.Semantic message
            SqlStatementAnalysisStatus.Invalid, SqlCatalogKnowledge.Known,
            state, ([]: SqlCatalogChange list), [error]
        let partial name operation =
            let next = RewriteCatalogModel.unknownTable state name
            SqlStatementAnalysisStatus.PartiallyAnalyzed, SqlCatalogKnowledge.Unknown,
            next, [changed syntax name operation SqlCatalogKnowledge.Unknown], []
        let exact name operation next =
            SqlStatementAnalysisStatus.Analyzed, SqlCatalogKnowledge.Known,
            next, [changed syntax name operation SqlCatalogKnowledge.Known], []
        match parsed with
        | Invalid message -> fail message
        | Unsupported ->
            let unknown = RewriteCatalogModel.taintAll state
            let error =
                diagnostic syntax "SQL_SCHEMA_UNSUPPORTED_STATEMENT"
                    SqlDiagnosticCategory.Capability
                    "PostgreSQL DDL is outside the statically proven TABLE subset; catalog facts are invalidated."
            SqlStatementAnalysisStatus.Unsupported, SqlCatalogKnowledge.Unknown,
            unknown, [changed syntax "*" SqlCatalogOperation.Indeterminate SqlCatalogKnowledge.Unknown], [error]
        | Recognized (Create(name, columns, ifNotExists)) ->
            match RewriteCatalogModel.lookup state name with
            | Some existing when existing.Knowledge = SqlCatalogKnowledge.Known && ifNotExists ->
                exact name SqlCatalogOperation.CreateTable state
            | Some existing when existing.Knowledge = SqlCatalogKnowledge.Known ->
                fail ("CREATE TABLE " + name + " already exists in the known catalog.")
            | _ when RewriteCatalogModel.isKnownAbsent state name ->
                RewriteCatalogModel.knownTable state name columns
                |> exact name SqlCatalogOperation.CreateTable
            | _ -> partial name SqlCatalogOperation.CreateTable
        | Recognized (Drop(name, ifExists)) ->
            match RewriteCatalogModel.lookup state name with
            | Some existing when existing.Knowledge = SqlCatalogKnowledge.Known ->
                RewriteCatalogModel.dropKnown state name
                |> exact name SqlCatalogOperation.DropTable
            | _ when RewriteCatalogModel.isKnownAbsent state name && ifExists ->
                exact name SqlCatalogOperation.DropTable state
            | _ when RewriteCatalogModel.isKnownAbsent state name ->
                fail ("DROP TABLE " + name + " does not exist in the known catalog.")
            | _ -> partial name SqlCatalogOperation.DropTable
        | Recognized (AddColumn(name, column)) ->
            match RewriteCatalogModel.lookup state name with
            | Some table when table.Knowledge = SqlCatalogKnowledge.Known ->
                if table.Columns |> Seq.exists (fun c -> String.Equals(c.Name, column.Name, StringComparison.Ordinal)) then
                    fail ("ALTER TABLE " + name + " column " + column.Name + " already exists.")
                else
                    seq {
                        yield! table.Columns
                        yield column
                    }
                    |> RewriteCatalogModel.knownTable state name
                    |> exact name SqlCatalogOperation.AddColumn
            | _ when RewriteCatalogModel.isKnownAbsent state name ->
                fail ("ALTER TABLE " + name + " does not exist in the known catalog.")
            | _ -> partial name SqlCatalogOperation.AddColumn
        | Recognized (RemoveColumn(name, columnName, ifExists)) ->
            match RewriteCatalogModel.lookup state name with
            | Some table when table.Knowledge = SqlCatalogKnowledge.Known ->
                let exists = table.Columns |> Seq.exists (fun c -> String.Equals(c.Name, columnName, StringComparison.Ordinal))
                if not exists && not ifExists then
                    fail ("ALTER TABLE " + name + " column " + columnName + " does not exist.")
                elif not exists then
                    exact name SqlCatalogOperation.DropColumn state
                else
                    table.Columns
                    |> Seq.filter (fun c -> not (String.Equals(c.Name, columnName, StringComparison.Ordinal)))
                    |> RewriteCatalogModel.knownTable state name
                    |> exact name SqlCatalogOperation.DropColumn
            | _ when RewriteCatalogModel.isKnownAbsent state name ->
                fail ("ALTER TABLE " + name + " does not exist in the known catalog.")
            | _ -> partial name SqlCatalogOperation.DropColumn

    let analyze sql sourceDialect (sourceProfile: SqlProviderCapabilityProfile | null) (initial: SqlCatalogState) =
        if Object.ReferenceEquals(initial, null) then nullArg "initialCatalog"
        RewriteResourceBudget.withCompilation (fun () ->
            let parsed = RewriteScriptParser.parse sql sourceDialect sourceProfile
            let results = ResizeArray<SqlStatementAnalysis>()
            let errors = ResizeArray<SqlDiagnostic>()
            let mutable current = initial

            RewriteResourceBudget.withStage "ScriptAnalysis" SqlDiagnosticStage.SemanticValidation (fun () ->
                for syntax in parsed.Statements do
                    RewriteResourceBudget.checkpoint ()
                    let before = current
                    let status, effects, certainty, next, changes, diagnostics =
                        if syntax.Status = SqlScriptParseStatus.Invalid then
                            SqlStatementAnalysisStatus.Invalid, SqlEffectKind.None,
                            SqlCatalogKnowledge.Unknown, current, [],
                            (if Object.ReferenceEquals(syntax.Diagnostic, null) then []
                             else [syntax.Diagnostic])
                        elif syntax.Kind = SqlScriptStatementKind.Schema then
                            let status, certainty, next, changes, diagnostics =
                                if sourceDialect = SqlAgentToolType.Postgres then
                                    analyzePostgresDdl current syntax
                                else
                                    let warning =
                                        diagnostic syntax "SQL_SCHEMA_DIALECT_UNSUPPORTED"
                                            SqlDiagnosticCategory.Capability
                                            "This source dialect has no proven DDL catalog analyzer."
                                    SqlStatementAnalysisStatus.Unsupported, SqlCatalogKnowledge.Unknown,
                                    RewriteCatalogModel.taintAll current,
                                    [changed syntax "*" SqlCatalogOperation.Indeterminate SqlCatalogKnowledge.Unknown],
                                    [warning]
                            status, SqlEffectKind.ChangeSchema, certainty, next, changes, diagnostics
                        else
                            let effects =
                                match syntax.Kind with
                                | SqlScriptStatementKind.Query -> SqlEffectKind.ReadData
                                | SqlScriptStatementKind.Mutation -> SqlEffectKind.WriteData
                                | SqlScriptStatementKind.Security -> SqlEffectKind.ChangePermissions
                                | SqlScriptStatementKind.Transaction -> SqlEffectKind.ChangeTransaction
                                | SqlScriptStatementKind.Session -> SqlEffectKind.ChangeSession
                                | SqlScriptStatementKind.Utility -> SqlEffectKind.Administrative
                                | _ -> SqlEffectKind.None
                            let status =
                                match syntax.Status with
                                | SqlScriptParseStatus.Parsed -> SqlStatementAnalysisStatus.PartiallyAnalyzed
                                | _ -> SqlStatementAnalysisStatus.Unsupported
                            let next =
                                if syntax.Kind = SqlScriptStatementKind.Session
                                   || syntax.Kind = SqlScriptStatementKind.Procedural
                                   || syntax.Kind = SqlScriptStatementKind.Unknown then
                                    RewriteCatalogModel.taintAll current
                                else current
                            let diagnostics =
                                if Object.ReferenceEquals(syntax.Diagnostic, null) then []
                                else [syntax.Diagnostic]
                            status, effects, SqlCatalogKnowledge.Unknown, next, [], diagnostics
                    current <- next
                    for item in diagnostics do errors.Add item
                    results.Add(
                        SqlStatementAnalysis(
                            syntax, status, effects, certainty, before, next,
                            readOnly changes, readOnly diagnostics)))
            SqlScriptAnalysisResult(readOnly results, readOnly errors, current))
