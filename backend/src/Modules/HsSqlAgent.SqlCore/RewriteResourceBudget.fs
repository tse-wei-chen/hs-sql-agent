namespace HsSqlAgent.SqlCore.Rewrite

open System
open System.Diagnostics
open System.Threading
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.SqlParsing

/// An internal, synchronous compilation-scope resource contract. All measurements
/// are per attempt, not static global counters or an execution-query timeout.
/// Every checkpoint is amortized to avoid a clock syscall at each AST node.
module internal RewriteResourceBudget =
    [<Literal>]
    let MaxStageWork = 8000000L
    [<Literal>]
    let MaxStageElapsedSeconds = 30L
    [<Literal>]
    let MaxCompilationElapsedSeconds = 90L
    [<Literal>]
    let MaxStageAllocatedBytes = 536870912L // cumulative managed bytes, not RSS

    type private Phase(name: string, diagnosticStage: SqlDiagnosticStage) =
        let start = Stopwatch.GetTimestamp()
        let allocated = GC.GetAllocatedBytesForCurrentThread()
        member _.Name = name
        member _.DiagnosticStage = diagnosticStage
        member _.Started = start
        member _.AllocatedAtStart = allocated
        member val Work = 0L with get, set

    type private Session() =
        let started = Stopwatch.GetTimestamp()
        member _.Started = started
        member val Current: Phase option = None with get, set

    let private ambient = AsyncLocal<Session option>()
    let private events = DiagnosticListener("HsSqlAgent.SqlCore.Compiler")

    let private fail code (stage: SqlDiagnosticStage) message : 'T =
        let diagnostic =
            SqlDiagnostic(
                code, stage, SqlDiagnosticCategory.Resource, message,
                Unchecked.defaultof<SqlDiagnosticSpan>)
        if stage = SqlDiagnosticStage.Parse || stage = SqlDiagnosticStage.Lexical then
            raise (SqlParseException(message, diagnostic))
        else
            raise (SqlCompilationException(message, diagnostic))

    let private enforce (session: Session) (phase: Phase) =
        if phase.Work > MaxStageWork then
            fail "SQL_COMPILER_STAGE_WORK_LIMIT" phase.DiagnosticStage
                ("Compiler stage " + phase.Name + " exceeded its deterministic work-unit budget.")
        if GC.GetAllocatedBytesForCurrentThread() - phase.AllocatedAtStart > MaxStageAllocatedBytes then
            fail "SQL_COMPILER_STAGE_ALLOCATION_LIMIT" phase.DiagnosticStage
                ("Compiler stage " + phase.Name + " exceeded its cumulative managed-allocation budget.")
        let now = Stopwatch.GetTimestamp()
        if Stopwatch.GetElapsedTime(session.Started, now).TotalSeconds > float MaxCompilationElapsedSeconds
           || Stopwatch.GetElapsedTime(phase.Started, now).TotalSeconds > float MaxStageElapsedSeconds then
            fail "SQL_COMPILER_DEADLINE_EXCEEDED" phase.DiagnosticStage
                ("Compiler stage " + phase.Name + " exceeded its monotonic compilation deadline.")

    /// A unit is one token, AST frame or renderer emission step. Work is
    /// intentionally NOT wall time, output bytes or allocation bytes.
    let charge units =
        match ambient.Value with
        | Some session ->
            match session.Current with
            | Some phase ->
                phase.Work <- phase.Work + units
                if phase.Work > MaxStageWork || (phase.Work &&& 255L) = 0L then
                    enforce session phase
            | None -> ()
        | None -> ()

    /// Cross-phase, unmetered checkpoints still honor a compilation deadline.
    let checkpoint () = charge 1L

    let withStage name diagnosticStage action =
        match ambient.Value with
        | None -> action ()
        | Some session ->
            let previous = session.Current
            let phase = Phase(name, diagnosticStage)
            session.Current <- Some phase
            let mutable succeeded = false
            try
                enforce session phase
                let value = action ()
                enforce session phase
                succeeded <- true
                value
            finally
                let elapsed = Stopwatch.GetElapsedTime(phase.Started)
                let allocated = max 0L (GC.GetAllocatedBytesForCurrentThread() - phase.AllocatedAtStart)
                session.Current <- previous
                // Observability must never replace the compiler's real
                // validation/resource diagnostic, even if an application
                // registers a faulty DiagnosticListener subscriber.
                try
                    if events.IsEnabled("StageCompleted") then
                        events.Write("StageCompleted",
                            {| Stage = phase.Name
                               ElapsedMicroseconds = int64 elapsed.TotalMicroseconds
                               AllocatedBytes = allocated
                               WorkUnits = phase.Work
                               Success = succeeded |})
                with _ -> ()

    /// Restores an enclosing session on every path; no quota can leak between
    /// parallel requests, C# facade calls or inspection paths.
    let withCompilation action =
        // Nested facade/inspection calls must share the same deadline,
        // instead of silently acquiring a fresh 90-second allowance.
        match ambient.Value with
        | Some _ -> action ()
        | None ->
            ambient.Value <- Some(Session())
            try action ()
            finally ambient.Value <- None
