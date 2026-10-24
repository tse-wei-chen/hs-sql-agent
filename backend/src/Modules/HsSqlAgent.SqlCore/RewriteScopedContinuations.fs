namespace HsSqlAgent.SqlCore.Rewrite

open System

/// Pure proof continuations are identified by their occurrence in a parent
/// evaluation, not by AST identity. The same Query can have different CTE,
/// LATERAL and outer-row scopes at different occurrences.
module internal RewriteScopedContinuations =
    type private Frame<'T>(action: unit -> 'T) =
        let children = ResizeArray<Frame<'T>>()
        member _.Run() = action ()
        member _.Children = children
        member val NextChild = 0 with get, set
        member val Result: 'T voption = ValueNone with get, set

    type private Suspension(owner: obj, frame: obj) =
        inherit Exception()
        member _.Owner = owner
        member _.Frame = frame

    /// Each validator creates its own evaluator. Immutable scopes are captured
    /// in child closures. Completed children resume in constant time; a suspended
    /// prefix is replayed in its original validation/error order. Every operation
    /// and every replay still belongs to the enclosing resource-budget session.
    type Evaluator<'T>() =
        let owner = obj ()
        let mutable driving = false
        let mutable current: Frame<'T> option = None
        let mutable inlineDepth = 0

        /// A caller-proven terminal Query cannot recursively enter a Query
        /// proof. Give its possibly deep scalar expressions a local heap loop
        /// so a shallow sibling does not suspend/replay an entire deep parent
        /// chain. Restore the parent's occurrence cursor on every path.
        member this.EvaluateTerminal(action: unit -> 'T) : 'T =
            let previousCurrent = current
            let previousDriving = driving
            let previousDepth = inlineDepth
            driving <- false
            try this.Evaluate(action, false)
            finally
                current <- previousCurrent
                driving <- previousDriving
                inlineDepth <- previousDepth

        member _.Evaluate(action: unit -> 'T, inlineAllowed: bool) : 'T =
            RewriteResourceBudget.checkpoint ()
            if driving && inlineAllowed && inlineDepth < 4 then
                // Small scalar proofs can finish without creating heap state.
                // The fixed inline allowance bounds the CLR stack; deeper
                // expressions suspend instead of being refused for their depth.
                inlineDepth <- inlineDepth + 1
                try action ()
                finally inlineDepth <- inlineDepth - 1
            elif driving then
                let parent = Option.get current
                let index = parent.NextChild
                parent.NextChild <- index + 1
                let child =
                    if index < parent.Children.Count then parent.Children.[index]
                    else
                        let frame = Frame(action)
                        parent.Children.Add frame
                        frame
                match child.Result with
                | ValueSome result -> result
                | ValueNone -> raise (Suspension(owner, child))
            else
                let root = Frame(action)
                let pending = ResizeArray<Frame<'T>>()
                pending.Add root
                driving <- true
                try
                    while pending.Count > 0 do
                        RewriteResourceBudget.checkpoint ()
                        let last = pending.Count - 1
                        let frame = pending.[last]
                        frame.NextChild <- 0
                        current <- Some frame
                        inlineDepth <- 0
                        try
                            frame.Result <- ValueSome(frame.Run())
                            pending.RemoveAt last
                        with :? Suspension as suspension
                            when Object.ReferenceEquals(owner, suspension.Owner) ->
                            pending.Add(suspension.Frame :?> Frame<'T>)
                    match root.Result with
                    | ValueSome result -> result
                    | ValueNone -> invalidOp "Proof continuation did not produce a result."
                finally
                    current <- None
                    driving <- false
                    inlineDepth <- 0
