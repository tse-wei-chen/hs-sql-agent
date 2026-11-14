using System.Collections.Immutable;
using System.Diagnostics;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Ast;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreRendererDocumentBudgetTests
{
    [Fact]
    public void ParsedAst_OversizedQuotedIdentifier_IsDeniedBeforeEscapingAllocation()
    {
        var parsed = CoreSqlTextParser.ParseQuery("SELECT id FROM users", SqlAgentToolType.Postgres);
        var select = Assert.IsType<SelectStatement>(parsed.Statement);
        var identifier = new SqlIdentifier(ImmutableArray.Create(
            new IdentifierPart(new string('x', 16_777_217), true, SourceSpan.Unknown, true)), SourceSpan.Unknown);
        select.Select[0].Expression = new ColumnExpr(identifier, SourceSpan.Unknown);
        var ownerThread = Environment.CurrentManagedThreadId;
        long renderingAllocation = -1;
        var subscriptions = new List<IDisposable>();
        using var observer = DiagnosticListener.AllListeners.Subscribe(new Observer<DiagnosticListener>(source =>
        {
            if (source.Name != "HsSqlAgent.SqlCore.Compiler") return;
            lock (subscriptions) subscriptions.Add(source.Subscribe(new Observer<KeyValuePair<string, object?>>(measurement =>
            {
                if (Environment.CurrentManagedThreadId != ownerThread || measurement.Key != "StageCompleted"
                    || measurement.Value is not { } data) return;
                object? Get(string name) => data.GetType().GetProperty(name)!.GetValue(data);
                if (Equals(Get("Stage"), "Rendering"))
                    renderingAllocation = Convert.ToInt64(Get("AllocatedBytes"));
            })));
        }));
        try
        {
            var error = Assert.Throws<SqlCompilationException>(() => CoreSqlCompiler.CreateDefault().Compile(
                parsed, SqlAgentToolType.Postgres, new SqlPlanValidationContext("document-output-budget-v1"),
                new SqlExecutionPlanPolicy()));
            Assert.Equal("SQL_COMPILER_RENDERED_OUTPUT_LIMIT", error.Diagnostic?.Code);
            Assert.Equal(SqlDiagnosticStage.RenderingInvariant, error.Diagnostic?.Stage);
            Assert.Equal(SqlDiagnosticCategory.Resource, error.Diagnostic?.Category);
            Assert.InRange(renderingAllocation, 0, 8_000_000);
        }
        finally
        {
            lock (subscriptions) foreach (var subscription in subscriptions) subscription.Dispose();
        }
    }

    private sealed class Observer<T>(Action<T> next) : IObserver<T>
    {
        public void OnNext(T value) => next(value);
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }
}
