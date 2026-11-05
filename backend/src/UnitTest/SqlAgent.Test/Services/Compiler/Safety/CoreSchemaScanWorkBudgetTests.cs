using System.Diagnostics;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreSchemaScanWorkBudgetTests
{
    private static SqlPlanValidationContext Context(SqlAgentToolType provider, int width)
    {
        var upper = provider is SqlAgentToolType.Oracle or SqlAgentToolType.Firebird;
        return new SqlPlanValidationContext("schema-scan-work-v1").WithSchemaSnapshot(
            new SqlSchemaSnapshot(provider, "wide-generated-metadata", new[] {
                SqlSchemaTable.WithTypes(upper ? "USERS" : "users",
                    Enumerable.Range(0, width).Select(index => new SqlSchemaColumn(
                        (upper ? "C" : "c") + index, SqlSchemaValueKind.Numeric))) }));
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void WideSchema_NameAndTypeProofCountEveryScannedColumn(SqlAgentToolType provider)
    {
        const int width = 512, projections = 32;
        var ownerThread = Environment.CurrentManagedThreadId;
        var work = new Dictionary<string, long>();
        var subscriptions = new List<IDisposable>();
        using var listener = DiagnosticListener.AllListeners.Subscribe(new Observer<DiagnosticListener>(source =>
        {
            if (source.Name != "HsSqlAgent.SqlCore.Compiler") return;
            lock (subscriptions) subscriptions.Add(source.Subscribe(new Observer<KeyValuePair<string, object?>>(sample =>
            {
                if (Environment.CurrentManagedThreadId != ownerThread || sample.Key != "StageCompleted"
                    || sample.Value is not { } data) return;
                object? Get(string name) => data.GetType().GetProperty(name)!.GetValue(data);
                if (Get("Stage") is string stage && stage is "SchemaBinding" or "SchemaType")
                    work[stage] = Convert.ToInt64(Get("WorkUnits"));
            })));
        }));
        try
        {
            var sql = "SELECT " + string.Join(",", Enumerable.Repeat("c511", projections)) + " FROM users";
            Assert.NotEmpty(SqlCoreFacade.CompileQuery(sql, provider, provider,
                Context(provider, width), new SqlExecutionPlanPolicy()).Sql);
            Assert.True(work["SchemaBinding"] >= width * projections);
            Assert.True(work["SchemaType"] >= width * projections);
        }
        finally
        {
            lock (subscriptions) foreach (var subscription in subscriptions) subscription.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedWideSchemaScan_ExhaustsRealProofWorkAndNextAttemptRecovers(bool parsedRoute)
    {
        var provider = SqlAgentToolType.Postgres;
        var context = Context(provider, 1024);
        var sql = "SELECT " + string.Join(",", Enumerable.Repeat("c1023", 8192)) + " FROM users";
        var parsed = parsedRoute ? CoreSqlTextParser.ParseQuery(sql, provider) : null;
        var error = Assert.Throws<SqlCompilationException>(() =>
        {
            if (parsed is not null)
                CoreSqlCompiler.CreateDefault().Compile(parsed, provider, context, new SqlExecutionPlanPolicy());
            else
                SqlCoreFacade.CompileQuery(sql, provider, provider, context, new SqlExecutionPlanPolicy());
        });
        Assert.Equal("SQL_COMPILER_STAGE_WORK_LIMIT", error.Diagnostic?.Code);
        Assert.Equal(SqlDiagnosticStage.Binding, error.Diagnostic?.Stage);
        Assert.Equal(SqlDiagnosticCategory.Resource, error.Diagnostic?.Category);
        Assert.NotEmpty(SqlCoreFacade.CompileQuery("SELECT c1023 FROM users", provider, provider,
            context, new SqlExecutionPlanPolicy()).Sql);
    }

    private sealed class Observer<T>(Action<T> next) : IObserver<T>
    {
        public void OnNext(T value) => next(value);
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }
}
