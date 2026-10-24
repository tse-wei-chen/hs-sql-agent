using System.Reflection;
using System.Diagnostics;
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

    private static object CurrentSession()
    {
        var ambient = BudgetModule.GetProperty("ambient",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(null)!;
        var option = ambient.GetType().GetProperty("Value")!.GetValue(ambient)!;
        return option.GetType().GetProperty("Value")!.GetValue(option)!;
    }

    [Theory]
    [InlineData("string")]
    [InlineData("parsed")]
    [InlineData("inspection")]
    [InlineData("facts")]
    [InlineData("dml")]
    [InlineData("batch")]
    public void PublicEntryPoints_InheritExpiredCompilationDeadlineAndRecover(string route)
    {
        var parsed = CoreSqlTextParser.ParseQuery("SELECT id FROM users", SqlAgentToolType.Postgres);
        var context = new SqlPlanValidationContext("public-deadline-v1");
        var error = Assert.ThrowsAny<Exception>(() => Compilation(() =>
        {
            var session = CurrentSession();
            session.GetType().GetField("started", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(session, Stopwatch.GetTimestamp() - 120L * Stopwatch.Frequency);
            return route switch
            {
                "string" => SqlCoreFacade.CompileQuery("SELECT id FROM users",
                    SqlAgentToolType.Postgres, SqlAgentToolType.Postgres, context, new SqlExecutionPlanPolicy()),
                "parsed" => CoreSqlCompiler.CreateDefault().Compile(parsed,
                    SqlAgentToolType.Postgres, context, new SqlExecutionPlanPolicy()),
                "inspection" => SqlCoreInspection.GetQueryFacts("SELECT id FROM users", SqlAgentToolType.Postgres),
                "facts" => SqlCoreFacade.CompileQueryWithFacts("SELECT id FROM users",
                    SqlAgentToolType.Postgres, SqlAgentToolType.Postgres, context, new SqlExecutionPlanPolicy()),
                "dml" => SqlCoreFacade.CompileDml("DELETE FROM users WHERE id = 1",
                    SqlAgentToolType.Postgres, SqlAgentToolType.Postgres, context),
                "batch" => CoreDmlBatchTextParser.ParseDmlBatch("DELETE FROM users WHERE id = 1", SqlAgentToolType.Postgres),
                _ => throw new ArgumentOutOfRangeException(nameof(route))
            };
        }));
        var diagnostic = error switch
        {
            SqlParseException parse => parse.Diagnostic,
            SqlCompilationException compile => compile.Diagnostic,
            _ => throw new InvalidOperationException("Untyped deadline failure", error)
        };
        Assert.Equal("SQL_COMPILER_DEADLINE_EXCEEDED", diagnostic?.Code);
        Assert.Equal(SqlDiagnosticCategory.Resource, diagnostic?.Category);
        Assert.NotEmpty(SqlCoreFacade.CompileQuery("SELECT id FROM users",
            SqlAgentToolType.Postgres, SqlAgentToolType.Postgres, context, new SqlExecutionPlanPolicy()).Sql);
    }

    [Fact]
    public void LargeCharge_CannotOverflowPreviouslyChargedWork()
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            Compilation(() => Stage("Binding", SqlDiagnosticStage.Binding, () =>
            {
                Invoke(Method("charge"), 1L);
                Invoke(Method("charge"), long.MaxValue);
                return null;
            })));
        Assert.Equal("SQL_COMPILER_STAGE_WORK_LIMIT", error.Diagnostic?.Code);
        Assert.Equal(SqlDiagnosticCategory.Resource, error.Diagnostic?.Category);
    }

    [Theory]
    [InlineData(SqlDiagnosticStage.Parse)]
    [InlineData(SqlDiagnosticStage.Binding)]
    [InlineData(SqlDiagnosticStage.RenderingInvariant)]
    public void BatchedCharge_ChecksAllocationBeforeStageExit(SqlDiagnosticStage stage)
    {
        Compilation(() => Stage("AllocationProbe", stage, () =>
        {
            Invoke(Method("charge"), 1L);
            var session = CurrentSession();
            var current = session.GetType().GetProperty("Current",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(session)!;
            var phase = current.GetType().GetProperty("Value")!.GetValue(current)!;
            var allocation = phase.GetType().GetField("allocated",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            var original = allocation.GetValue(phase);
            try
            {
                // Simulate this phase crossing its cumulative allocation
                // boundary without making the test allocate half a gigabyte.
                allocation.SetValue(phase, GC.GetAllocatedBytesForCurrentThread() - 536_870_913L);
                var error = Assert.ThrowsAny<Exception>(() => Invoke(Method("charge"), 256L));
                var diagnostic = error switch
                {
                    SqlParseException parse => parse.Diagnostic,
                    SqlCompilationException compile => compile.Diagnostic,
                    _ => throw new InvalidOperationException("Untyped allocation failure", error)
                };
                Assert.Equal("SQL_COMPILER_STAGE_ALLOCATION_LIMIT", diagnostic?.Code);
                Assert.Equal(stage, diagnostic?.Stage);
                Assert.Equal(SqlDiagnosticCategory.Resource, diagnostic?.Category);
            }
            finally
            {
                allocation.SetValue(phase, original);
            }
            return null;
        }));
    }

    [Theory]
    [InlineData(256L)]
    [InlineData(513L)]
    [InlineData(4096L)]
    public void BatchedCharge_ChecksDeadlineWhenCrossingSamplingBoundary(long units)
    {
        var checkedInsideAction = false;
        Compilation(() => Stage("Binding", SqlDiagnosticStage.Binding, () =>
        {
            Invoke(Method("charge"), 1L);
            // Age only this attempt's phase. No sleeps or process-global
            // clock/quota changes: the failure must happen inside charge,
            // rather than being deferred until withStage exits.
            var session = CurrentSession();
            var current = session.GetType().GetProperty("Current",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(session)!;
            var phase = current.GetType().GetProperty("Value")!.GetValue(current)!;
            var started = phase.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(field => field.Name == "start");
            started.SetValue(phase, Stopwatch.GetTimestamp() - 60L * Stopwatch.Frequency);
            var error = Assert.Throws<SqlCompilationException>(() => Invoke(Method("charge"), units));
            Assert.Equal("SQL_COMPILER_DEADLINE_EXCEEDED", error.Diagnostic?.Code);
            checkedInsideAction = true;
            // Restore phase time so its normal exit can complete.
            started.SetValue(phase, Stopwatch.GetTimestamp());
            return null;
        }));
        Assert.True(checkedInsideAction);
    }

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
