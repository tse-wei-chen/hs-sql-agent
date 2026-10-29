using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// P6: public compiler cancellation must remain a CLR-only opt-in contract.
/// A cancelled compile is not a failed SQL proof or a TryCompile error payload.
/// </summary>
public sealed class SqlCoreCompilerCancellationContractTests
{
    private static readonly Type[] QueryTextSignature =
    [
        typeof(string), typeof(SqlAgentToolType), typeof(SqlAgentToolType),
        typeof(SqlPlanValidationContext), typeof(SqlExecutionPlanPolicy),
        typeof(CancellationToken)
    ];

    private static readonly Type[] DmlTextSignature =
    [
        typeof(string), typeof(SqlAgentToolType), typeof(SqlAgentToolType),
        typeof(SqlPlanValidationContext), typeof(CancellationToken)
    ];

    private static readonly Type[] QueryParsedSignature =
    [
        typeof(ParsedStatement), typeof(SqlAgentToolType),
        typeof(SqlPlanValidationContext), typeof(SqlExecutionPlanPolicy),
        typeof(CancellationToken)
    ];

    private static readonly Type[] DmlParsedSignature =
    [
        typeof(ParsedStatement), typeof(SqlAgentToolType),
        typeof(SqlPlanValidationContext), typeof(CancellationToken)
    ];

    private static MethodInfo Overload(string name, Type[] signature)
    {
        var method = typeof(SqlCoreFacade).GetMethod(
            name, BindingFlags.Public | BindingFlags.Static,
            binder: null, types: signature, modifiers: null);
        Assert.NotNull(method);
        Assert.DoesNotContain("Microsoft.FSharp", method.ReturnType.FullName ?? "",
            StringComparison.Ordinal);
        return method;
    }

    private static SqlPlanValidationContext Validation() =>
        new("sqlcore-p6-cancellation-contract");

    private static object? Invoke(MethodInfo method, params object?[] args) =>
        method.Invoke(null, args);

    private static void Cancelled(MethodInfo method, params object?[] args)
    {
        var exception = Assert.Throws<TargetInvocationException>(() =>
            Invoke(method, args));
        Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
    }

    [Fact]
    public void AllSixCompilerEntryPointsExposeClrCancellationOverloads()
    {
        Overload("CompileQuery", QueryTextSignature);
        Overload("CompileDml", DmlTextSignature);
        Overload("CompileQuery", QueryParsedSignature);
        Overload("CompileDml", DmlParsedSignature);
        Overload("TryCompileQuery", QueryTextSignature);
        Overload("TryCompileDml", DmlTextSignature);
    }

    [Fact]
    public void PreCancelledTokenRejectsTextAndTryPathsWithoutPoisoningLaterCalls()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var query = Overload("CompileQuery", QueryTextSignature);
        var dml = Overload("CompileDml", DmlTextSignature);
        var tryQuery = Overload("TryCompileQuery", QueryTextSignature);
        var tryDml = Overload("TryCompileDml", DmlTextSignature);

        foreach (var method in new[] { query, tryQuery })
            Cancelled(method, "SELECT 1", SqlAgentToolType.Postgres,
                SqlAgentToolType.Postgres, Validation(),
                new SqlExecutionPlanPolicy(), source.Token);

        foreach (var method in new[] { dml, tryDml })
            Cancelled(method, "UPDATE users SET name = 'Ada' WHERE id = 1",
                SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
                Validation(), source.Token);

        Assert.NotEmpty(SqlCoreFacade.CompileQuery(
            "SELECT 2", SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            Validation(), new SqlExecutionPlanPolicy()).Sql);
        Assert.True(SqlCoreFacade.TryCompileQuery(
            "SELECT 3", SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            Validation(), new SqlExecutionPlanPolicy()).Success);
    }

    [Fact]
    public void PreCancelledTokenRejectsParsedPathsWhileLegacyParsedApiStillWorks()
    {
        var query = CoreSqlTextParser.ParseQuery("SELECT 1", SqlAgentToolType.Postgres);
        var dml = CoreSqlTextParser.ParseDml(
            "UPDATE users SET name = 'Ada' WHERE id = 1", SqlAgentToolType.Postgres);
        using var source = new CancellationTokenSource();
        source.Cancel();

        Cancelled(Overload("CompileQuery", QueryParsedSignature),
            query, SqlAgentToolType.Postgres, Validation(),
            new SqlExecutionPlanPolicy(), source.Token);
        Cancelled(Overload("CompileDml", DmlParsedSignature),
            dml, SqlAgentToolType.Postgres, Validation(), source.Token);

        Assert.NotEmpty(SqlCoreFacade.CompileQuery(
            query, SqlAgentToolType.Postgres, Validation(),
            new SqlExecutionPlanPolicy()).Sql);
    }

    [Fact]
    public void CancellationRaisedAfterParsingIsNotSwallowedByTryCompile()
    {
        using var source = new CancellationTokenSource();
        var subscriptions = new List<IDisposable>();
        var owner = Environment.CurrentManagedThreadId;
        var sawParsing = false;
        using var all = DiagnosticListener.AllListeners.Subscribe(
            new Observer<DiagnosticListener>(listener =>
            {
                if (listener.Name != "HsSqlAgent.SqlCore.Compiler") return;
                lock (subscriptions)
                    subscriptions.Add(listener.Subscribe(
                        new Observer<KeyValuePair<string, object?>>(measurement =>
                        {
                            if (measurement.Key != "StageCompleted" ||
                                measurement.Value is null ||
                                Environment.CurrentManagedThreadId != owner) return;
                            var stage = measurement.Value.GetType().GetProperty("Stage")
                                ?.GetValue(measurement.Value) as string;
                            if (stage != "Parsing") return;
                            sawParsing = true;
                            source.Cancel();
                        })));
            }));
        try
        {
            Cancelled(Overload("TryCompileQuery", QueryTextSignature),
                "SELECT 1", SqlAgentToolType.Postgres,
                SqlAgentToolType.Postgres, Validation(),
                new SqlExecutionPlanPolicy(), source.Token);
            Assert.True(sawParsing);
        }
        finally
        {
            lock (subscriptions)
                foreach (var subscription in subscriptions) subscription.Dispose();
        }
        Assert.NotEmpty(SqlCoreFacade.CompileQuery(
            "SELECT 2", SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            Validation(), new SqlExecutionPlanPolicy()).Sql);
    }

    [Fact]
    public void ActiveTokenUsesIdenticalSqlPipelineAndTryResultContracts()
    {
        var query = Overload("CompileQuery", QueryTextSignature);
        var tryQuery = Overload("TryCompileQuery", QueryTextSignature);
        var dml = Overload("CompileDml", DmlTextSignature);
        using var source = new CancellationTokenSource();

        var standard = SqlCoreFacade.CompileQuery(
            "SELECT 1", SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            Validation(), new SqlExecutionPlanPolicy());
        var active = Invoke(query, "SELECT 1", SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres, Validation(),
            new SqlExecutionPlanPolicy(), source.Token);
        Assert.Equal(standard.Sql, Assert.IsType<CompiledSqlCommand>(active).Sql);

        var tryResult = Assert.IsType<SqlCoreTryResult<CompiledSqlCommand>>(
            Invoke(tryQuery, "SELECT 1", SqlAgentToolType.Postgres,
                SqlAgentToolType.Postgres, Validation(),
                new SqlExecutionPlanPolicy(), source.Token));
        Assert.True(tryResult.Success);
        Assert.Equal(standard.Sql, tryResult.Value.Sql);

        var mutation = Invoke(dml, "UPDATE users SET name = 'Ada' WHERE id = 1",
            SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            Validation(), source.Token);
        Assert.NotEmpty(Assert.IsType<CompiledSqlCommand>(mutation).Sql);
    }

    private sealed class Observer<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnNext(T value) => onNext(value);
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }
}
