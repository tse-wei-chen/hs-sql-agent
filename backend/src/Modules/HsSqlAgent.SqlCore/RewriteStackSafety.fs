namespace HsSqlAgent.SqlCore.Rewrite

open System
open System.Runtime.CompilerServices
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.SqlParsing

/// Last-resort *runtime-adaptive* stack protection, never an arbitrary SQL
/// nesting-depth cap. Prefer iterative traversal where semantics permit.
/// Deep legal SQL remains compilable whenever this process/thread has stack.
module internal RewriteStackSafety =
    [<Literal>]
    let DiagnosticCode = "SQL_COMPILER_STACK_RESERVE"

    let private message =
        "The compiler's current execution thread has insufficient remaining stack " +
        "to safely continue recursive SQL processing; retry using a larger " +
        "execution stack or a less deeply nested expression."

    let private diagnostic stage sourceOffset =
        let location =
            if sourceOffset >= 0 then SqlDiagnosticSpan(sourceOffset, 1)
            else Unchecked.defaultof<SqlDiagnosticSpan>
        SqlDiagnostic(
            DiagnosticCode,
            stage,
            SqlDiagnosticCategory.Resource,
            message,
            location)

    let inline private enough () = RuntimeHelpers.TryEnsureSufficientExecutionStack()

    let parse sourceOffset =
        RewriteResourceBudget.checkpoint ()
        if not (enough ()) then
            raise (SqlParseException(message, diagnostic SqlDiagnosticStage.Parse sourceOffset))

    let compile stage =
        RewriteResourceBudget.checkpoint ()
        if not (enough ()) then
            raise (SqlCompilationException(message, diagnostic stage -1))
