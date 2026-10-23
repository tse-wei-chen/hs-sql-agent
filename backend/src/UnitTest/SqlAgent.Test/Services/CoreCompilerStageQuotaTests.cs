using System.Reflection;
using System.Runtime.ExceptionServices;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Microsoft.FSharp.Core;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Exercises the actual internal F# compilation session without exporting any
/// configurable resource budget or test-only hook in the shipped NuGet API.
/// Each test invokes a unit -> object closure via the same F# functions used
/// by the production compiler.
/// </summary>
public sealed class CoreCompilerStageQuotaTests
{
    private static readonly Type BudgetModule =
        typeof(SqlCoreFacade).Assembly.GetType(
            "HsSqlAgent.SqlCore.Rewrite.RewriteResourceBudget", throwOnError: true)!;

    private static MethodInfo Method(string name) =>
        BudgetModule.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(method => string.Equals(method.Name, name, StringComparison.OrdinalIgnoreCase));

    private sealed class Function(Func<object?> callback) : FSharpFunc<Unit, object>
    {
        public override object Invoke(Unit _) => callback()!;
    }

    private static object? Invoke(MethodInfo method, params object?[] arguments)
    {
        if (method.IsGenericMethodDefinition)
            method = method.MakeGenericMethod(typeof(object));
        try
        {
            return method.Invoke(null, arguments);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static object? Compilation(Func<object?> callback) =>
        Invoke(Method("withCompilation"), new Function(callback));

    private static object? Stage(
        string name, SqlDiagnosticStage stage, Func<object?> callback) =>
        Invoke(Method("withStage"), name, stage, new Function(callback));

    [Fact]
    public void StageWorkLimit_FailsClosedWithResourceDiagnosticInBinding()
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            Compilation(() => Stage("Binding", SqlDiagnosticStage.Binding, () =>
            {
                Invoke(Method("charge"), 8_000_001L);
                return null;
            })));

        Assert.Equal("SQL_COMPILER_STAGE_WORK_LIMIT", error.Diagnostic?.Code);
        Assert.Equal(SqlDiagnosticStage.Binding, error.Diagnostic?.Stage);
        Assert.Equal(SqlDiagnosticCategory.Resource, error.Diagnostic?.Category);

        // The failing request must release the compilation-local budget.
        var command = SqlCoreFacade.CompileQuery(
            "SELECT 1 AS value",
            SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("quota-recovery-v1"),
            new SqlExecutionPlanPolicy());
        Assert.NotEmpty(command.Sql);
    }

    [Fact]
    public void StageWorkLimit_UsesParseExceptionForParserPhase()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            Compilation(() => Stage("Parsing", SqlDiagnosticStage.Parse, () =>
            {
                Invoke(Method("charge"), 8_000_001L);
                return null;
            })));

        Assert.Equal("SQL_COMPILER_STAGE_WORK_LIMIT", error.Diagnostic?.Code);
        Assert.Equal(SqlDiagnosticStage.Parse, error.Diagnostic?.Stage);
        Assert.Equal(SqlDiagnosticCategory.Resource, error.Diagnostic?.Category);
    }

    [Fact]
    public void StageWorkBudget_IsIndependentAcrossSequentialPhases()
    {
        var result = Compilation(() =>
        {
            Stage("Parsing", SqlDiagnosticStage.Parse, () =>
            {
                Invoke(Method("charge"), 4_500_000L);
                return null;
            });
            Stage("Binding", SqlDiagnosticStage.Binding, () =>
            {
                Invoke(Method("charge"), 4_500_000L);
                return null;
            });
            return "stages-completed";
        });
        Assert.Equal("stages-completed", result);
    }

    [Fact]
    public void AggregateCompilationWork_RejectsIndividuallyValidStages()
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            Compilation(() =>
            {
                for (var i = 0; i < 7; i++)
                    Stage("Phase" + i, SqlDiagnosticStage.Binding, () =>
                    {
                        Invoke(Method("charge"), 5_000_000L);
                        return null;
                    });
                return null;
            }));

        Assert.Equal("SQL_COMPILER_TOTAL_WORK_LIMIT", error.Diagnostic?.Code);
        Assert.Equal(SqlDiagnosticStage.Binding, error.Diagnostic?.Stage);
        Assert.Equal(SqlDiagnosticCategory.Resource, error.Diagnostic?.Category);
    }

    [Fact]
    public void DmlBatch_UsesEnclosingAggregateQuotaWithoutReset()
    {
        var input = string.Concat(Enumerable.Repeat(
            "DELETE FROM users WHERE id = 1;", 80));
        var error = Assert.Throws<SqlParseException>(() =>
            Compilation(() =>
            {
                for (var i = 0; i < 6; i++)
                    Stage("PreviousStage" + i, SqlDiagnosticStage.Binding, () =>
                    {
                        Invoke(Method("charge"), 5_333_316L);
                        return null;
                    });
                return CoreDmlBatchTextParser.ParseDmlBatch(
                    input, SqlAgentToolType.Postgres);
            }));

        Assert.Equal("SQL_COMPILER_TOTAL_WORK_LIMIT", error.Diagnostic?.Code);
        Assert.Equal(SqlDiagnosticStage.Parse, error.Diagnostic?.Stage);
        Assert.Equal(SqlDiagnosticCategory.Resource, error.Diagnostic?.Category);

        // A second request must not inherit the failed batch's quota.
        Assert.Equal(2, CoreDmlBatchTextParser.ParseDmlBatch(
            "DELETE FROM users WHERE id = 1; DELETE FROM users WHERE id = 2",
            SqlAgentToolType.Postgres).Count);
    }

    [Fact]
    public void NestedCompilation_DoesNotResetTheParentStageQuota()
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            Compilation(() => Stage("Binding", SqlDiagnosticStage.Binding, () =>
            {
                Invoke(Method("charge"), 5_000_000L);
                Compilation(() =>
                {
                    Invoke(Method("charge"), 3_000_001L);
                    return null;
                });
                return null;
            })));

        Assert.Equal("SQL_COMPILER_STAGE_WORK_LIMIT", error.Diagnostic?.Code);
        Assert.Equal(SqlDiagnosticStage.Binding, error.Diagnostic?.Stage);
    }
}
