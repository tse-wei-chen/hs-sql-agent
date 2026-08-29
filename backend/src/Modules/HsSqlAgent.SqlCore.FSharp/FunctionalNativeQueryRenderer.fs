namespace HsSqlAgent.SqlCore.Core.Lowering

open System
open System.Collections.Immutable
open HsSqlAgent.SqlCore.Core.Ast
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Core.Execution
open HsSqlAgent.SqlCore.Core.Pipeline
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models

module internal FunctionalNativeQueryRenderer =

    let private appendFragments (left: NativeSqlFragment) (right: NativeSqlFragment) separator =
        if right.Sql.Length = 0 then left
        elif left.Sql.Length = 0 then right
        else NativeSqlFragment(left.Sql + separator + right.Sql, left.Bindings.AddRange(right.Bindings))

    let private rootCtes (statement: SqlStatement) =
        match statement with
        | :? SelectStatement as select -> select.Ctes
        | :? QueryStatement as query -> query.Head.Ctes
        | _ -> ImmutableArray<CteDefinition>.Empty

    let private requiresTail (statement: QueryStatement) =
        not statement.OrderBy.IsDefaultOrEmpty
        || statement.Limit.HasValue
        || (statement.Offset.HasValue && statement.Offset.Value > 0)

    let private withoutTail (statement: QueryStatement) =
        QueryStatement(
            statement.Head,
            statement.SetOperations,
            ImmutableArray<OrderByItem>.Empty,
            Nullable<int>(),
            Nullable<int>(),
            statement.Span)

    let private setOperationKeyword kind =
        match kind with
        | SetOperationKind.Union -> "UNION"
        | SetOperationKind.UnionAll -> "UNION ALL"
        | SetOperationKind.Intersect -> "INTERSECT"
        | SetOperationKind.Except -> "EXCEPT"
        | other -> raise (SqlCompilationException($"Unsupported set operation '{other}'."))

    let rec private renderStatement
        (renderer: NativeSqlRenderer)
        (statement: SqlStatement)
        position =

        match statement with
        | :? SelectStatement as select -> renderSelect renderer select position true
        | :? QueryStatement as query -> renderQuery renderer query position
        | other -> raise (SqlCompilationException(
            $"Native query renderer requires SELECT/query-set AST, not {other.GetType().Name}."))

    and private renderCtes (renderer: NativeSqlRenderer) (ctes: ImmutableArray<CteDefinition>) =
        if ctes.IsDefaultOrEmpty then
            NativeSqlFragment.Empty
        else
            let sqlParts = ResizeArray<string>()
            let mutable bindings = ImmutableArray<obj | null>.Empty
            for cte in ctes do
                if not cte.ColumnAliases.IsDefaultOrEmpty then
                    raise (SqlCompilationException(
                        "CTE column aliases must be canonicalized to projection aliases before native lowering."))
                if cte.Name.Parts.Length <> 1 then
                    raise (SqlCompilationException("CTE name must be unqualified."))

                let query: NativeSqlFragment =
                    renderStatement renderer cte.Query FunctionalQueryPosition.CteDefinition
                let name = CoreIdentifierSqlRenderer.Render(cte.Name, renderer.Provider, allowWildcard = false)
                sqlParts.Add(name + " AS (" + query.Sql + ")")
                bindings <- bindings.AddRange(query.Bindings)

            NativeSqlFragment("WITH " + String.Join(", ", sqlParts), bindings)

    and private renderSelect
        (renderer: NativeSqlRenderer)
        (statement: SelectStatement)
        position
        includeTail =

        let ctes = renderCtes renderer statement.Ctes
        let body =
            if includeTail
               && renderer.Provider = SqlAgentToolType.MsSqlServer
               && statement.Offset.HasValue
               && statement.Offset.Value > 0
               && (not statement.Limit.HasValue || statement.Limit.Value <> 0) then
                renderer.RenderSqlServerOffsetSelectForFunctional(statement)
            else
                renderer.RenderSelectBodyForFunctional(statement, position, includeTail)
        appendFragments ctes body " "

    and private renderSetBranch (renderer: NativeSqlRenderer) (statement: SqlStatement) =
        let branch = renderStatement renderer statement FunctionalQueryPosition.SetBranch
        if (rootCtes statement).IsDefaultOrEmpty then
            branch
        else
            let alias =
                CoreIdentifierSqlRenderer.RenderAlias(
                    IdentifierPart("_set_branch", false, SourceSpan.Unknown),
                    renderer.Provider)
            NativeSqlFragment(
                "SELECT * FROM (" + branch.Sql + ") AS " + alias,
                branch.Bindings)

    and private renderSetBody
        (renderer: NativeSqlRenderer)
        (statement: QueryStatement)
        position =

        let head = renderSelect renderer statement.Head position false
        let mutable sql = head.Sql
        let mutable bindings = head.Bindings
        for operation in statement.SetOperations do
            let branch = renderSetBranch renderer operation.Query
            sql <- sql + " " + setOperationKeyword operation.Kind + " " + branch.Sql
            bindings <- bindings.AddRange(branch.Bindings)
        NativeSqlFragment(sql, bindings)

    and private renderQuery
        (renderer: NativeSqlRenderer)
        (statement: QueryStatement)
        position =

        if not (requiresTail statement) then
            renderSetBody renderer statement position
        elif position = FunctionalQueryPosition.ScalarSubquery
             && CoreNativeSetTailScope.CanRenderDirectTail(statement) then
            let body = renderSetBody renderer (withoutTail statement) position
            let tail =
                renderer.RenderDirectSetTailForFunctional(
                    statement.OrderBy,
                    statement.Limit,
                    statement.Offset,
                    statement.Head.Select)
            appendFragments body tail " "
        else
            let inner = renderSetBody renderer (withoutTail statement) position
            renderer.RenderSetTailWrapperForFunctional(
                inner,
                statement.OrderBy,
                statement.Limit,
                statement.Offset,
                statement.Head.Select)

    let lower
        (plan: ExecutableSqlPlan)
        (provider: SqlAgentToolType)
        (targetProfile: SqlProviderCapabilityProfile | null) =

        ArgumentNullException.ThrowIfNull(plan)
        if plan.TargetProvider <> provider then
            raise (SqlCompilationException(
                $"Plan targets {plan.TargetProvider}, but this native renderer targets {provider}."))

        let renderer = NativeSqlRenderer(provider, targetProfile)
        let fragment = renderStatement renderer plan.Statement FunctionalQueryPosition.Root
        let struct (finalizedSql, finalizedParameters) = NativeSqlParameterizer.Finalize(fragment, provider)
        let command =
            CompiledSqlCommand(
                finalizedSql,
                finalizedParameters,
                SqlStatementKind.Select,
                String.Empty,
                provider)
        let fingerprint = DmlFingerprintService.ComputePlanFingerprint(command, plan.PolicyVersion)
        CompiledSqlCommand(
            command.Sql,
            command.Parameters,
            command.Kind,
            fingerprint,
            command.TargetProvider)
