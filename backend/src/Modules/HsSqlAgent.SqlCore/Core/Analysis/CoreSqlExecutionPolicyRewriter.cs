using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Core.Pipeline;

namespace HsSqlAgent.SqlCore.Core.Analysis;

/// <summary>
/// Applies execution policy to an already validated plan. Rewrites are structural and immutable,
/// so the command that reaches lowering cannot bypass the policy-applied AST.
/// </summary>
public sealed class CoreSqlExecutionPolicyRewriter : ISqlExecutionPolicyRewriter
{
    public ExecutableSqlPlan Rewrite(
        ValidatedSqlPlan plan,
        SqlExecutionPlanPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(policy);

        var statement = policy.QueryMaxRows > 0
            ? ApplyMaxRows(plan.Statement, policy.QueryMaxRows)
            : plan.Statement;

        return new ExecutableSqlPlan(
            statement,
            plan.Facts,
            plan.SourceDialect,
            plan.TargetProvider,
            plan.PolicyVersion);
    }

    private static SqlStatement ApplyMaxRows(SqlStatement statement, int maxRows)
    {
        if (maxRows <= 0) return statement;

        return statement switch
        {
            SelectStatement select => select with
            {
                Limit = ClampLimit(select.Limit, maxRows)
            },
            QueryStatement query => query with
            {
                // A set operation's externally visible cardinality is constrained at the query
                // level. Branch-local limits are semantic input and are intentionally untouched.
                Limit = ClampLimit(query.Limit, maxRows)
            },
            _ => throw new InvalidOperationException(
                $"Unsupported statement during execution policy rewrite: {statement.GetType().Name}")
        };
    }

    private static int ClampLimit(int? requested, int maxRows) => requested switch
    {
        0 => 0,
        > 0 => Math.Min(requested.Value, maxRows),
        _ => maxRows
    };
}
