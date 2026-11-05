using Xunit;

namespace SqlAgent.Test.Services;

public class SqlAstBindingContractTests
{
    [Fact]
    public void Binding_QualifiedColumn_ResolvesAliasAndFacts()
    {
        var definition = new QueryDefinition
        {
            TableName = "sales.orders",
            Alias = "o",
            SelectColumns = [new FieldSelectCondition { FieldName = "o.id" }]
        };

        var parsed = Parsed(definition);
        var facts = HsSqlAgent.SqlCore.SqlCoreInspection.GetQueryFacts(parsed);

        Assert.Contains("sales.orders", facts.ReferencedTables);
        Assert.Contains(facts.Aliases, alias =>
            alias.Alias == "o" && alias.Target == "sales.orders");

        var command = Compile(parsed);
        Assert.Contains("orders", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("o", command.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Binding_UnknownQualifier_FailsClosed()
    {
        var definition = new QueryDefinition
        {
            TableName = "users",
            Alias = "u",
            SelectColumns = [new FieldSelectCondition { FieldName = "x.id" }]
        };

        var error = Assert.Throws<InvalidOperationException>(() =>
            HsSqlAgent.SqlCore.SqlCoreInspection.GetQueryFacts(Parsed(definition)));

        Assert.Contains(
            "unknown table/alias qualifier",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Binding_CteReference_IsNotCountedAsPhysicalTable()
    {
        var definition = new QueryDefinition
        {
            TableName = "recent",
            Alias = "r",
            CteConditions =
            [
                new CteCondition
                {
                    CteAliasName = "recent",
                    Query = new QueryDefinition
                    {
                        TableName = "sales.orders",
                        SelectColumns = [new FieldSelectCondition { FieldName = "id" }]
                    }
                }
            ],
            SelectColumns = [new FieldSelectCondition { FieldName = "r.id" }]
        };

        var facts = HsSqlAgent.SqlCore.SqlCoreInspection.GetQueryFacts(Parsed(definition));

        Assert.Contains("sales.orders", facts.ReferencedTables);
        Assert.DoesNotContain("recent", facts.ReferencedTables);
        Assert.True(facts.ContainsCte);
    }

    [Fact]
    public void Binding_CorrelatedSubquery_ResolvesOuterAlias()
    {
        var definition = new QueryDefinition
        {
            TableName = "users",
            Alias = "u",
            SelectColumns = [new FieldSelectCondition { FieldName = "u.id" }],
            WhereColumnsAndValues =
            [
                new SubQueryWhereCondition
                {
                    Operator = "EXISTS",
                    SubQuery = new QueryDefinition
                    {
                        TableName = "orders",
                        Alias = "o",
                        SelectColumns = [new FieldSelectCondition { FieldName = "o.id" }],
                        WhereColumnsAndValues =
                        [
                            new ColumnCompareWhereCondition
                            {
                                LeftFieldName = "o.user_id",
                                Operator = "=",
                                RightFieldName = "u.id"
                            }
                        ]
                    }
                }
            ]
        };

        var parsed = Parsed(definition);
        var facts = HsSqlAgent.SqlCore.SqlCoreInspection.GetQueryFacts(parsed);

        Assert.Contains("users", facts.ReferencedTables);
        Assert.Contains("orders", facts.ReferencedTables);
        Assert.True(facts.ContainsSubquery);

        var command = Compile(parsed);
        Assert.Contains("user_id", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("u", command.Sql, StringComparison.Ordinal);
        Assert.Contains("o", command.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Binding_CorrelatedSubquery_LocalAliasShadowing_RemainsLocal()
    {
        var definition = new QueryDefinition
        {
            TableName = "users",
            Alias = "u",
            SelectColumns = [new FieldSelectCondition { FieldName = "u.id" }],
            WhereColumnsAndValues =
            [
                new SubQueryWhereCondition
                {
                    Operator = "EXISTS",
                    SubQuery = new QueryDefinition
                    {
                        TableName = "orders",
                        Alias = "u",
                        SelectColumns = [new FieldSelectCondition { FieldName = "u.id" }],
                        WhereColumnsAndValues =
                        [
                            new ColumnCompareWhereCondition
                            {
                                LeftFieldName = "u.user_id",
                                Operator = "=",
                                RightFieldName = "u.id"
                            }
                        ]
                    }
                }
            ]
        };

        var parsed = Parsed(definition);
        var facts = HsSqlAgent.SqlCore.SqlCoreInspection.GetQueryFacts(parsed);

        Assert.Equal(2, facts.Aliases.Count(alias => alias.Alias == "u"));
        Assert.Equal(
            2,
            facts.Aliases
                .Where(alias => alias.Alias == "u")
                .Select(alias => alias.ScopeId)
                .Distinct()
                .Count());

        _ = Compile(parsed);
    }

    private static ParsedStatement Parsed(QueryDefinition definition) =>
        new(
            QueryDefinitionCoreMapper.Map(definition),
            SqlAgentToolType.Postgres);

    private static CompiledSqlCommand Compile(ParsedStatement parsed) =>
        CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("binding-contract-v2"),
            new SqlExecutionPlanPolicy());
}
