using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Ast;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Microsoft.FSharp.Core;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreScopedSchemaContinuationTests
{
    public static IEnumerable<object[]> DeepCases()
    {
        foreach (var provider in Enum.GetValues<SqlAgentToolType>())
        foreach (var shape in new[] { "function-query", "function-chain", "outer-join", "multi-cte" })
        foreach (var depth in new[] { 128, 512, 2048 })
            yield return new object[] { provider, shape, depth };
        foreach (var depth in new[] { 128, 512, 2048 })
            yield return new object[] { SqlAgentToolType.Postgres, "lateral-nested-prefix", depth };
    }

    public static IEnumerable<object[]> NegativeCases()
    {
        foreach (var provider in Enum.GetValues<SqlAgentToolType>())
        foreach (var shape in new[] { "function-query", "function-chain", "outer-join", "multi-cte" })
        foreach (var typed in new[] { false, true })
            yield return new object[] { provider, shape, typed };
    }

    public static IEnumerable<object[]> SharedScopeCases()
    {
        foreach (var provider in Enum.GetValues<SqlAgentToolType>())
        foreach (var typed in new[] { false, true })
            yield return new object[] { provider, typed };
    }

    private static string Query(string shape, int depth, string leaf = "id")
    {
        if (shape == "function-chain")
        {
            var expression = leaf;
            for (var index = 0; index < depth; index++) expression = "COALESCE(" + expression + ", " + (index + 1) + ")";
            return "SELECT " + expression + " AS id FROM users";
        }
        if (shape == "lateral-nested-prefix")
        {
            var child = "SELECT a.id AS id";
            for (var index = 0; index < depth; index++)
                child = "SELECT b.id FROM users u " +
                    "JOIN LATERAL (SELECT (SELECT u.id) AS id) a ON TRUE " +
                    "JOIN LATERAL (" + child + ") b ON TRUE";
            return child;
        }
        var sql = "SELECT " + leaf + " AS id FROM users";
        for (var index = 0; index < depth; index++)
            sql = shape switch
            {
                "function-query" => "SELECT COALESCE((" + sql + "), " + (index + 1) + ") AS id",
                "outer-join" => "SELECT d.id FROM users AS u LEFT JOIN (" + sql + ") AS d ON u.id = d.id",
                "multi-cte" => "WITH a AS (" + sql + "), b AS (SELECT id FROM a) SELECT id FROM b",
                _ => throw new ArgumentOutOfRangeException(nameof(shape))
            };
        return sql;
    }

    private static SqlSchemaSnapshot Snapshot(SqlAgentToolType provider)
    {
        string Name(string value) => provider is SqlAgentToolType.Oracle or SqlAgentToolType.Firebird
            ? value.ToUpperInvariant() : value;
        return new SqlSchemaSnapshot(provider, "scoped-proof-v1", new[] {
            SqlSchemaTable.WithTypes(Name("users"), new[] {
                new SqlSchemaColumn(Name("id"), SqlSchemaValueKind.Numeric),
                new SqlSchemaColumn(Name("name"), SqlSchemaValueKind.Text) }) });
    }

    private static object Invoke(string module, string name, params object?[] arguments)
    {
        var type = typeof(SqlCoreFacade).Assembly.GetType(
            "HsSqlAgent.SqlCore.Rewrite." + module, throwOnError: true)!;
        var method = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
        if (method.IsGenericMethodDefinition) method = method.MakeGenericMethod(typeof(object));
        try { return method.Invoke(null, arguments)!; }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    private sealed class Function(Func<object> action) : FSharpFunc<Unit, object>
    {
        public override object Invoke(Unit _) => action();
    }

    private static object Scope(Func<object> action) => Invoke(
        "RewriteResourceBudget", "withCompilation", new Function(action));

    private static object Stage(string name, SqlDiagnosticStage stage, Func<object> action) => Invoke(
        "RewriteResourceBudget", "withStage", name, stage, new Function(action));

    private static object Bind(string sql, SqlAgentToolType provider)
    {
        var parsed = CoreSqlTextParser.ParseQuery(sql, provider);
        var native = Invoke("RewriteLegacyAstAdapter", "toParsed", parsed.Statement);
        return Invoke("RewriteBinder", "bind", provider, native);
    }

    [Theory]
    [MemberData(nameof(DeepCases))]
    public void GeneralGraphs_CompletePublicCompilerOn256KiBWithExactBindings(
        SqlAgentToolType provider, string shape, int depth)
    {
        var sql = Query(shape, depth);
        var source = SqlAgentToolType.Postgres;
        var context = new SqlPlanValidationContext("general-document-v1").WithSchemaSnapshot(Snapshot(source));
        CompiledSqlCommand? command = null;
        Exception? failure = null;
        long allocated = 0, renderingAllocated = -1;
        var ownerThread = -1;
        var successful = new HashSet<string>();
        var subscriptions = new List<IDisposable>();
        using var listener = DiagnosticListener.AllListeners.Subscribe(new Observer<DiagnosticListener>(sourceListener =>
        {
            if (sourceListener.Name != "HsSqlAgent.SqlCore.Compiler") return;
            lock (subscriptions) subscriptions.Add(sourceListener.Subscribe(
                new Observer<KeyValuePair<string, object?>>(measurement =>
                {
                    if (Environment.CurrentManagedThreadId != ownerThread || measurement.Key != "StageCompleted"
                        || measurement.Value is not { } data) return;
                    object? Get(string name) => data.GetType().GetProperty(name)!.GetValue(data);
                    var stage = (string)Get("Stage")!;
                    if (Equals(Get("Success"), true)) successful.Add(stage);
                    if (stage == "Rendering") renderingAllocated = Convert.ToInt64(Get("AllocatedBytes"));
                })));
        }));
        try
        {
            var worker = new Thread(() =>
            {
                ownerThread = Environment.CurrentManagedThreadId;
                var before = GC.GetAllocatedBytesForCurrentThread();
                try { command = SqlCoreFacade.CompileQuery(sql, source, provider, context, new SqlExecutionPlanPolicy()); }
                catch (Exception error) { failure = error; }
                finally { allocated = GC.GetAllocatedBytesForCurrentThread() - before; }
            }, 256 * 1024) { IsBackground = true };
            worker.Start();
            Assert.True(worker.Join(TimeSpan.FromSeconds(30)), "General compiler worker timed out.");
            if (shape == "multi-cte" && provider is SqlAgentToolType.Oracle or SqlAgentToolType.Firebird or SqlAgentToolType.MsSqlServer)
            {
                var unsupported = Assert.IsType<SqlCompilationException>(failure);
                Assert.Equal("SQL_TARGET_CAPABILITY_REJECTED", unsupported.Diagnostic?.Code);
                Assert.Null(command);
                return;
            }
            Assert.True(failure is null, failure?.ToString());
            Assert.NotNull(command);
            Assert.NotEmpty(command.Sql);
            foreach (var stage in new[] { "Parsing", "Binding", "SchemaBinding", "SchemaType",
                         "Normalization", "Validation", "Policy", "Rendering" })
                Assert.Contains(stage, successful);
            var parameterCount = shape == "lateral-nested-prefix" ? depth * 2
                : shape is "function-query" or "function-chain" ? depth : 0;
            Assert.Equal(parameterCount, command.Parameters.Length);
            if (shape is "function-query" or "function-chain")
                Assert.Equal(Enumerable.Range(1, depth).Select(value => (long)value),
                    command.Parameters.Select(parameter => Convert.ToInt64(parameter.Value)));
            if (shape == "lateral-nested-prefix")
                Assert.All(command.Parameters, parameter => Assert.Equal(true, parameter.Value));
            if (depth == 2048)
            {
                Assert.InRange(renderingAllocated, 0, 80_000_000);
                Assert.InRange(allocated, 0, 240_000_000);
            }
        }
        finally
        {
            lock (subscriptions) foreach (var subscription in subscriptions) subscription.Dispose();
        }
    }

    [Theory]
    [MemberData(nameof(DeepCases))]
    public void BothStrictProofPhases_SucceedOn256KiBWithoutSkippingScopeOrTypeWork(
        SqlAgentToolType provider, string shape, int depth)
    {
        // Isolate the proof phases from rendering: an unsupported provider's
        // nested-WITH output grammar must still be refused later by its own
        // capability gate. Bound AST construction is tested separately.
        var bound = Bind(Query(shape, depth), provider);
        var snapshot = Snapshot(provider);
        Exception? failure = null;
        var allocated = 0L;
        var ownerThread = -1;
        var samples = new Dictionary<string, long>();
        var subscriptions = new List<IDisposable>();
        using var listener = DiagnosticListener.AllListeners.Subscribe(new Observer<DiagnosticListener>(source =>
        {
            if (source.Name != "HsSqlAgent.SqlCore.Compiler") return;
            lock (subscriptions) subscriptions.Add(source.Subscribe(
                new Observer<KeyValuePair<string, object?>>(measurement =>
                {
                    if (Environment.CurrentManagedThreadId != ownerThread || measurement.Key != "StageCompleted"
                        || measurement.Value is not { } data) return;
                    object? Get(string name) => data.GetType().GetProperty(name)!.GetValue(data);
                    if (Equals(Get("Success"), true))
                        samples[(string)Get("Stage")!] = Convert.ToInt64(Get("WorkUnits"));
                })));
        }));
        try
        {
            var worker = new Thread(() =>
            {
                ownerThread = Environment.CurrentManagedThreadId;
                var before = GC.GetAllocatedBytesForCurrentThread();
                try
                {
                    Scope(() =>
                    {
                        Assert.Same(bound, Stage("SchemaBinding", SqlDiagnosticStage.Binding,
                            () => Invoke("RewriteSchemaValidation", "verify", provider, snapshot, bound)));
                        Assert.Same(bound, Stage("SchemaType", SqlDiagnosticStage.SemanticValidation,
                            () => Invoke("RewriteSchemaTypeValidation", "verify", provider, snapshot, bound)));
                        return bound;
                    });
                }
                catch (Exception error) { failure = error; }
                finally { allocated = GC.GetAllocatedBytesForCurrentThread() - before; }
            }, 256 * 1024) { IsBackground = true };
            worker.Start();
            Assert.True(worker.Join(TimeSpan.FromSeconds(30)), "Strict proof worker timed out.");
            Assert.True(failure is null, failure?.ToString());
            Assert.True(samples.GetValueOrDefault("SchemaBinding") >= depth, "Missing real schema work.");
            Assert.True(samples.GetValueOrDefault("SchemaType") >= depth, "Missing real type work.");
            if (depth == 2048) Assert.True(allocated <= 180_000_000,
                $"Both strict proof phases allocated {allocated:N0} bytes.");
        }
        finally
        {
            lock (subscriptions) foreach (var subscription in subscriptions) subscription.Dispose();
        }
    }

    [Theory]
    [MemberData(nameof(NegativeCases))]
    public void SuspendedScopes_KeepInnermostNameAndTypeFailures(
        SqlAgentToolType provider, string shape, bool typed)
    {
        var bound = Bind(Query(shape, 96, typed ? "name + 1" : "missing_column"), provider);
        var snapshot = Snapshot(provider);
        var error = Assert.Throws<SqlCompilationException>(() => Scope(() =>
        {
            Stage("SchemaBinding", SqlDiagnosticStage.Binding,
                () => Invoke("RewriteSchemaValidation", "verify", provider, snapshot, bound));
            return Stage("SchemaType", SqlDiagnosticStage.SemanticValidation,
                () => Invoke("RewriteSchemaTypeValidation", "verify", provider, snapshot, bound));
        }));
        Assert.Equal(typed ? "SQL_SCHEMA_TYPE_MISMATCH" : "SQL_SCHEMA_COLUMN_NOT_FOUND", error.Diagnostic?.Code);
    }

    [Theory]
    [MemberData(nameof(SharedScopeCases))]
    public void SameQueryObject_InDifferentOuterScopes_DoesNotReuseProofs(
        SqlAgentToolType provider, bool typed)
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT (SELECT d.id) + 1 AS value FROM users AS d UNION ALL " +
            "SELECT (SELECT d.id) + 1 AS value FROM other AS d", provider);
        var root = Assert.IsType<QueryStatement>(parsed.Statement);
        var first = Assert.IsType<BinaryExpr>(root.Head.Select[0].Expression);
        var child = Assert.IsType<SubqueryExpr>(first.Left);
        var second = Assert.IsType<SelectStatement>(root.SetOperations[0].Query);
        second.Select[0].Expression = new BinaryExpr(new SubqueryExpr(child.Query, SourceSpan.Unknown),
            "+", new LiteralExpr(1, SourceSpan.Unknown), SourceSpan.Unknown);
        var native = Invoke("RewriteLegacyAstAdapter", "toParsed", root);
        var bound = Invoke("RewriteBinder", "bind", provider, native);
        string Name(string value) => provider is SqlAgentToolType.Oracle or SqlAgentToolType.Firebird
            ? value.ToUpperInvariant() : value;
        var snapshot = new SqlSchemaSnapshot(provider, "shared-outer-scope-v1", new[] {
            SqlSchemaTable.WithTypes(Name("users"), new[] {
                new SqlSchemaColumn(Name("id"), SqlSchemaValueKind.Numeric) }),
            SqlSchemaTable.WithTypes(Name("other"), new[] {
                new SqlSchemaColumn(Name(typed ? "id" : "name"), SqlSchemaValueKind.Text) }) });
        var error = Assert.Throws<SqlCompilationException>(() => Scope(() =>
        {
            Stage("SchemaBinding", SqlDiagnosticStage.Binding,
                () => Invoke("RewriteSchemaValidation", "verify", provider, snapshot, bound));
            return Stage("SchemaType", SqlDiagnosticStage.SemanticValidation,
                () => Invoke("RewriteSchemaTypeValidation", "verify", provider, snapshot, bound));
        }));
        Assert.Equal(typed ? "SQL_SCHEMA_TYPE_MISMATCH" : "SQL_SCHEMA_COLUMN_NOT_FOUND", error.Diagnostic?.Code);
    }

    private sealed class Observer<T>(Action<T> next) : IObserver<T>
    {
        public void OnNext(T value) => next(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
