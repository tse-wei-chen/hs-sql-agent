namespace HsSqlAgent.SqlCore.Rewrite

open System
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.Typestate

module internal RewritePolicy =

    type ExecutableSql = private ExecutableSql of Document * TargetRuntime

    module Executable =
        let internal value (ExecutableSql(document, _)) = document
        let internal targetRuntime (ExecutableSql(_, targetRuntime)) = targetRuntime

        /// Only a validated plan can be authorized into an executable token.
        let internal create (validated: RewriteStages.ValidatedSql) statement =
            let document = RewriteStages.Validated.value validated
            ExecutableSql({ document with Statement = statement }, RewriteStages.Validated.targetRuntime validated)

    type MutationSafety = RequirePredicate | AllowAllRows
    type RowCap = Unlimited | MaxRows of PositiveRowCount

    type ExecutionPolicy =
        { UpdateSafety: MutationSafety
          DeleteSafety: MutationSafety
          QueryRows: RowCap }
        member this.AllowUnboundedUpdate = this.UpdateSafety = AllowAllRows
        member this.AllowUnboundedDelete = this.DeleteSafety = AllowAllRows
        member this.QueryMaxRows = match this.QueryRows with Unlimited -> 0 | MaxRows count -> PositiveRowCount.value count

    let safeDefaults = { UpdateSafety = RequirePredicate; DeleteSafety = RequirePredicate; QueryRows = Unlimited }

    let private diagnosticDataKey = "HsSqlAgent.SqlCore.Diagnostic"

    let internal deny code (span: Span) message : 'T =
        let diagnosticSpan =
            if span.Start < 0 || span.Length < 0 then null
            else SqlDiagnosticSpan(span.Start, span.Length)
        let diagnostic =
            SqlDiagnostic(
                code,
                SqlDiagnosticStage.Policy,
                SqlDiagnosticCategory.Policy,
                message,
                diagnosticSpan)
        let error = UnauthorizedAccessException(message)
        error.Data[diagnosticDataKey] <- diagnostic
        raise error


