using Xunit;

namespace SqlAgent.Test.Services;

public class SqlAstBinderTests
{
    [Fact]
    public void Bind_QualifiedColumn_ResolvesAliasAndFacts()
    {
        var dto = new QueryDefinition
        {
            TableName = "sales.orders",
            Alias = "o",
            SelectColumns = [new FieldSelectCondition { FieldName = "o.id" }]
        };
        var parsed = new ParsedStatement(QueryDefinitionCoreMapper.Map(dto), SqlAgentToolType.Postgres);

        var bound = new SqlAstBinder().Bind(parsed);

        var select = Assert.IsType<SelectStatement>(bound.Statement);
        var column = Assert.IsType<BoundColumnExpr>(select.Select[0].Expression);
        Assert.NotNull(column.Source);
        Assert.Equal("sales.orders", column.Source!.Name);
        Assert.Equal("o", column.Source.Alias);
        Assert.Contains("sales.orders", bound.Facts.ReferencedTables);
    }

    [Fact]
    public void Bind_UnknownQualifier_FailsClosed()
    {
        var dto = new QueryDefinition
        {
            TableName = "users",
            Alias = "u",
            SelectColumns = [new FieldSelectCondition { FieldName = "x.id" }]
        };
        var parsed = new ParsedStatement(QueryDefinitionCoreMapper.Map(dto), SqlAgentToolType.Postgres);

        var ex = Assert.Throws<InvalidOperationException>(() => new SqlAstBinder().Bind(parsed));

        Assert.Contains("unknown table/alias qualifier", ex.Message);
    }

    [Fact]
    public void Bind_CteReference_IsNotCountedAsPhysicalTable()
    {
        var dto = new QueryDefinition
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
        var parsed = new ParsedStatement(QueryDefinitionCoreMapper.Map(dto), SqlAgentToolType.Postgres);

        var bound = new SqlAstBinder().Bind(parsed);

        Assert.Contains("sales.orders", bound.Facts.ReferencedTables);
        Assert.DoesNotContain("recent", bound.Facts.ReferencedTables);
        Assert.True(bound.Facts.ContainsCte);
    }

    [Fact]
    public void Bind_CorrelatedSubquery_ResolvesOuterAlias()
    {
        var outer = new QueryDefinition
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
        var parsed = new ParsedStatement(QueryDefinitionCoreMapper.Map(outer), SqlAgentToolType.Postgres);

        var bound = new SqlAstBinder().Bind(parsed);

        var select = Assert.IsType<SelectStatement>(bound.Statement);
        var exists = Assert.IsType<ExistsExpr>(select.Where);
        var inner = Assert.IsType<SelectStatement>(exists.Query);
        var comparison = Assert.IsType<BinaryExpr>(inner.Where);
        var outerColumn = Assert.IsType<BoundColumnExpr>(comparison.Right);
        Assert.Equal("users", outerColumn.Source!.Name);
        Assert.Equal("u", outerColumn.Source.Alias);
    }
}
