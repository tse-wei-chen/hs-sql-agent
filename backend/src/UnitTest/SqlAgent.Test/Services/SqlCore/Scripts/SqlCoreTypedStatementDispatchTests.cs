using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Ddl;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// U1: lexical candidates cannot become executable-family proofs.
/// These tests run against all six source dialects without live databases.
/// </summary>
public sealed class SqlCoreTypedStatementDispatchTests
{
    public static TheoryData<SqlAgentToolType, string, SqlScriptStatementKind, SqlScriptParseStatus> FamilyMatrix()
    {
        var matrix = new TheoryData<SqlAgentToolType, string, SqlScriptStatementKind, SqlScriptParseStatus>();
        foreach (var provider in new[]
        {
            SqlAgentToolType.Postgres, SqlAgentToolType.MySQL, SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.Sqlite, SqlAgentToolType.Oracle, SqlAgentToolType.Firebird
        })
        {
            matrix.Add(provider, "SELECT 1", SqlScriptStatementKind.Query, SqlScriptParseStatus.Parsed);
            matrix.Add(provider, "UPDATE users SET name = 'Ada' WHERE id = 1", SqlScriptStatementKind.Mutation, SqlScriptParseStatus.Parsed);
            matrix.Add(provider, "CREATE TABLE dispatch_example (id INTEGER)", SqlScriptStatementKind.Schema, SqlScriptParseStatus.Unsupported);
            matrix.Add(provider, "GRANT SELECT ON users TO reader", SqlScriptStatementKind.Security, SqlScriptParseStatus.Unsupported);
            matrix.Add(provider, "COMMIT", SqlScriptStatementKind.Transaction, SqlScriptParseStatus.Unsupported);
            matrix.Add(provider, "SET search_path TO public", SqlScriptStatementKind.Session, SqlScriptParseStatus.Unsupported);
            matrix.Add(provider, "EXPLAIN SELECT 1", SqlScriptStatementKind.Utility, SqlScriptParseStatus.Unsupported);
            matrix.Add(provider, "CALL dispatch_proc()", SqlScriptStatementKind.Procedural, SqlScriptParseStatus.Unsupported);
        }
        return matrix;
    }

    [Theory]
    [MemberData(nameof(FamilyMatrix))]
    public void EightFamilies_SeparateParseProofFromLexicalCandidate(
        SqlAgentToolType provider, string sql, SqlScriptStatementKind expected, SqlScriptParseStatus status)
    {
        var script = SqlCoreFacade.ParseScript(" \n" + sql + ";", provider, TestContext.Current.CancellationToken);
        var statement = Assert.Single(script.Statements);
        Assert.Equal(expected, statement.Kind);
        Assert.Equal(status, statement.Status);
        Assert.Equal(sql, statement.Text);
        Assert.Equal(2, statement.Span.Start);
        Assert.Equal(sql.Length, statement.Span.Length);
        if (status == SqlScriptParseStatus.Parsed)
        {
            Assert.Null(statement.Diagnostic);
            Assert.Empty(script.Diagnostics);
        }
        else
        {
            Assert.NotNull(statement.Diagnostic);
            Assert.Equal(statement.Diagnostic, Assert.Single(script.Diagnostics));
        }
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void SingleCompileAndScript_AgreeOnProvenQueryAndMutation(SqlAgentToolType provider)
    {
        const string query = "SELECT id FROM users WHERE id = 1";
        const string mutation = "UPDATE users SET name = 'Ada' WHERE id = 1";
        var script = SqlCoreFacade.ParseScript(query + "; " + mutation, provider, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { SqlScriptStatementKind.Query, SqlScriptStatementKind.Mutation },
            script.Statements.Select(x => x.Kind));
        Assert.All(script.Statements, x => Assert.Equal(SqlScriptParseStatus.Parsed, x.Status));

        var context = new SqlPlanValidationContext("u1-family-equivalence");
        var compiledQuery = SqlCoreFacade.CompileQuery(query, provider, provider, context,
            new SqlExecutionPlanPolicy(10), TestContext.Current.CancellationToken);
        var compiledMutation = SqlCoreFacade.CompileDml(mutation, provider, provider, context,
            TestContext.Current.CancellationToken);
        Assert.Equal(SqlStatementKind.Query, compiledQuery.Kind);
        Assert.Equal(SqlStatementKind.Update, compiledMutation.Kind);
        Assert.NotNull(compiledQuery.CompileEvidence);
        Assert.NotNull(compiledMutation.CompileEvidence);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void WrongFamily_IsRejectedBeforeOtherDomainPolicy(SqlAgentToolType provider)
    {
        // The UPDATE is intentionally unbounded. Query compilation must not
        // run the Mutation engine far enough to throw the Mutation WHERE policy.
        var context = new SqlPlanValidationContext("u1-wrong-family");
        Assert.Throws<ArgumentException>(() =>
            SqlCoreFacade.CompileQuery("UPDATE users SET name = 'Ada'", provider, provider,
                context, new SqlExecutionPlanPolicy(10), TestContext.Current.CancellationToken));
        Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileDml("SELECT id FROM users", provider, provider,
                context, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void WithMutation_IsNotRoutedAsQuery()
    {
        const string sql = "WITH ids AS (SELECT id FROM users WHERE id = 1) UPDATE users SET name = 'Ada' WHERE id = 1";
        var statement = Assert.Single(SqlCoreFacade.ParseScript(sql, SqlAgentToolType.Postgres,
            TestContext.Current.CancellationToken).Statements);
        Assert.Equal(SqlScriptStatementKind.Mutation, statement.Kind);
        Assert.Equal(SqlScriptParseStatus.Parsed, statement.Status);
    }

    [Fact]
    public void MalformedWith_HasNoInventedQueryOrMutationProof()
    {
        const string sql = "WITH ids AS (SELECT 1) UPSERT users SET id = 1";
        var statement = Assert.Single(SqlCoreFacade.ParseScript(sql, SqlAgentToolType.Postgres,
            TestContext.Current.CancellationToken).Statements);
        Assert.Equal(SqlScriptStatementKind.Unknown, statement.Kind);
        Assert.Equal(SqlScriptParseStatus.Invalid, statement.Status);
        Assert.NotNull(statement.Diagnostic);
    }

    [Fact]
    public void UnknownStatement_IsNotClaimedAsRecognizedOrParsed()
    {
        var statement = Assert.Single(SqlCoreFacade.ParseScript("ZORP 1", SqlAgentToolType.Postgres,
            TestContext.Current.CancellationToken).Statements);
        Assert.Equal(SqlScriptStatementKind.Unknown, statement.Kind);
        Assert.Equal(SqlScriptParseStatus.Unsupported, statement.Status);
        Assert.Equal("SQL_SCRIPT_UNKNOWN_STATEMENT", statement.Diagnostic?.Code);
    }

    [Fact]
    public void SqlServerBeginAndGo_AreContextualAndBatchBoundariesRemainExact()
    {
        const string sql = "BEGIN TRAN;\nGO\nBEGIN SELECT 1; END;\nGO\nSELECT 2";
        var parsed = SqlCoreFacade.ParseScript(sql, SqlAgentToolType.MsSqlServer,
            TestContext.Current.CancellationToken);
        Assert.Equal(3, parsed.BatchCount);
        Assert.Equal(3, parsed.Statements.Count);
        Assert.Equal(new[] { 0, 1, 2 }, parsed.Statements.Select(x => x.BatchIndex));
        Assert.Equal(new[] {
            SqlScriptStatementKind.Transaction, SqlScriptStatementKind.Procedural,
            SqlScriptStatementKind.Query
        }, parsed.Statements.Select(x => x.Kind));
        foreach (var statement in parsed.Statements)
            Assert.Equal(statement.Text, sql.Substring(statement.Span.Start, statement.Span.Length));
    }

    public static TheoryData<SqlAgentToolType, string, SqlScriptStatementKind, SqlScriptParseStatus> NegativeMatrix()
    {
        var matrix = new TheoryData<SqlAgentToolType, string, SqlScriptStatementKind, SqlScriptParseStatus>();
        foreach (var provider in new[]
        {
            SqlAgentToolType.Postgres, SqlAgentToolType.MySQL, SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.Sqlite, SqlAgentToolType.Oracle, SqlAgentToolType.Firebird
        })
        {
            matrix.Add(provider, "SELECT FROM users", SqlScriptStatementKind.Query, SqlScriptParseStatus.Invalid);
            matrix.Add(provider, "UPDATE users SET name =", SqlScriptStatementKind.Mutation, SqlScriptParseStatus.Invalid);
            // These six domains have analyzers but no executable proof in
            // ParseScript: incomplete lexical candidates remain Unsupported.
            matrix.Add(provider, "CREATE TABLE", SqlScriptStatementKind.Schema, SqlScriptParseStatus.Unsupported);
            matrix.Add(provider, "GRANT", SqlScriptStatementKind.Security, SqlScriptParseStatus.Unsupported);
            matrix.Add(provider, "COMMIT nonsense", SqlScriptStatementKind.Transaction, SqlScriptParseStatus.Unsupported);
            matrix.Add(provider, "SET", SqlScriptStatementKind.Session, SqlScriptParseStatus.Unsupported);
            matrix.Add(provider, "EXPLAIN", SqlScriptStatementKind.Utility, SqlScriptParseStatus.Unsupported);
            matrix.Add(provider, "CALL", SqlScriptStatementKind.Procedural, SqlScriptParseStatus.Unsupported);
        }
        return matrix;
    }

    [Theory]
    [MemberData(nameof(NegativeMatrix))]
    public void NegativeFamilyMatrix_NeverPromotesInvalidOrOpaqueSQLToParseProof(
        SqlAgentToolType provider, string sql, SqlScriptStatementKind expected, SqlScriptParseStatus status)
    {
        var statement = Assert.Single(SqlCoreFacade.ParseScript(sql, provider,
            TestContext.Current.CancellationToken).Statements);
        Assert.Equal(expected, statement.Kind);
        Assert.Equal(status, statement.Status);
        Assert.NotNull(statement.Diagnostic);
        Assert.Equal(SqlDiagnosticStage.Parse, statement.Diagnostic.Stage);
        Assert.Equal(sql, statement.Text);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, SqlScriptStatementKind.Transaction)]
    [InlineData(SqlAgentToolType.MySQL, SqlScriptStatementKind.Transaction)]
    [InlineData(SqlAgentToolType.Sqlite, SqlScriptStatementKind.Transaction)]
    [InlineData(SqlAgentToolType.MsSqlServer, SqlScriptStatementKind.Procedural)]
    [InlineData(SqlAgentToolType.Oracle, SqlScriptStatementKind.Procedural)]
    [InlineData(SqlAgentToolType.Firebird, SqlScriptStatementKind.Procedural)]
    public void BareBegin_UsesSourceDialectContext(
        SqlAgentToolType provider, SqlScriptStatementKind family)
    {
        var statement = Assert.Single(SqlCoreFacade.ParseScript("BEGIN", provider,
            TestContext.Current.CancellationToken).Statements);
        Assert.Equal(family, statement.Kind);
        Assert.Equal(SqlScriptParseStatus.Unsupported, statement.Status);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void DdlFacade_DoesNotAcceptAQueryOrRawFallback(SqlAgentToolType provider)
    {
        Assert.ThrowsAny<Exception>(() => SqlCoreFacade.CompileDdl(
            "SELECT 1", new DdlCompilationOptions(provider, provider)));
    }


    [Fact]
    public void FirebirdRecreateProcedure_DoesNotSplitInternalBodySemicolons()
    {
        const string sql = "RECREATE PROCEDURE p AS BEGIN SELECT 1 FROM rdb$database; END; SELECT 2";
        var statement = Assert.Single(SqlCoreFacade.ParseScript(sql, SqlAgentToolType.Firebird,
            TestContext.Current.CancellationToken).Statements);
        Assert.Equal(SqlScriptStatementKind.Procedural, statement.Kind);
        Assert.Equal(SqlScriptParseStatus.Unsupported, statement.Status);
        Assert.Contains("SELECT 2", statement.Text);
    }

    [Fact]
    public void FirebirdCreateOrAlterProcedure_IsNeverClassifiedAsSchema()
    {
        const string sql = "CREATE OR ALTER PROCEDURE p AS BEGIN SELECT 1 FROM rdb$database; END;";
        var statement = Assert.Single(SqlCoreFacade.ParseScript(sql, SqlAgentToolType.Firebird,
            TestContext.Current.CancellationToken).Statements);
        Assert.Equal(SqlScriptStatementKind.Procedural, statement.Kind);
        Assert.Equal(SqlScriptParseStatus.Unsupported, statement.Status);
    }


    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void QueryFacts_RejectsMutationBeforeDmlPolicyWithoutReclassifyingAsParseError(
        SqlAgentToolType provider)
    {
        var error = Assert.Throws<ArgumentException>(() =>
            SqlCoreFacade.CompileQueryWithFacts(
                "UPDATE users SET name = 'Ada'", provider, provider,
                new SqlPlanValidationContext("u1-query-facts-kind"),
                new SqlExecutionPlanPolicy(10)));
        Assert.Contains("CompileQuery", error.Message, StringComparison.Ordinal);
    }

}
