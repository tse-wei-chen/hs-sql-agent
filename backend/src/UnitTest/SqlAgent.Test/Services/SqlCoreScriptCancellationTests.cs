using System.Reflection;
using System.Threading;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using Microsoft.FSharp.Core;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class SqlCoreScriptCancellationTests
{
    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void ParseScript_PreCancelledToken_StopsBeforeParsing(SqlAgentToolType dialect)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            SqlCoreFacade.ParseScript("SELECT 1; SELECT 2", dialect, cancellation.Token));

        // A cancelled request must not poison later requests on the same context.
        var fresh = SqlCoreFacade.ParseScript("SELECT 1", dialect);
        Assert.Single(fresh.Statements);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void AnalyzeScript_PreCancelledToken_StopsWithoutCatalogEffects(SqlAgentToolType dialect)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            SqlCoreFacade.AnalyzeScript("CREATE TABLE t (id INT); SELECT 1",
                dialect, SqlCatalogState.KnownEmpty(), cancellation.Token));

        var fresh = SqlCoreFacade.AnalyzeScript("SELECT 1", dialect,
            SqlCatalogState.KnownEmpty());
        Assert.Single(fresh.Statements);
    }

    [Fact]
    public void Cancellation_IsObservedAtTheNextWorkCheckpoint_NotOnlyBeforeTheCall()
    {
        using var cancellation = new CancellationTokenSource();
        var budget = typeof(SqlCoreFacade).Assembly.GetType(
            "HsSqlAgent.SqlCore.Rewrite.RewriteResourceBudget", throwOnError: true)!;
        var scope = budget.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(method => method.Name == "withCancellation");
        var checkpoint = budget.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(method => method.Name == "checkpoint");

        if (scope.IsGenericMethodDefinition)
            scope = scope.MakeGenericMethod(typeof(object));

        var callback = new Callback(() =>
        {
            checkpoint.Invoke(null, null); // Work can proceed while not cancelled.
            cancellation.Cancel();
            var error = Assert.Throws<TargetInvocationException>(() =>
                checkpoint.Invoke(null, null));
            Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
            return new object();
        });
        var scopeError = Assert.Throws<TargetInvocationException>(() =>
            scope.Invoke(null, new object[] { cancellation.Token, callback }));
        Assert.IsAssignableFrom<OperationCanceledException>(scopeError.InnerException);

        // The ambient cancellation scope must be restored after exiting.
        Assert.Single(SqlCoreFacade.ParseScript("SELECT 1", SqlAgentToolType.Postgres).Statements);
    }

    private sealed class Callback(Func<object> callback) : FSharpFunc<Unit, object>
    {
        public override object Invoke(Unit _) => callback();
    }
}
