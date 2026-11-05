using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Ddl;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Deterministic, offline adversarial tests of the three executable compilation
/// boundaries. No SQL is sent to a database. An attempted second statement must
/// never turn into a compiled single-statement plan; an attack-shaped literal
/// must remain a parameter, not become executable SQL.
/// </summary>
public sealed class CoreAdversarialInjectionMatrixTests
{
    private static readonly SqlAgentToolType[] Providers =
    [
        SqlAgentToolType.Postgres,
        SqlAgentToolType.MySQL,
        SqlAgentToolType.MsSqlServer,
        SqlAgentToolType.Oracle,
        SqlAgentToolType.Sqlite,
        SqlAgentToolType.Firebird
    ];

    private static readonly string[] AttackStatements =
    [
        "DROP TABLE users",
        "DELETE FROM users WHERE id = 1",
        "UPDATE users SET name = 'owned' WHERE id = 1",
        "INSERT INTO users (id) VALUES (9999)",
        "CREATE TABLE injected (id INTEGER)",
        "GRANT ALL ON users TO PUBLIC",
        "SELECT id FROM secrets",
        "TRUNCATE TABLE users"
    ];

    private static readonly string[] StatementSeparators =
    [
        "; ",
        ";\n",
        "; /* disguised boundary */ ",
        "; -- disguised boundary\r\n"
    ];

    private static readonly string[] QueryPrefixes =
    [
        "SELECT id FROM users WHERE id = 7",
        "WITH visible AS (SELECT id FROM users) SELECT id FROM visible"
    ];

    private static readonly string[] DmlPrefixes =
    [
        "UPDATE users SET name = 'safe' WHERE id = 7",
        "DELETE FROM users WHERE id = 7"
    ];

    private static readonly string[] MalformedTails =
    [
        " /* unterminated",
        " 'unterminated",
        " \0",
        " \u0001"
    ];

    private static readonly string[] LiteralPayloads =
    [
        "'; DROP TABLE users; --",
        "admin' OR '1'='1",
        "x' UNION SELECT id FROM secrets --",
        "'); DELETE FROM users WHERE id = 7; --",
        "x' /* comment */ OR '1'='1",
        "--\nDROP TABLE users",
        "/* buried */ UNION SELECT id FROM secrets",
        "x'; GRANT ALL ON users TO PUBLIC; --",
        "x'; TRUNCATE TABLE users; --",
        "x' AND EXISTS (SELECT 1 FROM secrets) --",
        "O'Reilly",
        "測試' OR '一'='一"
    ];

    [Fact]
    public void StatementStacking_NeverEscapesQueryOrDmlCompileBoundary()
    {
        var attempts = 0;
        foreach (var provider in Providers)
        foreach (var attack in AttackStatements)
        foreach (var separator in StatementSeparators)
        {
            foreach (var prefix in QueryPrefixes)
            {
                var sql = prefix + separator + attack;
                AssertRejected(
                    () => SqlCoreFacade.TryCompileQuery(
                        sql, provider, provider, Validation(),
                        new SqlExecutionPlanPolicy(100)),
                    provider + " Query " + sql);
                attempts++;
            }

            foreach (var prefix in DmlPrefixes)
            {
                var sql = prefix + separator + attack;
                AssertRejected(
                    () => SqlCoreFacade.TryCompileDml(
                        sql, provider, provider, Validation()),
                    provider + " DML " + sql);
                attempts++;
            }
        }

        // Minimum floor, not a random fuzz count: 6 x 8 x 4 x (2 + 2).
        Assert.Equal(768, attempts);
    }

    [Fact]
    public void StatementStacking_NeverEscapesSingleDdlCompileBoundary()
    {
        var attempts = 0;
        foreach (var provider in Providers)
        foreach (var attack in AttackStatements)
        foreach (var separator in StatementSeparators)
        {
            var baseSql = provider == SqlAgentToolType.MsSqlServer
                ? "CREATE TABLE widgets (id INT)"
                : "CREATE TABLE widgets (id INTEGER)";
            var sql = baseSql + separator + attack;
            var options = new DdlCompilationOptions(provider, provider);
            var first = SqlCoreFacade.TryCompileDdl(sql, options);
            var second = SqlCoreFacade.TryCompileDdl(sql, options);

            Assert.False(first.Success, provider + " DDL " + sql);
            Assert.False(second.Success, provider + " DDL repeat " + sql);
            Assert.Null(first.Value);
            Assert.Null(second.Value);
            Assert.False(string.IsNullOrWhiteSpace(first.ErrorCode));
            Assert.Equal(first.ErrorCode, second.ErrorCode);
            Assert.Equal(first.ErrorMessage, second.ErrorMessage);
            attempts++;
        }

        // The batch compiler is a separate, explicitly authorized API.
        Assert.Equal(192, attempts);
    }

    [Fact]
    public void UnterminatedLexemesAndControlCharacters_NeverReachRendering()
    {
        var attempts = 0;
        foreach (var provider in Providers)
        foreach (var tail in MalformedTails)
        {
            var query = "SELECT id FROM users WHERE id = 7" + tail;
            AssertRejected(
                () => SqlCoreFacade.TryCompileQuery(
                    query, provider, provider, Validation(),
                    new SqlExecutionPlanPolicy(100)),
                provider + " malformed query");
            attempts++;

            var dml = "DELETE FROM users WHERE id = 7" + tail;
            AssertRejected(
                () => SqlCoreFacade.TryCompileDml(
                    dml, provider, provider, Validation()),
                provider + " malformed DML");
            attempts++;
        }

        Assert.Equal(48, attempts);
    }

    [Fact]
    public void SmuggledPhysicalTables_AreNeverAuthorizedByCteOrSubqueryNames()
    {
        var queries = new[]
        {
            "SELECT id FROM secrets",
            "WITH visible AS (SELECT id FROM secrets) SELECT id FROM visible",
            "SELECT u.id FROM users u JOIN secrets s ON u.id = s.id",
            "SELECT id FROM users WHERE id IN (SELECT id FROM secrets)"
        };

        var attempts = 0;
        foreach (var provider in Providers)
        foreach (var sql in queries)
        {
            AssertRejected(
                () => SqlCoreFacade.TryCompileQuery(
                    sql, provider, provider, Validation(),
                    new SqlExecutionPlanPolicy(100)),
                provider + " denied physical table " + sql);
            attempts++;
        }

        Assert.Equal(24, attempts);
    }

    [Fact]
    public void CommentObfuscation_DoesNotBypassDefaultUnboundedDmlPolicy()
    {
        var cases = new[]
        {
            ("UPDATE users SET name = 'x' WHERE 1 = 1 /* allowed? */",
                "SQL_POLICY_UPDATE_UNBOUNDED_PREDICATE"),
            ("DELETE FROM users WHERE 1 = 1 /* allowed? */",
                "SQL_POLICY_DELETE_UNBOUNDED_PREDICATE"),
            ("UPDATE users SET name = 'x' WHERE id = 7 OR 1 = 1 /* allowed? */",
                "SQL_POLICY_UPDATE_UNBOUNDED_PREDICATE"),
            ("DELETE FROM users WHERE id = 7 OR 1 = 1 /* allowed? */",
                "SQL_POLICY_DELETE_UNBOUNDED_PREDICATE")
        };

        var attempts = 0;
        foreach (var provider in Providers)
        foreach (var (sql, code) in cases)
        {
            var result = SqlCoreFacade.TryCompileDml(
                sql, provider, provider, Validation());
            Assert.False(result.Success, provider + " " + sql);
            Assert.Null(result.Value);
            Assert.Contains(
                result.TypedDiagnostics,
                diagnostic => diagnostic.Code == code &&
                    diagnostic.Stage == SqlDiagnosticStage.Policy &&
                    diagnostic.Category == SqlDiagnosticCategory.Policy);
            attempts++;
        }

        Assert.Equal(24, attempts);
    }

    [Fact]
    public void SqlInjectionLookingLiterals_StayParameterizedAcrossSixProviders()
    {
        var attempts = 0;
        foreach (var provider in Providers)
        foreach (var payload in LiteralPayloads)
        {
            var literal = QuoteSqlLiteral(payload);
            var query = SqlCoreFacade.CompileQuery(
                "SELECT id FROM users WHERE name = " + literal + " AND id = 7",
                provider, provider, Validation(), new SqlExecutionPlanPolicy(100));
            AssertParameterized(query, payload, provider + " Query");
            attempts++;

            var dml = SqlCoreFacade.CompileDml(
                "UPDATE users SET name = " + literal + " WHERE id = 7",
                provider, provider, Validation());
            AssertParameterized(dml, payload, provider + " DML");
            attempts++;
        }

        // Legal data, even attack-shaped data, must not be blacklisted.
        Assert.Equal(144, attempts);
    }

    [Fact]
    public void EveryNegativeCampaign_HasAWorkingPositiveControl()
    {
        foreach (var provider in Providers)
        {
            var query = SqlCoreFacade.CompileQuery(
                "WITH visible AS (SELECT id FROM users) SELECT id FROM visible",
                provider, provider, Validation(), new SqlExecutionPlanPolicy(100));
            Assert.False(string.IsNullOrWhiteSpace(query.Sql));

            var dml = SqlCoreFacade.CompileDml(
                "DELETE FROM users WHERE id = 7",
                provider, provider, Validation());
            Assert.False(string.IsNullOrWhiteSpace(dml.Sql));

            var ddl = SqlCoreFacade.CompileDdl(
                provider == SqlAgentToolType.MsSqlServer
                    ? "CREATE TABLE widgets (id INT)"
                    : "CREATE TABLE widgets (id INTEGER)",
                new DdlCompilationOptions(provider, provider));
            Assert.False(string.IsNullOrWhiteSpace(ddl.Command.Sql));
            Assert.False(ddl.AuthorizesExecution);
        }
    }

    private static void AssertRejected(
        Func<SqlCoreTryResult<CompiledSqlCommand>> compile,
        string label)
    {
        var first = compile();
        var second = compile();
        Assert.False(first.Success, label);
        Assert.False(second.Success, label + " (repeat)");
        Assert.Null(first.Value);
        Assert.Null(second.Value);
        Assert.False(string.IsNullOrWhiteSpace(first.ErrorCode), label);
        Assert.Equal(first.ErrorCode, second.ErrorCode);
        Assert.Equal(first.ErrorMessage, second.ErrorMessage);

        var evidence = Assert.IsType<SqlCompileEvidence>(first.CompileEvidence);
        var repeat = Assert.IsType<SqlCompileEvidence>(second.CompileEvidence);
        Assert.Equal(SqlCompileVerdict.Rejected, evidence.Verdict);
        Assert.NotEqual(SqlCompileDecisionBoundary.Completed, evidence.DecisionBoundary);
        Assert.Null(evidence.PlanFingerprint);
        Assert.Equal(evidence.DecisionBoundary, repeat.DecisionBoundary);
        Assert.Equal(evidence.DecisionCode, repeat.DecisionCode);
        Assert.Equal(evidence.EvidenceFingerprint, repeat.EvidenceFingerprint);
    }

    private static void AssertParameterized(
        CompiledSqlCommand command,
        string payload,
        string label)
    {
        Assert.Contains(
            command.Parameters,
            parameter => Equals(payload, parameter.Value));
        Assert.DoesNotContain(payload, command.Sql, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(command.PlanFingerprint), label);
        Assert.Equal(SqlCompileVerdict.Translated,
            Assert.IsType<SqlCompileEvidence>(command.CompileEvidence).Verdict);
    }

    private static SqlPlanValidationContext Validation() =>
        new("adversarial-injection-matrix-v1",
            new HashSet<string>(
                ["users"],
                StringComparer.OrdinalIgnoreCase));

    private static string QuoteSqlLiteral(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
