using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreSchemaSnapshotBindingTests
{
    private static readonly SqlAgentToolType[] Providers = Enum.GetValues<SqlAgentToolType>();

    private static SqlPlanValidationContext Context(SqlAgentToolType dialect)
    {
        var upper = dialect is SqlAgentToolType.Oracle or SqlAgentToolType.Firebird;
        var snapshot = new SqlSchemaSnapshot(
            dialect,
            "schema-v1",
            new[]
            {
                new SqlSchemaTable(upper ? "USERS" : "users",
                    upper ? new[] { "ID", "NAME", "ACTIVE" } : new[] { "id", "name", "active" }),
                new SqlSchemaTable(upper ? "ORDERS" : "orders",
                    upper ? new[] { "ID", "USER_ID" } : new[] { "id", "user_id" })
            });
        return new SqlPlanValidationContext("schema-policy-v1").WithSchemaSnapshot(snapshot);
    }

    public static IEnumerable<object[]> SixDialects()
        => Providers.Select(provider => new object[] { provider });

    public static IEnumerable<object[]> RejectedReferences()
    {
        foreach (var dialect in Providers)
        {
            yield return new object[]
            {
                dialect, "SELECT missing FROM users", "SQL_SCHEMA_COLUMN_NOT_FOUND"
            };
            yield return new object[]
            {
                dialect, "SELECT id FROM unavailable", "SQL_SCHEMA_TABLE_NOT_FOUND"
            };
            yield return new object[]
            {
                dialect, "SELECT id FROM users u JOIN orders o ON u.id = o.user_id",
                "SQL_SCHEMA_COLUMN_AMBIGUOUS"
            };
            yield return new object[]
            {
                dialect, "SELECT u.missing FROM users u", "SQL_SCHEMA_COLUMN_NOT_FOUND"
            };
        }
    }

    [Fact]
    public void NestedSetQueries_UseIterativeSchemaScopeAndPreserveFirstError()
    {
        const int depth = 48;
        var valid = "SELECT id FROM users";
        for (var level = 0; level < depth; level++)
            valid = $"SELECT id FROM users UNION ({valid})";
        var result = SqlCoreFacade.CompileQuery(
            valid, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            Context(SqlAgentToolType.Postgres), new SqlExecutionPlanPolicy());
        Assert.NotEmpty(result.Sql);

        var invalid = "SELECT missing FROM users";
        for (var level = 0; level < depth; level++)
            invalid = $"SELECT id FROM users UNION ({invalid})";
        var error = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(
                invalid, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
                Context(SqlAgentToolType.Postgres), new SqlExecutionPlanPolicy()));
        Assert.Equal("SQL_SCHEMA_COLUMN_NOT_FOUND", error.Diagnostic?.Code);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    public void DeepSingleCteChain_UsesExplicitSchemaProjectionContinuations(
        SqlAgentToolType dialect)
    {
        var sql = "SELECT id FROM users";
        for (var depth = 0; depth < 256; depth++)
            sql = $"WITH c AS ({sql}) SELECT id FROM c";
        var result = SqlCoreFacade.CompileQuery(
            sql, dialect, dialect, Context(dialect), new SqlExecutionPlanPolicy());
        Assert.NotEmpty(result.Sql);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void NestedCte_UnsupportedNativeTargetRemainsFailClosed(
        SqlAgentToolType dialect)
    {
        var sql = "WITH c AS (WITH inner_c AS (SELECT id FROM users) " +
                  "SELECT id FROM inner_c) SELECT id FROM c";
        var error = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(
                sql, dialect, dialect, Context(dialect),
                new SqlExecutionPlanPolicy()));
        Assert.Contains("select.cte_scope", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(SixDialects))]
    public void NestedDerivedQueries_KeepSchemaBindingAndProjectionNames(
        SqlAgentToolType dialect)
    {
        // Query -> derived Source -> Query must run the stack-reserve
        // check at each boundary, even with only terminal Column expressions.
        var query = "SELECT id FROM users";
        for (var depth = 0; depth < 256; depth++)
            query = $"SELECT d.id FROM ({query}) d";

        var compiled = SqlCoreFacade.CompileQuery(
            query, dialect, dialect, Context(dialect), new SqlExecutionPlanPolicy());

        Assert.NotEmpty(compiled.Sql);
    }




    [Fact]
    public void MixedParserHeapContinuation_DoesNotSwallowBooleanAfterExists()
    {
        // A standalone WHERE EXISTS may use the heap scheduler; an EXISTS
        // with a trailing AND still belongs to the expression parser.
        var dialect = SqlAgentToolType.Postgres;
        var sql = "SELECT u.id FROM users u WHERE EXISTS (SELECT u.id) AND u.id = 1";
        Assert.NotEmpty(SqlCoreFacade.CompileQuery(
            sql, dialect, dialect, Context(dialect), new SqlExecutionPlanPolicy()).Sql);

        var invalid = "SELECT u.id FROM users u WHERE EXISTS (SELECT u.id) AND u.missing = 1";
        var error = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(
                invalid, dialect, dialect, Context(dialect), new SqlExecutionPlanPolicy()));
        Assert.Equal("SQL_SCHEMA_COLUMN_NOT_FOUND", error.Diagnostic?.Code);
    }



    [Fact]
    public void WhereInFollowedByAnd_UsesOriginalBooleanParser()
    {
        var dialect = SqlAgentToolType.Postgres;
        var valid = "SELECT id FROM users WHERE id IN (SELECT user_id FROM orders) AND id = 7";
        Assert.NotEmpty(SqlCoreFacade.CompileQuery(
            valid, dialect, dialect, Context(dialect), new SqlExecutionPlanPolicy()).Sql);

        var invalid = "SELECT id FROM users WHERE id IN (SELECT user_id FROM orders) AND missing = 7";
        var error = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(
                invalid, dialect, dialect, Context(dialect), new SqlExecutionPlanPolicy()));
        Assert.Equal("SQL_SCHEMA_COLUMN_NOT_FOUND", error.Diagnostic?.Code);
    }



    [Fact]
    public void DeepSearchedCaseScalarSubquery_ReportsMissingInnerColumn()
    {
        const int depth = 96;
        var valid = "SELECT id FROM users";
        for (var i = 0; i < depth; i++)
            valid = "SELECT CASE WHEN 1 = 1 THEN (" + valid + ") ELSE 0 END";
        Assert.NotEmpty(SqlCoreFacade.CompileQuery(
            valid, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            Context(SqlAgentToolType.Postgres), new SqlExecutionPlanPolicy()).Sql);

        var invalid = "SELECT missing FROM users";
        for (var i = 0; i < depth; i++)
            invalid = "SELECT CASE WHEN 1 = 1 THEN (" + invalid + ") ELSE 0 END";
        var error = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(invalid, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
                Context(SqlAgentToolType.Postgres), new SqlExecutionPlanPolicy()));
        Assert.Equal("SQL_SCHEMA_COLUMN_NOT_FOUND", error.Diagnostic?.Code);
    }

    [Fact]
    public void DeepProjectionScalarSubquery_ResumesStrictSchemaProof()
    {
        const int depth = 96;
        var valid = "SELECT id FROM users";
        for (var i = 0; i < depth; i++)
            valid = "SELECT (" + valid + ")";
        Assert.NotEmpty(SqlCoreFacade.CompileQuery(
            valid, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            Context(SqlAgentToolType.Postgres), new SqlExecutionPlanPolicy()).Sql);

        var invalid = "SELECT missing FROM users";
        for (var i = 0; i < depth; i++)
            invalid = "SELECT (" + invalid + ")";
        var error = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(invalid, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
                Context(SqlAgentToolType.Postgres), new SqlExecutionPlanPolicy()));
        Assert.Equal("SQL_SCHEMA_COLUMN_NOT_FOUND", error.Diagnostic?.Code);
    }

    [Fact]
    public void DeepCorrelatedInSubquery_PreservesStrictSchemaFirstError()
    {
        const int depth = 96;
        var valid = "SELECT id FROM users";
        for (var level = 0; level < depth; level++)
            valid = $"SELECT id FROM users WHERE id IN ({valid})";
        var compiled = SqlCoreFacade.CompileQuery(
            valid, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            Context(SqlAgentToolType.Postgres), new SqlExecutionPlanPolicy());
        Assert.NotEmpty(compiled.Sql);

        var invalid = "SELECT missing FROM users";
        for (var level = 0; level < depth; level++)
            invalid = $"SELECT id FROM users WHERE id IN ({invalid})";
        var error = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(
                invalid, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
                Context(SqlAgentToolType.Postgres), new SqlExecutionPlanPolicy()));
        Assert.Equal("SQL_SCHEMA_COLUMN_NOT_FOUND", error.Diagnostic?.Code);
    }
    [Fact]
    public void CorrelatedWhereExistsChain_PreservesStrictSchemaOrder()
    {
        const int depth = 96;
        var sql = "SELECT 1 WHERE u.id = 1";
        for (var level = 0; level < depth; level++)
            sql = $"SELECT u.id FROM users u WHERE EXISTS ({sql})";
        var compiled = SqlCoreFacade.CompileQuery(
            sql, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            Context(SqlAgentToolType.Postgres), new SqlExecutionPlanPolicy());
        Assert.Equal(depth, compiled.Sql.Split("EXISTS (", StringSplitOptions.None).Length - 1);

        var invalid = "SELECT 1 WHERE u.not_in_schema = 1";
        for (var level = 0; level < depth; level++)
            invalid = $"SELECT u.id FROM users u WHERE EXISTS ({invalid})";
        var error = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(
                invalid, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
                Context(SqlAgentToolType.Postgres), new SqlExecutionPlanPolicy()));
        Assert.Equal("SQL_SCHEMA_COLUMN_NOT_FOUND", error.Diagnostic?.Code);
    }



    [Fact]
    public void TwoJoinedLaterals_SecondSeesFirstWithoutExposingFutureAlias()
    {
        const int depth = 96;
        var context = Context(SqlAgentToolType.Postgres);
        static string Wrap(string sql) =>
            "SELECT b.id FROM users u JOIN LATERAL "
            + "(SELECT u.id AS id) a ON a.id = u.id "
            + "JOIN LATERAL (" + sql + ") b ON b.id = a.id";
        var valid = "SELECT a.id AS id";
        for (var i = 0; i < depth; i++)
            valid = Wrap(valid);
        var compiled = SqlCoreFacade.CompileQuery(
            valid, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            context, new SqlExecutionPlanPolicy());
        Assert.Equal(depth * 2, compiled.Sql.Split("LATERAL (", StringSplitOptions.None).Length - 1);

        var invalid = "SELECT a.missing AS id";
        for (var i = 0; i < depth; i++)
            invalid = Wrap(invalid);
        var error = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(invalid, SqlAgentToolType.Postgres,
                SqlAgentToolType.Postgres, context, new SqlExecutionPlanPolicy()));
        Assert.Equal("SQL_SCHEMA_COLUMN_NOT_FOUND", error.Diagnostic?.Code);

        // LATERAL A must not see B's alias: the continuation must not
        // expose all JOIN sources at once merely because the outer
        // SELECT eventually has both aliases in scope.
        var futureAlias = "SELECT b.id FROM users u "
            + "JOIN LATERAL (SELECT b.id AS id) a ON a.id = u.id "
            + "JOIN LATERAL (SELECT a.id AS id) b ON b.id = a.id";
        var hidden = Record.Exception(() =>
            SqlCoreFacade.CompileQuery(futureAlias, SqlAgentToolType.Postgres,
                SqlAgentToolType.Postgres, context, new SqlExecutionPlanPolicy()));
        Assert.NotNull(hidden);
        Assert.DoesNotContain("SQL_COMPILER_STACK_RESERVE", hidden.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PrefixedSecondJoinLateralChain_ResolvesPriorSourceAndPreservesFirstError()
    {
        const int depth = 96;
        var context = Context(SqlAgentToolType.Postgres);
        static string Wrap(string sql) =>
            "SELECT l.id FROM users u JOIN users v ON v.id = u.id "
            + "JOIN LATERAL (" + sql + ") l ON l.id = v.id";
        var valid = "SELECT v.id AS id";
        for (var level = 0; level < depth; level++)
            valid = Wrap(valid);
        var compiled = SqlCoreFacade.CompileQuery(valid, SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres, context, new SqlExecutionPlanPolicy());
        Assert.Equal(depth, compiled.Sql.Split("LATERAL (", StringSplitOptions.None).Length - 1);

        var invalid = "SELECT v.missing AS id";
        for (var level = 0; level < depth; level++)
            invalid = Wrap(invalid);
        var error = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(invalid, SqlAgentToolType.Postgres,
                SqlAgentToolType.Postgres, context, new SqlExecutionPlanPolicy()));
        Assert.Equal("SQL_SCHEMA_COLUMN_NOT_FOUND", error.Diagnostic?.Code);
    }

    [Fact]
    public void JoinedCorrelatedLateralChain_UsesSchemaContinuationsAndPreservesFirstError()
    {
        const int depth = 96;
        var context = Context(SqlAgentToolType.Postgres);
        static string Wrap(string sql) =>
            $"SELECT l.id FROM users u JOIN LATERAL ({sql}) l ON l.id = u.id";
        var valid = "SELECT u.id AS id";
        for (var level = 0; level < depth; level++)
            valid = Wrap(valid);
        var compiled = SqlCoreFacade.CompileQuery(
            valid, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            context, new SqlExecutionPlanPolicy());
        Assert.Equal(depth, compiled.Sql.Split("LATERAL (", StringSplitOptions.None).Length - 1);

        var invalid = "SELECT u.missing AS id";
        for (var level = 0; level < depth; level++)
            invalid = Wrap(invalid);
        var error = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(
                invalid, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
                context, new SqlExecutionPlanPolicy()));
        Assert.Equal("SQL_SCHEMA_COLUMN_NOT_FOUND", error.Diagnostic?.Code);
    }

    [Fact]
    public void DeepPrimaryLateralSource_KeepsStrictSchemaAndTypeProofIterative()
    {
        var sql = "SELECT id FROM users";
        for (var depth = 0; depth < 128; depth++)
            sql = $"SELECT l.id FROM LATERAL ({sql}) AS l";

        var context = new SqlPlanValidationContext("lateral-schema-heap-v1")
            .WithSchemaSnapshot(new SqlSchemaSnapshot(
                SqlAgentToolType.Postgres, "typed-lateral-heap-v1",
                new[] { SqlSchemaTable.WithTypes("users",
                    new[] { new SqlSchemaColumn("id", SqlSchemaValueKind.Numeric) }) }));
        var compiled = SqlCoreFacade.CompileQuery(
            sql, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            context, new SqlExecutionPlanPolicy());

        Assert.Equal(128, compiled.Sql.Split("LATERAL (", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void PrimaryLateralSource_KeepsOuterCorrelationInStrictSchemaAndType()
    {
        var sql = "SELECT u.id FROM users AS u WHERE EXISTS ("
            + "SELECT l.id FROM LATERAL (SELECT u.id AS id) AS l "
            + "WHERE l.id = u.id)";
        var context = new SqlPlanValidationContext("lateral-correlated-schema-heap-v1")
            .WithSchemaSnapshot(new SqlSchemaSnapshot(
                SqlAgentToolType.Postgres, "typed-lateral-correlation-v1",
                new[] { SqlSchemaTable.WithTypes("users",
                    new[] { new SqlSchemaColumn("id", SqlSchemaValueKind.Numeric) }) }));
        var compiled = SqlCoreFacade.CompileQuery(
            sql, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            context, new SqlExecutionPlanPolicy());
        Assert.Contains("LATERAL", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("EXISTS", compiled.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(SixDialects))]
    public void DeepConjunction_UsesHeapSchemaExpressionTraversal(SqlAgentToolType dialect)
    {
        var conditions = string.Concat(Enumerable.Repeat(" AND id = 1", 256));
        var query = "SELECT id FROM users WHERE id = 1" + conditions;
        var result = SqlCoreFacade.CompileQuery(
            query, dialect, dialect, Context(dialect), new SqlExecutionPlanPolicy());
        Assert.NotEmpty(result.Sql);

        // The first unproven column must still win the left-to-right proof.
        var invalid = "SELECT id FROM users WHERE missing = 1" + conditions;
        var error = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(
                invalid, dialect, dialect, Context(dialect), new SqlExecutionPlanPolicy()));
        Assert.Equal("SQL_SCHEMA_COLUMN_NOT_FOUND", error.Diagnostic?.Code);
    }

    [Theory]
    [MemberData(nameof(SixDialects))]
    public void AllDialects_ValidateExistingQualifiedAndUnqualifiedColumns(SqlAgentToolType dialect)
    {
        var context = Context(dialect);
        var compiled = SqlCoreFacade.CompileQuery(
            "SELECT u.id, u.name FROM users u WHERE u.id = 7",
            dialect, dialect, context, new SqlExecutionPlanPolicy(10));
        Assert.NotEmpty(compiled.Sql);
        Assert.NotEmpty(compiled.Parameters);

        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT u.id FROM users u WHERE u.id = 7", dialect);
        var fromParsed = CoreSqlFacadeCompileParsed(parsed, dialect, context);
        Assert.NotEmpty(fromParsed.Sql);
    }

    private static CompiledSqlCommand CoreSqlFacadeCompileParsed(
        ParsedStatement parsed, SqlAgentToolType dialect, SqlPlanValidationContext context) =>
        SqlCoreFacade.CompileQuery(parsed, dialect, context, new SqlExecutionPlanPolicy(10));

    [Theory]
    [MemberData(nameof(RejectedReferences))]
    public void AllDialects_RejectUnprovenSchemaReferencesWithTypedBindingDiagnostics(
        SqlAgentToolType dialect, string sql, string expectedCode)
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(
                sql, dialect, dialect, Context(dialect), new SqlExecutionPlanPolicy(10)));

        var diagnostic = Assert.IsType<SqlDiagnostic>(error.Diagnostic);
        Assert.Equal(expectedCode, diagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.Binding, diagnostic.Stage);
        Assert.Equal(SqlDiagnosticCategory.Binding, diagnostic.Category);
    }

    [Theory]
    [MemberData(nameof(SixDialects))]
    public void AllDialects_DmlTargetColumnsAndPredicatesRequireSnapshotProof(
        SqlAgentToolType dialect)
    {
        Assert.NotEmpty(SqlCoreFacade.CompileDml(
            "UPDATE users SET name = 'Updated' WHERE id = 1",
            dialect, dialect, Context(dialect)).Sql);

        var ex = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileDml(
                "DELETE FROM users WHERE missing = 1",
                dialect, dialect, Context(dialect)));
        Assert.Equal("SQL_SCHEMA_COLUMN_NOT_FOUND", ex.Diagnostic?.Code);

        var insert = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileDml(
                "INSERT INTO users (missing) VALUES (3)",
                dialect, dialect, Context(dialect)));
        Assert.Equal("SQL_SCHEMA_COLUMN_NOT_FOUND", insert.Diagnostic?.Code);
    }

    public static IEnumerable<object[]> NativeUsingProviders()
        => Providers
            .Where(provider => provider != SqlAgentToolType.MsSqlServer)
            .Select(provider => new object[] { provider });

    [Theory]
    [MemberData(nameof(NativeUsingProviders))]
    public void JoinUsing_CoalescesOnlyUnqualifiedColumnNames(SqlAgentToolType dialect)
    {
        var context = Context(dialect);
        var valid = new[]
        {
            "SELECT id FROM users u JOIN orders o USING (id) WHERE id = 7",
            "SELECT u.id, o.id FROM users u JOIN orders o USING (id)",
            "SELECT d.id, d.user_id FROM (SELECT * FROM users u JOIN orders o USING (id)) d",
            "SELECT id FROM users u JOIN orders o USING (id) JOIN orders o2 USING (id)"
        };
        foreach (var sql in valid)
        {
            var command = SqlCoreFacade.CompileQuery(
                sql, dialect, dialect, context, new SqlExecutionPlanPolicy());
            Assert.NotEmpty(command.Sql);
        }

        var ambiguous = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(
                "SELECT id FROM users u JOIN orders o USING (id) " +
                "JOIN users another ON another.id = u.id",
                dialect, dialect, context, new SqlExecutionPlanPolicy()));
        Assert.Equal("SQL_SCHEMA_COLUMN_AMBIGUOUS", ambiguous.Diagnostic?.Code);

        var missing = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(
                "SELECT id FROM users u JOIN orders o USING (name)",
                dialect, dialect, context, new SqlExecutionPlanPolicy()));
        Assert.Equal("SQL_SCHEMA_COLUMN_NOT_FOUND", missing.Diagnostic?.Code);
    }

    public static IEnumerable<object[]> NativeNaturalProviders()
        => Providers
            .Where(provider => provider is SqlAgentToolType.Postgres or SqlAgentToolType.MySQL
                or SqlAgentToolType.Sqlite or SqlAgentToolType.Oracle)
            .Select(provider => new object[] { provider });

    [Theory]
    [MemberData(nameof(NativeNaturalProviders))]
    public void NaturalJoin_ProvesImplicitJoinKeysAndWildcardProjection(SqlAgentToolType dialect)
    {
        var context = Context(dialect);
        var valid = new[]
        {
            "SELECT id FROM users u NATURAL JOIN orders o WHERE id = 7",
            "SELECT u.id, o.id FROM users u NATURAL LEFT JOIN orders o",
            "SELECT d.id, d.user_id FROM (SELECT * FROM users u NATURAL JOIN orders o) d",
            "WITH x AS (SELECT * FROM users u NATURAL JOIN orders o) SELECT id FROM x",
            "SELECT id FROM users u NATURAL JOIN orders o NATURAL JOIN orders o2",
            // No common columns: NATURAL JOIN behaves as an unconstrained join.
            "SELECT d.id, d.renamed FROM " +
            "(SELECT * FROM users u NATURAL JOIN (SELECT name AS renamed FROM users) b) d"
        };
        foreach (var sql in valid)
            Assert.NotEmpty(SqlCoreFacade.CompileQuery(
                sql, dialect, dialect, context, new SqlExecutionPlanPolicy()).Sql);

        var ambiguous = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(
                "SELECT id FROM users u NATURAL JOIN orders o " +
                "JOIN users extra ON extra.id = u.id",
                dialect, dialect, context, new SqlExecutionPlanPolicy()));
        Assert.Equal("SQL_SCHEMA_COLUMN_AMBIGUOUS", ambiguous.Diagnostic?.Code);
    }

    [Fact]
    public void Postgres_CteAndCorrelatedQueries_UseActualScopedColumns()
    {
        var context = Context(SqlAgentToolType.Postgres);
        Assert.NotEmpty(SqlCoreFacade.CompileQuery(
            "WITH x AS (SELECT id, name FROM users) SELECT x.name FROM x",
            SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            context, new SqlExecutionPlanPolicy()).Sql);

        var ex = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(
                "SELECT u.id FROM users u WHERE EXISTS " +
                "(SELECT 1 FROM orders o WHERE o.user_id = u.id AND o.missing = 1)",
                SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
                context, new SqlExecutionPlanPolicy()));
        Assert.Equal("SQL_SCHEMA_COLUMN_NOT_FOUND", ex.Diagnostic?.Code);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(32)]
    public void Postgres_DeepCorrelatedSubqueries_ResolveAncestorSchemaWithoutRecursiveScopeLookup(
        int depth)
    {
        var nested = string.Concat(Enumerable.Repeat(
            "EXISTS (SELECT 1 FROM orders o WHERE o.user_id = u.id AND ", depth))
            + "u.id = 7" + new string(')', depth);
        var sql = "SELECT u.id FROM users u WHERE " + nested;
        var context = Context(SqlAgentToolType.Postgres);

        var compiled = SqlCoreFacade.CompileQuery(
            sql, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            context, new SqlExecutionPlanPolicy());
        Assert.Contains("EXISTS", compiled.Sql, StringComparison.OrdinalIgnoreCase);

        // An outer alias still resolves, but its missing column must fail
        // at the original Schema Binding stage, not silently bind locally.
        var invalidSql = "SELECT u.id FROM users u WHERE "
            + string.Concat(Enumerable.Repeat(
                "EXISTS (SELECT 1 FROM orders o WHERE o.user_id = u.id AND ", depth))
            + "u.missing = 7" + new string(')', depth);
        var error = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(
                invalidSql, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
                context, new SqlExecutionPlanPolicy()));
        Assert.Equal("SQL_SCHEMA_COLUMN_NOT_FOUND", error.Diagnostic?.Code);
        Assert.Equal(SqlDiagnosticStage.Binding, error.Diagnostic?.Stage);
    }

    [Fact]
    public void Postgres_SnapshotValidation_RefusesRecursiveCteAndUnprovenProjection()
    {
        var context = Context(SqlAgentToolType.Postgres);
        var recursive = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(
                "WITH RECURSIVE x(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM x WHERE n < 3) " +
                "SELECT n FROM x",
                SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
                context, new SqlExecutionPlanPolicy()));
        Assert.Equal("SQL_SCHEMA_RECURSIVE_CTE_UNPROVABLE", recursive.Diagnostic?.Code);

        var computed = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(
                "SELECT x.anonymous FROM (SELECT id + 1 FROM users) x",
                SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
                context, new SqlExecutionPlanPolicy()));
        Assert.Equal("SQL_SCHEMA_SHAPE_UNPROVABLE", computed.Diagnostic?.Code);
    }

    [Fact]
    public void MissingSnapshot_RetainsExistingCompilerCompatibility()
    {
        var command = SqlCoreFacade.CompileQuery(
            "SELECT unknown_field FROM users",
            SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("legacy-scope-v1"),
            new SqlExecutionPlanPolicy());
        Assert.NotEmpty(command.Sql);
    }

    [Fact]
    public void SnapshotContentAndVersion_AreBoundToPlanAndEvidenceFingerprints()
    {
        static SqlPlanValidationContext WithSchema(string version, params string[] columnNames) =>
            new SqlPlanValidationContext("same-policy-v1").WithSchemaSnapshot(
                new SqlSchemaSnapshot(SqlAgentToolType.Postgres, version, new[]
                {
                    new SqlSchemaTable("users", columnNames)
                }));

        static CompiledSqlCommand Compile(SqlPlanValidationContext context) =>
            SqlCoreFacade.CompileQuery(
                "SELECT id FROM users", SqlAgentToolType.Postgres,
                SqlAgentToolType.Postgres, context, new SqlExecutionPlanPolicy(10));

        var baseline = Compile(WithSchema("v1", "id", "name"));
        var reordered = Compile(WithSchema("v1", "name", "id"));
        var revisedColumns = Compile(WithSchema("v1", "id", "status"));
        var revisedVersion = Compile(WithSchema("v2", "id", "name"));

        Assert.Equal(baseline.PlanFingerprint, reordered.PlanFingerprint);
        Assert.NotEqual(baseline.PlanFingerprint, revisedColumns.PlanFingerprint);
        Assert.NotEqual(baseline.PlanFingerprint, revisedVersion.PlanFingerprint);
        Assert.NotNull(baseline.CompileEvidence);
        Assert.Equal(baseline.CompileEvidence!.EvidenceFingerprint,
            reordered.CompileEvidence!.EvidenceFingerprint);
        Assert.NotEqual(baseline.CompileEvidence.EvidenceFingerprint,
            revisedColumns.CompileEvidence!.EvidenceFingerprint);
        Assert.NotEqual(baseline.CompileEvidence.EvidenceFingerprint,
            revisedVersion.CompileEvidence!.EvidenceFingerprint);

        var baselineDml = SqlCoreFacade.CompileDml(
            "DELETE FROM users WHERE id = 7",
            SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            WithSchema("v1", "id", "name"));
        var revisedDml = SqlCoreFacade.CompileDml(
            "DELETE FROM users WHERE id = 7",
            SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            WithSchema("v2", "id", "name"));
        Assert.NotEqual(baselineDml.PlanFingerprint, revisedDml.PlanFingerprint);
    }

    [Fact]
    public void SnapshotCopiesInputColumnsAndRejectsWrongDialect()
    {
        var cols = new List<string> { "id" };
        var table = new SqlSchemaTable("users", cols);
        cols[0] = "changed";
        Assert.Equal("id", table.Columns[0]);
        var schema = new SqlSchemaSnapshot(SqlAgentToolType.Postgres, "stable-v1", new[] { table });
        Assert.Equal("stable-v1", schema.Version);

        Assert.Throws<ArgumentException>(() => SqlCoreFacade.CompileQuery(
            "SELECT id FROM users",
            SqlAgentToolType.MySQL, SqlAgentToolType.MySQL,
            new SqlPlanValidationContext("mismatched").WithSchemaSnapshot(schema),
            new SqlExecutionPlanPolicy()));
    }
}
