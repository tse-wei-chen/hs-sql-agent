using System.Diagnostics;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Numeric-token canonicalization must participate in the same deterministic
/// lexical work quota as SQL source scanning, for every public script dialect.
/// </summary>
public sealed class SqlCoreNumericLexicalWorkBudgetTests
{
    public static IEnumerable<object[]> SixDialects() =>
        Enum.GetValues<SqlAgentToolType>().Select(value => new object[] { value });

    [Theory]
    [MemberData(nameof(SixDialects))]
    public void CanonicalDecimalScanning_IsChargedInScriptLexicalStage(
        SqlAgentToolType provider)
    {
        const string literal = "0.0000000000000000000000000001";
        var sql = "SELECT " + string.Join(", ", Enumerable.Repeat(literal, 96));
        var ownerThread = Environment.CurrentManagedThreadId;
        var samples = new List<long>();
        var subscriptions = new List<IDisposable>();

        using var all = DiagnosticListener.AllListeners.Subscribe(
            new Observer<DiagnosticListener>(listener =>
            {
                if (listener.Name != "HsSqlAgent.SqlCore.Compiler") return;
                lock (subscriptions)
                    subscriptions.Add(listener.Subscribe(
                        new Observer<KeyValuePair<string, object?>>(eventData =>
                        {
                            if (eventData.Key != "StageCompleted"
                                || eventData.Value is null
                                || Environment.CurrentManagedThreadId != ownerThread) return;
                            var payload = eventData.Value;
                            if (!Equals(Property(payload, "Stage"), "ScriptLexical")
                                || !Equals(Property(payload, "Success"), true)) return;
                            samples.Add(Convert.ToInt64(Property(payload, "WorkUnits")));
                        })));
            }));
        try
        {
            // Script classification must remain available for all six
            // providers. The important contract is metered lexing, not the
            // independent status of every later SQL grammar extension.
            SqlCoreFacade.ParseScript(sql, provider,
                TestContext.Current.CancellationToken);
            Assert.Contains(samples, units => units > sql.Length * 2L);
        }
        finally
        {
            lock (subscriptions)
                foreach (var subscription in subscriptions) subscription.Dispose();
        }
    }

    [Fact]
    public void GiantLeadingZeroLiteral_ExhaustsNumericWorkBudgetBeforeAllocationHeavyNormalization()
    {
        // Under the independent 4 Mi-character input and 131072 token caps,
        // but canonicalization would otherwise revisit ~4 million digits.
        var sql = "SELECT " + new string('0', 4_010_000);

        var error = Assert.Throws<SqlParseException>(() =>
            SqlCoreFacade.ParseScript(sql, SqlAgentToolType.Postgres,
                TestContext.Current.CancellationToken));

        Assert.Equal("SQL_COMPILER_STAGE_WORK_LIMIT", error.Diagnostic?.Code);
        Assert.Equal(SqlDiagnosticStage.Lexical, error.Diagnostic?.Stage);
        Assert.Equal(SqlDiagnosticCategory.Resource, error.Diagnostic?.Category);

        // No ambient quota may poison the next independent compilation.
        Assert.Single(SqlCoreFacade.ParseScript(
            "SELECT 1", SqlAgentToolType.Postgres,
            TestContext.Current.CancellationToken).Statements);
    }

    private static object? Property(object payload, string name) =>
        payload.GetType().GetProperty(name)?.GetValue(payload);

    private sealed class Observer<T>(Action<T> action) : IObserver<T>
    {
        public void OnNext(T item) => action(item);
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }
}
