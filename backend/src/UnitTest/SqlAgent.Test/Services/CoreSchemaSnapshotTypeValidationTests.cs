using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreSchemaSnapshotTypeValidationTests
{
    public static IEnumerable<object[]> Providers()
        => Enum.GetValues<SqlAgentToolType>().Select(dialect => new object[] { dialect });

    private static SqlPlanValidationContext Context(SqlAgentToolType dialect, bool typed)
    {
        var upper = dialect is SqlAgentToolType.Oracle or SqlAgentToolType.Firebird;
        string Name(string name) => upper ? name.ToUpperInvariant() : name;
        var columns = new[]
        {
            new SqlSchemaColumn(Name("id"), SqlSchemaValueKind.Numeric),
            new SqlSchemaColumn(Name("name"), SqlSchemaValueKind.Text),
            new SqlSchemaColumn(Name("enabled"), SqlSchemaValueKind.Boolean)
        };
        var table = typed
            ? SqlSchemaTable.WithTypes(Name("users"), columns)
            : new SqlSchemaTable(Name("users"), columns.Select(x => x.Name).ToArray());
        return new SqlPlanValidationContext("typed-schema-v1").WithSchemaSnapshot(
            new SqlSchemaSnapshot(dialect, "catalog-v1", new[] { table }));
    }

    public static IEnumerable<object[]> Rejections()
    {
        foreach (var dialect in Enum.GetValues<SqlAgentToolType>())
        {
            yield return new object[] { dialect, "SELECT id FROM users WHERE id = 'not-a-number'", false };
            yield return new object[] { dialect, "SELECT id FROM users WHERE id = name", false };
            yield return new object[] { dialect, "SELECT id FROM users WHERE name + 2 > 0", false };
            yield return new object[] { dialect, "UPDATE users SET name = 3 WHERE id = 7", true };
            yield return new object[] { dialect, "INSERT INTO users (name) VALUES (3)", true };
            yield return new object[] { dialect, "DELETE FROM users WHERE id = 'bad'", true };
        }
    }

    [Theory]
    [MemberData(nameof(Rejections))]
    public void TypedSnapshot_RejectsProvenIncompatibleTypeFamilies(
        SqlAgentToolType dialect, string sql, bool dml)
    {
        var context = Context(dialect, typed: true);
        var error = Assert.Throws<SqlCompilationException>(() =>
        {
            if (dml)
                SqlCoreFacade.CompileDml(sql, dialect, dialect, context);
            else
                SqlCoreFacade.CompileQuery(
                    sql, dialect, dialect, context, new SqlExecutionPlanPolicy());
        });

        Assert.NotNull(error.Diagnostic);
        Assert.Equal("SQL_SCHEMA_TYPE_MISMATCH", error.Diagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.SemanticValidation, error.Diagnostic.Stage);
        Assert.Equal(SqlDiagnosticCategory.Semantic, error.Diagnostic.Category);
    }

    [Fact]
    public void NestedSetQueries_PreserveTypeFamilyDiagnostics()
    {
        const int depth = 48;
        var valid = "SELECT id FROM users";
        for (var level = 0; level < depth; level++)
            valid = $"SELECT id FROM users UNION ({valid})";
        var compiled = SqlCoreFacade.CompileQuery(
            valid, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            Context(SqlAgentToolType.Postgres, typed: true),
            new SqlExecutionPlanPolicy());
        Assert.NotEmpty(compiled.Sql);

        var invalid = "SELECT name + 2 AS id FROM users";
        for (var level = 0; level < depth; level++)
            invalid = $"SELECT id FROM users UNION ({invalid})";
        var error = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(
                invalid, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
                Context(SqlAgentToolType.Postgres, typed: true),
                new SqlExecutionPlanPolicy()));
        Assert.Equal("SQL_SCHEMA_TYPE_MISMATCH", error.Diagnostic?.Code);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    public void DeepSingleCteChain_UsesExplicitTypeProofContinuations(
        SqlAgentToolType dialect)
    {
        var sql = "SELECT id FROM users";
        for (var depth = 0; depth < 256; depth++)
            sql = $"WITH c AS ({sql}) SELECT id FROM c";
        var result = SqlCoreFacade.CompileQuery(
            sql, dialect, dialect, Context(dialect, typed: true),
            new SqlExecutionPlanPolicy());
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
                sql, dialect, dialect, Context(dialect, typed: true),
                new SqlExecutionPlanPolicy()));
        Assert.Contains("select.cte_scope", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void NestedDerivedQueries_KeepTypedNumericProjectionProof(
        SqlAgentToolType dialect)
    {
        // Typed query projection checks must remain safe across
        // Query -> derived Source -> Query transitions.
        var query = "SELECT id FROM users";
        for (var depth = 0; depth < 256; depth++)
            query = $"SELECT d.id FROM ({query}) d";

        var compiled = SqlCoreFacade.CompileQuery(
            query, dialect, dialect, Context(dialect, typed: true),
            new SqlExecutionPlanPolicy());

        Assert.NotEmpty(compiled.Sql);
    }






    [Fact]
    public void DeepSearchedCaseScalarSubquery_RejectsInvalidInnerArithmetic()
    {
        const int depth = 96;
        var context = Context(SqlAgentToolType.Postgres, typed: true);
        var valid = "SELECT id + 1 FROM users";
        for (var i = 0; i < depth; i++)
            valid = "SELECT CASE WHEN 1 = 1 THEN (" + valid + ") ELSE 0 END";
        Assert.NotEmpty(SqlCoreFacade.CompileQuery(
            valid, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            context, new SqlExecutionPlanPolicy()).Sql);

        var invalid = "SELECT name + 1 FROM users";
        for (var i = 0; i < depth; i++)
            invalid = "SELECT CASE WHEN 1 = 1 THEN (" + invalid + ") ELSE 0 END";
        var error = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(invalid, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
                context, new SqlExecutionPlanPolicy()));
        Assert.Equal("SQL_SCHEMA_TYPE_MISMATCH", error.Diagnostic?.Code);
    }

    [Fact]
    public void DeepProjectionScalarSubquery_PreservesNumericTypeRejection()
    {
        const int depth = 96;
        var context = Context(SqlAgentToolType.Postgres, typed: true);
        var valid = "SELECT id + 1 FROM users";
        for (var i = 0; i < depth; i++)
            valid = "SELECT (" + valid + ")";
        Assert.NotEmpty(SqlCoreFacade.CompileQuery(
            valid, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            context, new SqlExecutionPlanPolicy()).Sql);

        var invalid = "SELECT name + 1 FROM users";
        for (var i = 0; i < depth; i++)
            invalid = "SELECT (" + invalid + ")";
        var error = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(invalid, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
                context, new SqlExecutionPlanPolicy()));
        Assert.Equal("SQL_SCHEMA_TYPE_MISMATCH", error.Diagnostic?.Code);
    }

    [Fact]
    public void DeepCorrelatedInSubquery_PreservesStrictNumericComparison()
    {
        const int depth = 96;
        var context = Context(SqlAgentToolType.Postgres, typed: true);
        var valid = "SELECT id FROM users";
        for (var level = 0; level < depth; level++)
            valid = $"SELECT id FROM users WHERE id IN ({valid})";
        Assert.NotEmpty(SqlCoreFacade.CompileQuery(
            valid, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            context, new SqlExecutionPlanPolicy()).Sql);

        var invalid = "SELECT name FROM users";
        for (var level = 0; level < depth; level++)
            invalid = $"SELECT id FROM users WHERE id IN ({invalid})";
        var error = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(
                invalid, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
                context, new SqlExecutionPlanPolicy()));
        Assert.Equal("SQL_SCHEMA_TYPE_MISMATCH", error.Diagnostic?.Code);
    }
    [Fact]
    public void CorrelatedWhereExistsChain_PreservesNumericTypeFamilyProof()
    {
        const int depth = 96;
        var context = Context(SqlAgentToolType.Postgres, typed: true);
        var sql = "SELECT 1 WHERE u.id = 1";
        for (var level = 0; level < depth; level++)
            sql = $"SELECT u.id FROM users u WHERE EXISTS ({sql})";
        Assert.NotEmpty(SqlCoreFacade.CompileQuery(
            sql, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            context, new SqlExecutionPlanPolicy()).Sql);

        var invalid = "SELECT 1 WHERE u.name + 1 = 0";
        for (var level = 0; level < depth; level++)
            invalid = $"SELECT u.id FROM users u WHERE EXISTS ({invalid})";
        var error = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(
                invalid, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
                context, new SqlExecutionPlanPolicy()));
        Assert.Equal("SQL_SCHEMA_TYPE_MISMATCH", error.Diagnostic?.Code);
    }




    [Fact]
    public void ThreeDependentLaterals_PreserveNumericTypeAndNegativeDiagnostics()
    {
        const int depth = 96;
        var context = Context(SqlAgentToolType.Postgres, typed: true);
        static string Wrap(string sql) =>
            "SELECT c.id FROM users u "
            + "JOIN LATERAL (SELECT u.id AS id) a ON a.id = u.id "
            + "JOIN LATERAL (SELECT a.id AS id) b ON b.id = a.id "
            + "JOIN LATERAL (" + sql + ") c ON c.id = b.id";
        var valid = "SELECT b.id AS id";
        for (var i = 0; i < depth; i++) valid = Wrap(valid);
        Assert.NotEmpty(SqlCoreFacade.CompileQuery(valid, SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres, context, new SqlExecutionPlanPolicy()).Sql);

        var invalid = "SELECT b.id + 'not-numeric' AS id";
        for (var i = 0; i < depth; i++) invalid = Wrap(invalid);
        var error = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(invalid, SqlAgentToolType.Postgres,
                SqlAgentToolType.Postgres, context, new SqlExecutionPlanPolicy()));
        Assert.Equal("SQL_SCHEMA_TYPE_MISMATCH", error.Diagnostic?.Code);
    }

    [Fact]
    public void TwoJoinedLaterals_SecondInheritsFirstNumericColumnType()
    {
        const int depth = 96;
        var context = Context(SqlAgentToolType.Postgres, typed: true);
        static string Wrap(string sql) =>
            "SELECT b.id FROM users u JOIN LATERAL "
            + "(SELECT u.id AS id) a ON a.id = u.id "
            + "JOIN LATERAL (" + sql + ") b ON b.id = a.id";
        var valid = "SELECT a.id AS id";
        for (var i = 0; i < depth; i++)
            valid = Wrap(valid);
        Assert.NotEmpty(SqlCoreFacade.CompileQuery(valid, SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres, context, new SqlExecutionPlanPolicy()).Sql);

        var invalid = "SELECT a.id + 'not-numeric' AS id";
        for (var i = 0; i < depth; i++)
            invalid = Wrap(invalid);
        var error = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(invalid, SqlAgentToolType.Postgres,
                SqlAgentToolType.Postgres, context, new SqlExecutionPlanPolicy()));
        Assert.Equal("SQL_SCHEMA_TYPE_MISMATCH", error.Diagnostic?.Code);
    }

    [Fact]
    public void PrefixedSecondJoinLateralChain_PreservesNumericTypeProof()
    {
        const int depth = 96;
        var context = Context(SqlAgentToolType.Postgres, typed: true);
        static string Wrap(string sql) =>
            "SELECT l.id FROM users u JOIN users v ON v.id = u.id "
            + "JOIN LATERAL (" + sql + ") l ON l.id = v.id";
        var valid = "SELECT v.id AS id";
        for (var level = 0; level < depth; level++)
            valid = Wrap(valid);
        Assert.NotEmpty(SqlCoreFacade.CompileQuery(valid, SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres, context, new SqlExecutionPlanPolicy()).Sql);

        var invalid = "SELECT v.name + 1 AS id";
        for (var level = 0; level < depth; level++)
            invalid = Wrap(invalid);
        var error = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(invalid, SqlAgentToolType.Postgres,
                SqlAgentToolType.Postgres, context, new SqlExecutionPlanPolicy()));
        Assert.Equal("SQL_SCHEMA_TYPE_MISMATCH", error.Diagnostic?.Code);
    }

    [Fact]
    public void JoinedCorrelatedLateralChain_PreservesNestedNumericTypeAndMismatch()
    {
        const int depth = 96;
        var context = Context(SqlAgentToolType.Postgres, typed: true);
        static string Wrap(string sql) =>
            $"SELECT l.id FROM users u JOIN LATERAL ({sql}) l ON l.id = u.id";
        var valid = "SELECT u.id AS id";
        for (var level = 0; level < depth; level++)
            valid = Wrap(valid);
        Assert.NotEmpty(SqlCoreFacade.CompileQuery(
            valid, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            context, new SqlExecutionPlanPolicy()).Sql);

        var invalid = "SELECT u.name + 1 AS id";
        for (var level = 0; level < depth; level++)
            invalid = Wrap(invalid);
        var error = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(
                invalid, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
                context, new SqlExecutionPlanPolicy()));
        Assert.Equal("SQL_SCHEMA_TYPE_MISMATCH", error.Diagnostic?.Code);
        Assert.Equal(SqlDiagnosticStage.SemanticValidation, error.Diagnostic?.Stage);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void TypedSnapshot_DeepBooleanGraph_UsesIterativeTypeProof(
        SqlAgentToolType dialect)
    {
        var suffix = string.Concat(Enumerable.Repeat(" AND id = 1", 384));
        var sql = "SELECT id FROM users WHERE id = 1" + suffix;
        Assert.NotEmpty(SqlCoreFacade.CompileQuery(
            sql, dialect, dialect, Context(dialect, typed: true),
            new SqlExecutionPlanPolicy()).Sql);

        var invalid = "SELECT id FROM users WHERE name + 2 = 1" + suffix;
        var error = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(
                invalid, dialect, dialect, Context(dialect, typed: true),
                new SqlExecutionPlanPolicy()));
        Assert.Equal("SQL_SCHEMA_TYPE_MISMATCH", error.Diagnostic?.Code);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void TypedSnapshot_PreservesValidNumericAndTextPredicatesAndAssignments(
        SqlAgentToolType dialect)
    {
        var context = Context(dialect, typed: true);
        Assert.NotEmpty(SqlCoreFacade.CompileQuery(
            "SELECT id, name FROM users WHERE id = 7 AND name = 'Alice'",
            dialect, dialect, context, new SqlExecutionPlanPolicy()).Sql);

        Assert.NotEmpty(SqlCoreFacade.CompileDml(
            "UPDATE users SET name = 'Alice' WHERE id = 7",
            dialect, dialect, context).Sql);

        Assert.NotEmpty(SqlCoreFacade.CompileDml(
            "INSERT INTO users (id, name) VALUES (7, 'Alice')",
            dialect, dialect, context).Sql);

        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT id FROM users WHERE id = 7", dialect);
        Assert.NotEmpty(SqlCoreFacade.CompileQuery(
            parsed, dialect, context, new SqlExecutionPlanPolicy()).Sql);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void TypedSnapshot_RejectsIncompatibleInsertSelectAssignments(
        SqlAgentToolType dialect)
    {
        var context = Context(dialect, typed: true);
        var sqls = new[]
        {
            "INSERT INTO users (name) SELECT id FROM users",
            "INSERT INTO users (id, name) SELECT name, id FROM users"
        };

        foreach (var sql in sqls)
        {
            var error = Assert.Throws<SqlCompilationException>(() =>
                SqlCoreFacade.CompileDml(sql, dialect, dialect, context));
            Assert.Equal("SQL_SCHEMA_TYPE_MISMATCH", error.Diagnostic?.Code);
            Assert.Equal(SqlDiagnosticStage.SemanticValidation, error.Diagnostic?.Stage);
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void TypedSnapshot_AcceptsCompatibleInsertSelectAssignments(
        SqlAgentToolType dialect)
    {
        var context = Context(dialect, typed: true);
        var sqls = new[]
        {
            "INSERT INTO users (name) SELECT name FROM users",
            "INSERT INTO users (id, name) SELECT id, name FROM users"
        };

        foreach (var sql in sqls)
            Assert.NotEmpty(SqlCoreFacade.CompileDml(sql, dialect, dialect, context).Sql);

        // A name-only snapshot cannot prove a type-family mismatch.
        Assert.NotEmpty(SqlCoreFacade.CompileDml(
            "INSERT INTO users (name) SELECT id FROM users",
            dialect, dialect, Context(dialect, typed: false)).Sql);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void TypedSnapshot_RejectsInsertSelectProjectionWidthMismatch(
        SqlAgentToolType dialect)
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileDml(
                "INSERT INTO users (name) SELECT id, name FROM users",
                dialect, dialect, Context(dialect, typed: true)));
        Assert.Equal("SQL_SCHEMA_SHAPE_UNPROVABLE", error.Diagnostic?.Code);
        Assert.Equal(SqlDiagnosticStage.Binding, error.Diagnostic?.Stage);
    }

    [Fact]
    public void NameOnlySchema_StillAcceptsPreviouslyUnprovenTypeCoercions()
    {
        var sql = "SELECT id FROM users WHERE id = 'bad'";
        var parsed = CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Postgres);
        var command = SqlCoreFacade.CompileQuery(
            parsed, SqlAgentToolType.Postgres,
            Context(SqlAgentToolType.Postgres, typed: false),
            new SqlExecutionPlanPolicy());
        Assert.NotEmpty(command.Sql);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void SchemaBoundDmlFingerprint_IsRecomputableAtPreviewAndCommit(
        SqlAgentToolType dialect)
    {
        var context = Context(dialect, typed: true);
        var command = SqlCoreFacade.CompileDml(
            "DELETE FROM users WHERE id = 7", dialect, dialect, context);

        // Preview/commit intentionally compute the fingerprint from the approved command.
        // The schema proof must not disappear at that boundary.
        Assert.Equal(
            command.PlanFingerprint,
            HsSqlAgent.SqlCore.Core.Execution.DmlFingerprintService.ComputePlanFingerprint(
                command, context.PolicyVersion));
        Assert.NotNull(command.CompileEvidence);
        Assert.Contains(command.CompileEvidence!.Assurances,
            assurance => assurance.Kind == "sqlcore.schema_snapshot");
    }

    [Fact]
    public void TypeProofChangesAlterPlanAndEvidenceIdentity()
    {
        var typed = SqlCoreFacade.CompileQuery(
            "SELECT id FROM users", SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres, Context(SqlAgentToolType.Postgres, typed: true),
            new SqlExecutionPlanPolicy());
        var untyped = SqlCoreFacade.CompileQuery(
            "SELECT id FROM users", SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres, Context(SqlAgentToolType.Postgres, typed: false),
            new SqlExecutionPlanPolicy());
        Assert.NotEqual(typed.PlanFingerprint, untyped.PlanFingerprint);
        Assert.NotNull(typed.CompileEvidence);
        Assert.NotNull(untyped.CompileEvidence);
        Assert.NotEqual(typed.CompileEvidence!.EvidenceFingerprint,
            untyped.CompileEvidence!.EvidenceFingerprint);
    }


    public static IEnumerable<object[]> TypedProjectionRejections()
    {
        foreach (var dialect in Enum.GetValues<SqlAgentToolType>())
        {
            yield return new object[]
            {
                dialect, "WITH x AS (SELECT id FROM users) SELECT id FROM x WHERE id = 'bad'"
            };
            yield return new object[]
            {
                dialect, "WITH x(key_id) AS (SELECT id FROM users) SELECT key_id FROM x WHERE key_id = 'bad'"
            };
            yield return new object[]
            {
                dialect, "WITH x AS (SELECT id AS k FROM users), " +
                         "y AS (SELECT k FROM x) SELECT k FROM y WHERE k = 'bad'"
            };
            yield return new object[]
            {
                dialect, "SELECT d.id FROM (SELECT id FROM users) d WHERE d.id = 'bad'"
            };
            yield return new object[]
            {
                dialect, "SELECT d.computed FROM (SELECT id + 1 AS computed FROM users) d " +
                         "WHERE d.computed = 'bad'"
            };
            yield return new object[]
            {
                dialect, "SELECT id FROM users WHERE id IN (SELECT name FROM users)"
            };
        }
    }

    [Theory]
    [MemberData(nameof(TypedProjectionRejections))]
    public void TypedCteAndDerivedProjections_RejectIncompatibleOuterPredicates(
        SqlAgentToolType dialect, string sql)
    {
        var exception = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(
                sql, dialect, dialect, Context(dialect, typed: true),
                new SqlExecutionPlanPolicy()));
        Assert.Equal("SQL_SCHEMA_TYPE_MISMATCH", exception.Diagnostic?.Code);
        Assert.Equal(SqlDiagnosticStage.SemanticValidation, exception.Diagnostic?.Stage);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void TypedCteAndDerivedProjections_PreserveValidPredicates(
        SqlAgentToolType dialect)
    {
        var sqls = new[]
        {
            "WITH x(k) AS (SELECT id FROM users) SELECT k FROM x WHERE k = 7",
            "SELECT d.id FROM (SELECT id FROM users) d WHERE d.id = 7",
            "SELECT d.computed FROM (SELECT id + 1 AS computed FROM users) d WHERE d.computed = 8",
            "SELECT id FROM users WHERE id IN (SELECT id FROM users)"
        };
        foreach (var sql in sqls)
        {
            var command = SqlCoreFacade.CompileQuery(
                sql, dialect, dialect, Context(dialect, typed: true),
                new SqlExecutionPlanPolicy());
            Assert.NotEmpty(command.Sql);
        }
    }

    public static IEnumerable<object[]> NativeUsingProviders()
        => Enum.GetValues<SqlAgentToolType>()
            .Where(provider => provider != SqlAgentToolType.MsSqlServer)
            .Select(provider => new object[] { provider });

    private static SqlPlanValidationContext JoinedContext(SqlAgentToolType dialect)
    {
        var upper = dialect is SqlAgentToolType.Oracle or SqlAgentToolType.Firebird;
        string Name(string value) => upper ? value.ToUpperInvariant() : value;
        SqlSchemaTable Table(string name, params (string Column, SqlSchemaValueKind Kind)[] columns)
            => SqlSchemaTable.WithTypes(Name(name), columns
                .Select(column => new SqlSchemaColumn(Name(column.Column), column.Kind))
                .ToArray());

        return new SqlPlanValidationContext("typed-using-v1").WithSchemaSnapshot(
            new SqlSchemaSnapshot(dialect, "typed-using-v1", new[]
            {
                Table("users", ("id", SqlSchemaValueKind.Numeric),
                    ("name", SqlSchemaValueKind.Text)),
                Table("orders", ("id", SqlSchemaValueKind.Numeric),
                    ("user_id", SqlSchemaValueKind.Numeric))
            }));
    }

    [Theory]
    [MemberData(nameof(NativeUsingProviders))]
    public void JoinUsing_PropagatesCoalescedWildcardAndQualifiedTypeProofs(SqlAgentToolType dialect)
    {
        var context = JoinedContext(dialect);
        var valid = new[]
        {
            "SELECT id FROM users u JOIN orders o USING (id) WHERE id = 7",
            "SELECT d.id FROM (SELECT * FROM users u JOIN orders o USING (id)) d WHERE d.id = 7",
            "WITH x AS (SELECT * FROM users u JOIN orders o USING (id)) SELECT id FROM x WHERE id = 7"
        };
        foreach (var sql in valid)
            Assert.NotEmpty(SqlCoreFacade.CompileQuery(
                sql, dialect, dialect, context, new SqlExecutionPlanPolicy()).Sql);

        var invalid = new[]
        {
            "SELECT id FROM users u JOIN orders o USING (id) WHERE id = 'bad'",
            "SELECT u.id FROM users u JOIN orders o USING (id) WHERE u.id = 'bad'",
            "SELECT o.id FROM users u JOIN orders o USING (id) WHERE o.id = 'bad'",
            "SELECT d.id FROM (SELECT * FROM users u JOIN orders o USING (id)) d WHERE d.id = 'bad'",
            "WITH x AS (SELECT * FROM users u JOIN orders o USING (id)) SELECT id FROM x WHERE id = 'bad'"
        };
        foreach (var sql in invalid)
        {
            var error = Assert.Throws<SqlCompilationException>(() =>
                SqlCoreFacade.CompileQuery(
                    sql, dialect, dialect, context, new SqlExecutionPlanPolicy()));
            Assert.Equal("SQL_SCHEMA_TYPE_MISMATCH", error.Diagnostic?.Code);
            Assert.Equal(SqlDiagnosticStage.SemanticValidation, error.Diagnostic?.Stage);
        }
    }

    public static IEnumerable<object[]> NativeNaturalProviders()
        => Enum.GetValues<SqlAgentToolType>()
            .Where(provider => provider is SqlAgentToolType.Postgres or SqlAgentToolType.MySQL
                or SqlAgentToolType.Sqlite or SqlAgentToolType.Oracle)
            .Select(provider => new object[] { provider });

    [Theory]
    [MemberData(nameof(NativeNaturalProviders))]
    public void NaturalJoin_MergedKeysCarryProvenTypeFamilies(SqlAgentToolType dialect)
    {
        var context = JoinedContext(dialect);
        var valid = new[]
        {
            "SELECT id FROM users u NATURAL JOIN orders o WHERE id = 7",
            "SELECT d.id FROM (SELECT * FROM users u NATURAL JOIN orders o) d WHERE d.id = 7",
            "WITH x AS (SELECT * FROM users u NATURAL JOIN orders o) SELECT id FROM x WHERE id = 7"
        };
        foreach (var sql in valid)
            Assert.NotEmpty(SqlCoreFacade.CompileQuery(
                sql, dialect, dialect, context, new SqlExecutionPlanPolicy()).Sql);

        var incompatible = new[]
        {
            "SELECT id FROM users u NATURAL JOIN orders o WHERE id = 'bad'",
            "SELECT d.id FROM (SELECT * FROM users u NATURAL JOIN orders o) d WHERE d.id = 'bad'",
            "WITH x AS (SELECT * FROM users u NATURAL JOIN orders o) SELECT id FROM x WHERE id = 'bad'"
        };
        foreach (var sql in incompatible)
        {
            var error = Assert.Throws<SqlCompilationException>(() =>
                SqlCoreFacade.CompileQuery(
                    sql, dialect, dialect, context, new SqlExecutionPlanPolicy()));
            Assert.Equal("SQL_SCHEMA_TYPE_MISMATCH", error.Diagnostic?.Code);
        }
    }

    [Fact]
    public void PostgreSql_CorrelatedSubqueryAndScalarProjection_CarryColumnTypeProofs()
    {
        var snapshot = new SqlSchemaSnapshot(
            SqlAgentToolType.Postgres, "typed-v2",
            new[]
            {
                SqlSchemaTable.WithTypes("users",
                    new[]
                    {
                        new SqlSchemaColumn("id", SqlSchemaValueKind.Numeric),
                        new SqlSchemaColumn("name", SqlSchemaValueKind.Text)
                    }),
                SqlSchemaTable.WithTypes("orders",
                    new[]
                    {
                        new SqlSchemaColumn("id", SqlSchemaValueKind.Numeric),
                        new SqlSchemaColumn("user_id", SqlSchemaValueKind.Numeric)
                    })
            });
        var context = new SqlPlanValidationContext("typed-v2").WithSchemaSnapshot(snapshot);
        var statements = new[]
        {
            "SELECT id FROM users WHERE id = (SELECT name FROM users)",
            "SELECT u.id FROM users u WHERE EXISTS " +
            "(SELECT 1 FROM orders o WHERE o.user_id = u.id AND o.user_id = 'bad')"
        };
        foreach (var sql in statements)
        {
            var error = Assert.Throws<SqlCompilationException>(() =>
                SqlCoreFacade.CompileQuery(
                    sql, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
                    context, new SqlExecutionPlanPolicy()));
            Assert.Equal("SQL_SCHEMA_TYPE_MISMATCH", error.Diagnostic?.Code);
        }
    }

    [Fact]
    public void TypedColumnInputs_AreDefensivelyCopiedAndValidated()
    {
        var input = new List<SqlSchemaColumn>
        {
            new("id", SqlSchemaValueKind.Numeric)
        };
        var table = SqlSchemaTable.WithTypes("users", input);
        input[0] = new SqlSchemaColumn("other", SqlSchemaValueKind.Text);
        Assert.Equal("id", table.TypedColumns[0].Name);
        Assert.Equal(SqlSchemaValueKind.Numeric, table.TypedColumns[0].Kind);
        Assert.Throws<ArgumentException>(() =>
            new SqlSchemaColumn("bad", (SqlSchemaValueKind)999));
    }
}
