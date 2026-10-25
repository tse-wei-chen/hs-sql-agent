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
        (items |> Seq.toArray |> Array.AsReadOnly) :> IReadOnlyList<'T>

    let private diagnostic (syntax: SqlScriptStatementSyntax) code category message =
        SqlDiagnostic(code, SqlDiagnosticStage.SemanticValidation, category, message, syntax.Span)

    let private changed (syntax: SqlScriptStatementSyntax) name operation certainty =
        SqlCatalogChange(name, operation, certainty, syntax.Span)

    let private analyzeDdl sourceDialect (sourceProfile: SqlProviderCapabilityProfile | null)
                           (state: SqlCatalogState) (syntax: SqlScriptStatementSyntax) =
        let tokens =
            RewriteResourceBudget.withStage "SchemaLexical" SqlDiagnosticStage.Lexical (fun () ->
                RewriteFacadeAdapter.tokenizeSourceValidated syntax.Text sourceDialect sourceProfile)
            |> List.toArray
        let parsed =
            RewriteResourceBudget.withStage "SchemaParsing" SqlDiagnosticStage.Parse (fun () ->
                RewriteSchemaAnalyzer.parseForWith sourceDialect sourceProfile tokens)
        let fail message =
            let error = diagnostic syntax "SQL_SCHEMA_SEMANTIC_INVALID" SqlDiagnosticCategory.Semantic message
            SqlStatementAnalysisStatus.Invalid, SqlCatalogKnowledge.Known,
            state, ([]: SqlCatalogChange list), [error]
        let partial name operation =
            let next = RewriteCatalogModel.unknownTable state name
            SqlStatementAnalysisStatus.PartiallyAnalyzed, SqlCatalogKnowledge.Unknown,
            next, [changed syntax name operation SqlCatalogKnowledge.Unknown], []
        let noChange =
            SqlStatementAnalysisStatus.Analyzed, SqlCatalogKnowledge.Known,
            state, ([]: SqlCatalogChange list), ([]: SqlDiagnostic list)
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
                    "Source dialect DDL is outside the statically proven TABLE subset; catalog facts are invalidated."
            SqlStatementAnalysisStatus.Unsupported, SqlCatalogKnowledge.Unknown,
            unknown, [changed syntax "*" SqlCatalogOperation.Indeterminate SqlCatalogKnowledge.Unknown], [error]
        | Recognized (Create(name, columns, ifNotExists)) ->
            match RewriteCatalogModel.lookup state name with
            | Some existing when existing.Knowledge = SqlCatalogKnowledge.Known && ifNotExists ->
                noChange
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
                noChange
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
                    noChange
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
            let mutable transaction = RewriteTransactionAnalyzer.Outside

            RewriteResourceBudget.withStage "ScriptAnalysis" SqlDiagnosticStage.SemanticValidation (fun () ->
                for syntax in parsed.Statements do
                    RewriteResourceBudget.checkpoint ()
                    let before = current
                    let transactionBefore = RewriteTransactionAnalyzer.status transaction
                    let status, effects, certainty, bindingStatus, nameStatus, typeStatus, next, changes, diagnostics =
                        if syntax.Status = SqlScriptParseStatus.Invalid then
                            SqlStatementAnalysisStatus.Invalid, SqlEffectKind.None,
                            SqlCatalogKnowledge.Unknown, SqlCatalogBindingStatus.Rejected,
                            SqlCatalogBindingStatus.Rejected, SqlCatalogBindingStatus.NotApplicable, current, [],
                            (if Object.ReferenceEquals(syntax.Diagnostic, null) then []
                             else [syntax.Diagnostic])
                        elif syntax.Kind = SqlScriptStatementKind.Transaction then
                            let status, certainty, next, newTransaction, changes, diagnostics =
                                RewriteTransactionAnalyzer.analyze sourceDialect sourceProfile current transaction syntax
                            transaction <- newTransaction
                            let effects =
                                if changes.IsEmpty then SqlEffectKind.ChangeTransaction
                                else SqlEffectKind.ChangeTransaction ||| SqlEffectKind.ChangeSchema
                            status, effects, certainty, SqlCatalogBindingStatus.NotApplicable,
                            SqlCatalogBindingStatus.NotApplicable, SqlCatalogBindingStatus.NotApplicable,
                            next, changes, diagnostics
                        elif syntax.Kind = SqlScriptStatementKind.Schema then
                            let status, certainty, next, changes, diagnostics =
                                analyzeDdl sourceDialect sourceProfile current syntax
                            status, SqlEffectKind.ChangeSchema, certainty, SqlCatalogBindingStatus.NotApplicable,
                            SqlCatalogBindingStatus.NotApplicable, SqlCatalogBindingStatus.NotApplicable,
                            next, changes, diagnostics
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
                            let bindingStatus, nameStatus, typeStatus, status, certainty, catalogErrors =
                                if syntax.Status = SqlScriptParseStatus.Parsed
                                   && (syntax.Kind = SqlScriptStatementKind.Query
                                       || syntax.Kind = SqlScriptStatementKind.Mutation) then
                                    RewriteCatalogBinding.verify sourceDialect sourceProfile current syntax
                                else
                                    SqlCatalogBindingStatus.NotApplicable,
                                    SqlCatalogBindingStatus.NotApplicable,
                                    SqlCatalogBindingStatus.NotApplicable,
                                    SqlStatementAnalysisStatus.Unsupported,
                                    SqlCatalogKnowledge.Unknown,
                                    []
                            let next =
                                if syntax.Kind = SqlScriptStatementKind.Session
                                   || syntax.Kind = SqlScriptStatementKind.Procedural
                                   || syntax.Kind = SqlScriptStatementKind.Unknown then
                                    RewriteCatalogModel.taintAll current
                                else current
                            let diagnostics =
                                let parserDiagnostics =
                                    if Object.ReferenceEquals(syntax.Diagnostic, null) then []
                                    else [syntax.Diagnostic]
                                parserDiagnostics @ catalogErrors
                            status, effects, certainty, bindingStatus, nameStatus, typeStatus, next, [], diagnostics
                    let nextTransaction =
                        RewriteTransactionAnalyzer.invalidateIfUnproven transaction syntax.Kind status
                    let stateBecameUnknown =
                        transactionBefore = SqlTransactionState.Active
                        && RewriteTransactionAnalyzer.status nextTransaction = SqlTransactionState.Unknown
                    transaction <- nextTransaction
                    let after =
                        if stateBecameUnknown then RewriteCatalogModel.taintAll next else next
                    let additionalErrors =
                        if stateBecameUnknown then
                            [diagnostic syntax "SQL_TRANSACTION_CONTEXT_UNPROVEN" SqlDiagnosticCategory.Capability
                                "An unproven statement or failure has invalidated the active transaction and Catalog proofs."]
                        else []
                    let allDiagnostics = diagnostics @ additionalErrors
                    current <- after
                    for item in allDiagnostics do errors.Add item
                    results.Add(
                        SqlStatementAnalysis(
                            syntax, status, effects,
                            (if stateBecameUnknown then SqlCatalogKnowledge.Unknown else certainty),
                            bindingStatus, nameStatus, typeStatus, before, after,
                            transactionBefore, RewriteTransactionAnalyzer.status transaction,
                            readOnly changes, readOnly allDiagnostics)))
            SqlScriptAnalysisResult(
                readOnly results, readOnly errors, current,
                RewriteTransactionAnalyzer.status transaction))
