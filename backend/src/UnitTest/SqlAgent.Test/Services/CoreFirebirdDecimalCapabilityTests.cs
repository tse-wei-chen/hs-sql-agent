using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreFirebirdDecimalCapabilityTests
{
    [Fact]
    public void Matrix_FirebirdExtendedDecimal_RequiresVersion4Profile()
    {
        Assert.Equal(
            SqlCapabilityStatus.Rejected,
            ExtendedDecimalCapability(
                SqlCapabilityMatrix.ForProvider(SqlAgentToolType.Firebird)).Status);
        Assert.Equal(
            SqlCapabilityStatus.Rejected,
            ExtendedDecimalCapability(
                SqlCapabilityMatrix.ForProvider(
                    SqlAgentToolType.Firebird,
                    FirebirdProfile(3))).Status);

        var supported = ExtendedDecimalCapability(
            SqlCapabilityMatrix.ForProvider(
                SqlAgentToolType.Firebird,
                FirebirdProfile(4)));
        Assert.Equal(SqlCapabilityStatus.Translated, supported.Status);
        Assert.Contains("4.0+", supported.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompileQuery_FirebirdLegacyPrecisionDecimal_PreservesFullScaleWithoutProfile()
    {
        const decimal value = 0.123456789012345678m;

        var command = CompileQuery(value, targetProfile: null);

        Assert.Contains("DECIMAL(18,18)", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DECIMAL(38,10)", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Single(command.Parameters);
        Assert.Equal(value, Assert.IsType<decimal>(command.Parameters[0].Value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(3)]
    public void CompileQuery_FirebirdExtendedPrecisionDecimal_FailsClosedWithoutVersion4(
        int? majorVersion)
    {
        const decimal value = 1234567890123456789.1m;

        var error = Assert.Throws<SqlCompilationException>(() =>
            CompileQuery(
                value,
                majorVersion is null ? null : FirebirdProfile(majorVersion.Value)));

        Assert.Contains("numeric.decimal_extended", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("4.0", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DECIMAL(20,1)", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompileQuery_Firebird4ExtendedPrecisionDecimal_UsesExactShape()
    {
        const decimal value = 1234567890123456789.1m;

        var command = CompileQuery(value, FirebirdProfile(4));

        Assert.Contains("DECIMAL(20,1)", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Single(command.Parameters);
        Assert.Equal(value, Assert.IsType<decimal>(command.Parameters[0].Value));
    }

    [Fact]
    public void CompileInsert_Firebird4ExtendedPrecisionDecimal_UsesSameTargetContract()
    {
        const decimal value = 1234567890123456789.1m;

        var command = CoreDmlCompiler.CreateDefault().Compile(
            DecimalInsert(value),
            SqlAgentToolType.Firebird,
            new SqlPlanValidationContext("firebird-decimal-v1"),
            new DmlCompilationPolicy(),
            FirebirdProfile(4));

        Assert.Contains("DECIMAL(20,1)", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Single(command.Parameters);
        Assert.Equal(value, Assert.IsType<decimal>(command.Parameters[0].Value));
    }

    private static CompiledSqlCommand CompileQuery(
        decimal value,
        SqlProviderCapabilityProfile? targetProfile) =>
        CoreSqlCompiler.CreateDefault().Compile(
            DecimalQuery(value),
            SqlAgentToolType.Firebird,
            new SqlPlanValidationContext("firebird-decimal-v1"),
            new SqlExecutionPlanPolicy(),
            targetProfile);

    private static ParsedStatement DecimalQuery(decimal value)
    {
        var definition = new QueryDefinition
        {
            TableName = "orders",
            SelectColumns =
            [
                new ConstantSelectCondition
                {
                    Constant = value,
                    Alias = "amount"
                }
            ]
        };

        return new ParsedStatement(
            QueryDefinitionCoreMapper.Map(definition),
            SqlAgentToolType.Postgres);
    }

    private static ParsedStatement DecimalInsert(decimal value)
    {
        var span = SourceSpan.Unknown;
        return new ParsedStatement(
            new InsertStatement(
                new NamedTableSource(
                    SqlIdentifier.Unquoted("orders", span),
                    Alias: null,
                    span),
                [SqlIdentifier.Unquoted("amount", span)],
                new InsertValuesSource(
                    [[new LiteralExpr(value, span)]],
                    span),
                span),
            SqlAgentToolType.Postgres);
    }

    private static SqlCapability ExtendedDecimalCapability(ProviderSqlCapabilities matrix) =>
        Assert.Single(
            matrix.Capabilities,
            item => item.Id == "numeric.decimal_extended");

    private static SqlProviderCapabilityProfile FirebirdProfile(int majorVersion) =>
        new(
            SqlAgentToolType.Firebird,
            ServerVersion: new Version(majorVersion, 0));
}
