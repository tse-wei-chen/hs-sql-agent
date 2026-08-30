using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreQuotedIdentifierLoweringTests
{
    [Fact]
    public void Compile_PostgresQuotedTableColumnAndAlias_PreservesSpacesAndDots()
    {
        var command = Compile(
            "SELECT \"Order.Detail\".\"Line Item\" AS \"Display Name\" " +
            "FROM \"Order.Detail\" AS \"Order.Detail\"",
            SqlAgentToolType.Postgres);

        Assert.Contains("\"Order.Detail\".\"Line Item\"", command.Sql, StringComparison.Ordinal);
        Assert.Contains("AS \"Display Name\"", command.Sql, StringComparison.Ordinal);
        Assert.Contains("FROM \"Order.Detail\" AS \"Order.Detail\"", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_PostgresQuotedIdentifier_EscapesEmbeddedDelimiter()
    {
        var command = Compile(
            "SELECT \"a\"\"b\" FROM \"T\"",
            SqlAgentToolType.Postgres);

        Assert.Contains("\"a\"\"b\"", command.Sql, StringComparison.Ordinal);
        Assert.Contains("\"T\"", command.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_PostgresNamedJoin_PreservesStructuredQuotedSources()
    {
        var command = Compile(
            "SELECT \"Left Scope\".id, \"Right Scope\".id " +
            "FROM \"Left Table\" AS \"Left Scope\" " +
            "JOIN \"Right Table\" AS \"Right Scope\" " +
            "ON \"Left Scope\".id = \"Right Scope\".id",
            SqlAgentToolType.Postgres);

        Assert.Contains("\"Left Table\" AS \"Left Scope\"", command.Sql, StringComparison.Ordinal);
        Assert.Contains("\"Right Table\" AS \"Right Scope\"", command.Sql, StringComparison.Ordinal);
        Assert.Contains("\"Left Scope\".\"id\"", command.Sql, StringComparison.Ordinal);
        Assert.Contains("\"Right Scope\".\"id\"", command.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_PostgresQuotedCteName_PreservesQuoteIntent()
    {
        var command = Compile(
            "WITH \"My Cte\" AS (SELECT 1 AS id) " +
            "SELECT \"My Cte\".id FROM \"My Cte\"",
            SqlAgentToolType.Postgres);

        Assert.Contains("\"My Cte\"", command.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("\"my cte\"", command.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Bind_PostgresQuotedAliasesDifferingOnlyByCase_AreDistinct()
    {
        var command = Compile(
            "SELECT \"Foo\".id, \"foo\".id " +
            "FROM users AS \"Foo\" " +
            "JOIN accounts AS \"foo\" ON \"Foo\".id = \"foo\".id",
            SqlAgentToolType.Postgres);

        Assert.Contains("\"Foo\".\"id\"", command.Sql, StringComparison.Ordinal);
        Assert.Contains("\"foo\".\"id\"", command.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Bind_PostgresUnquotedQualifier_DoesNotMatchDifferentlyCasedQuotedAlias()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            Compile(
                "SELECT foo.id FROM users AS \"Foo\"",
                SqlAgentToolType.Postgres));
        Assert.Contains("unknown table/alias qualifier", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Bind_PostgresUnquotedQualifier_MatchesLowercaseQuotedAlias()
    {
        var command = Compile(
            "SELECT foo.id FROM users AS \"foo\"",
            SqlAgentToolType.Postgres);

        Assert.Contains("\"foo\".\"id\"", command.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Bind_QuotedCanonicalLookingFunction_FailsBeforeNormalization()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            Compile(
                "SELECT \"CORE_DATE_ADD\"(1, 2, 3)",
                SqlAgentToolType.Postgres));
        Assert.Contains("quoted or qualified function identifier", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_QualifiedFunction_FailsClosedBeforeRegistryNameFlattening()
    {
        Assert.Throws<SqlParseException>(() => CoreSqlTextParser.ParseQuery(
            "SELECT custom.fn(id) FROM users",
            SqlAgentToolType.Postgres));
    }

    [Theory]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Compile_UpperFoldingProviders_UppercaseUnquotedButPreserveQuoted(
        SqlAgentToolType provider)
    {
        var unquoted = Compile("SELECT mixedcase FROM mixedtable", provider);
        Assert.Contains("MIXEDCASE", unquoted.Sql, StringComparison.Ordinal);
        Assert.Contains("MIXEDTABLE", unquoted.Sql, StringComparison.Ordinal);

        var quoted = Compile("SELECT \"MixedCase\" FROM \"MixedTable\"", provider);
        Assert.Contains("\"MixedCase\"", quoted.Sql, StringComparison.Ordinal);
        Assert.Contains("\"MixedTable\"", quoted.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_StructuredDtoOraclePhysicalNamesFoldButOutputAliasStaysExact()
    {
        var statement = QueryDefinitionCoreMapper.Map(new QueryDefinition
        {
            TableName = "MixedTable",
            Alias = "t",
            SelectColumns =
            [
                new FieldSelectCondition
                {
                    FieldName = "t.MixedColumn",
                    Alias = "result_name"
                }
            ],
            OrderByColumns =
            [
                new FieldOrderByCondition
                {
                    FieldName = "RESULT_NAME",
                    Direction = SortDirection.Asc
                }
            ]
        });

        var command = CoreSqlCompiler.CreateDefault().Compile(
            new ParsedStatement(statement, SqlAgentToolType.Oracle),
            SqlAgentToolType.Oracle,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy());

        Assert.Contains("FROM \"MIXEDTABLE\" \"T\"", command.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("\"MIXEDTABLE\" AS \"T\"", command.Sql, StringComparison.Ordinal);
        Assert.Contains("\"T\".\"MIXEDCOLUMN\" AS \"result_name\"", command.Sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY \"result_name\"", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_StructuredDtoPostgresPhysicalNamesFoldButOutputAliasStaysExact()
    {
        var statement = QueryDefinitionCoreMapper.Map(new QueryDefinition
        {
            TableName = "Users",
            Alias = "U",
            SelectColumns =
            [
                new FieldSelectCondition
                {
                    FieldName = "U.Name",
                    Alias = "DisplayName"
                }
            ],
            OrderByColumns =
            [
                new FieldOrderByCondition
                {
                    FieldName = "displayname",
                    Direction = SortDirection.Asc
                }
            ]
        });

        var command = CoreSqlCompiler.CreateDefault().Compile(
            new ParsedStatement(statement, SqlAgentToolType.Postgres),
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy());

        Assert.Contains("FROM \"users\" AS \"u\"", command.Sql, StringComparison.Ordinal);
        Assert.Contains("\"u\".\"name\" AS \"DisplayName\"", command.Sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY \"DisplayName\"", command.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_WindowOrderByMultipleItems_KeepsCommaSeparator()
    {
        var command = Compile(
            "SELECT ROW_NUMBER() OVER (ORDER BY created_at ASC, id DESC) FROM users",
            SqlAgentToolType.Postgres);

        Assert.Contains(
            "ORDER BY \"created_at\" ASC, \"id\" DESC",
            command.Sql,
            StringComparison.OrdinalIgnoreCase);
    }

    private static HsSqlAgent.SqlCore.Core.Compilation.CompiledSqlCommand Compile(
        string sql,
        SqlAgentToolType provider) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, provider),
            provider,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy());
}
