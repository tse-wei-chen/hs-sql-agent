using System.Text.Json;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
using SqlAgent.Service.Validation;
using Xunit;

namespace SqlAgent.Test.Services;

public class DefinitionValidatorTests
{
    [Fact]
    public void Validate_QueryDefinitionNull_ReturnsError()
    {
        var errors = DefinitionValidator.Validate((QueryDefinition?)null);
        Assert.Single(errors);
        Assert.Contains("null", errors[0]);
    }

    [Fact]
    public void Validate_QueryDefinitionNoTableNameNoFromQuery_ReturnsError()
    {
        var qd = new QueryDefinition();
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("tableName") && e.Contains("fromQuery"));
    }

    [Fact]
    public void Validate_QueryDefinitionWithTableName_ReturnsNoErrors()
    {
        var qd = new QueryDefinition { TableName = "users" };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_QueryDefinitionWithFromQuery_ReturnsNoErrors()
    {
        var qd = new QueryDefinition
        {
            FromQuery = new QueryDefinition { TableName = "sub" },
            SelectColumns =
            [
                new FieldSelectCondition { FieldName = "x" }
            ]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Empty(errors);
    }

    // ── CTE ──────────────────────────────────────────────

    [Fact]
    public void Validate_CteConditionEmptyAlias_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            CteConditions =
            [
                new CteCondition { CteAliasName = "", Query = new QueryDefinition { TableName = "t" } }
            ]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("cteAliasName") && e.Contains("empty"));
    }

    [Fact]
    public void Validate_CteConditionNullQuery_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            CteConditions =
            [
                new CteCondition { CteAliasName = "cte", Query = null! }
            ]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("cteConditions[0]") && e.Contains("null"));
    }

    [Fact]
    public void Validate_CteConditionValid_ReturnsNoErrors()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            CteConditions =
            [
                new CteCondition { CteAliasName = "cte", Query = new QueryDefinition { TableName = "t" } }
            ]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Empty(errors);
    }

    // ── SelectColumns ────────────────────────────────────

    [Fact]
    public void Validate_SelectColumnNullEntry_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            SelectColumns = [null!]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("selectColumns[0]") && e.Contains("null"));
    }

    [Fact]
    public void Validate_SelectColumnFieldEmpty_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            SelectColumns = [new FieldSelectCondition { FieldName = "" }]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("fieldName") && e.Contains("empty"));
    }

    [Fact]
    public void Validate_SelectColumnFieldWithParentheses_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            SelectColumns = [new FieldSelectCondition { FieldName = "COUNT(*)" }]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("parentheses"));
        Assert.Contains(errors, e => e.Contains("type: 'field'"));
    }

    [Fact]
    public void Validate_SelectColumnOperationNullLeft_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            SelectColumns =
            [
                new OperationSelectCondition
                {
                    Left = null!,
                    Right = new ConstantSelectCondition { Constant = 1 }
                }
            ]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("left") && e.Contains("null"));
    }

    [Fact]
    public void Validate_SelectColumnOperationNullRight_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            SelectColumns =
            [
                new OperationSelectCondition
                {
                    Left = new ConstantSelectCondition { Constant = 1 },
                    Right = null!
                }
            ]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("right") && e.Contains("null"));
    }

    [Fact]
    public void Validate_SelectColumnConstantJsonNull_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            SelectColumns =
            [
                new ConstantSelectCondition
                {
                    Constant = JsonSerializer.Deserialize<JsonElement>("null")
                }
            ]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("constant") && e.Contains("null"));
    }

    [Fact]
    public void Validate_SelectColumnConstantCSharpNull_ReturnsNoErrors()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            SelectColumns =
            [
                new ConstantSelectCondition { Constant = null! }
            ]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_SelectColumnFunctionEmptyName_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            SelectColumns = [new FunctionSelectCondition { FunctionName = "" }]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("functionName") && e.Contains("empty"));
    }

    [Fact]
    public void Validate_SelectColumnFunctionNullArgument_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            SelectColumns =
            [
                new FunctionSelectCondition
                {
                    FunctionName = "COALESCE",
                    Arguments = [null!]
                }
            ]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("arguments[0]") && e.Contains("null"));
    }

    [Fact]
    public void Validate_SelectColumnFunctionFilterWhereConditionNull_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            SelectColumns =
            [
                new FunctionSelectCondition
                {
                    FunctionName = "COUNT",
                    Arguments = [new FieldSelectCondition { FieldName = "id" }],
                    FilterWhereConditions = [null!]
                }
            ]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("filterWhereConditions[0]") && e.Contains("null"));
    }

    [Fact]
    public void Validate_SelectColumnFunctionValid_ReturnsNoErrors()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            SelectColumns =
            [
                new FunctionSelectCondition
                {
                    FunctionName = "COUNT",
                    Arguments = [new FieldSelectCondition { FieldName = "id" }]
                }
            ]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_SelectColumnCaseWhenEmptyCases_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            SelectColumns = [new CaseWhenSelectCondition { CaseWhen = [] }]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("caseWhen") && e.Contains("at least one"));
    }

    [Fact]
    public void Validate_SelectColumnCaseWhenNullClause_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            SelectColumns =
            [
                new CaseWhenSelectCondition
                {
                    CaseWhen = [null!]
                }
            ]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("caseWhen[0]") && e.Contains("null"));
    }

    [Fact]
    public void Validate_SelectColumnCaseWhenNullCondition_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            SelectColumns =
            [
                new CaseWhenSelectCondition
                {
                    CaseWhen =
                    [
                        new CaseWhenClause { Condition = null!, Value = 1 }
                    ]
                }
            ]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("condition") && e.Contains("null"));
    }

    [Fact]
    public void Validate_SelectColumnCaseWhenValid_ReturnsNoErrors()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            SelectColumns =
            [
                new CaseWhenSelectCondition
                {
                    CaseWhen =
                    [
                        new CaseWhenClause
                        {
                            Condition = new BasicWhereCondition { FieldName = "status", Operator = "=", Value = 1 },
                            Value = "'active'"
                        }
                    ]
                }
            ]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_SelectColumnSubQueryNoTableNoFromQuery_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            SelectColumns =
            [
                new SubQuerySelectCondition()
            ]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("subquery") && e.Contains("tableName") && e.Contains("fromQuery"));
    }

    [Fact]
    public void Validate_SelectColumnSubQueryValid_ReturnsNoErrors()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            SelectColumns =
            [
                new SubQuerySelectCondition
                {
                    TableName = "orders",
                    SelectColumns = [new FieldSelectCondition { FieldName = "total" }],
                    WhereColumnsAndValues =
                    [
                        new BasicWhereCondition { FieldName = "status", Operator = "=", Value = "active" }
                    ]
                }
            ]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Empty(errors);
    }

    // ── WhereConditions ──────────────────────────────────

    [Fact]
    public void Validate_WhereConditionNullEntry_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            WhereColumnsAndValues = [null!]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("whereColumnsAndValues[0]") && e.Contains("null"));
    }

    [Fact]
    public void Validate_WhereConditionBasicEmptyFieldName_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            WhereColumnsAndValues = [new BasicWhereCondition { FieldName = "", Operator = "=" }]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("fieldName") && e.Contains("empty"));
    }

    [Fact]
    public void Validate_WhereConditionBasicEmptyOperator_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            WhereColumnsAndValues = [new BasicWhereCondition { FieldName = "status", Operator = "" }]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("operator") && e.Contains("empty"));
    }

    [Fact]
    public void Validate_WhereConditionBasicValid_ReturnsNoErrors()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            WhereColumnsAndValues = [new BasicWhereCondition { FieldName = "status", Operator = "=", Value = 1 }]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_WhereConditionColumnCompareEmptyLeftField_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            WhereColumnsAndValues =
            [
                new ColumnCompareWhereCondition { LeftFieldName = "", Operator = "=", RightFieldName = "r" }
            ]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("leftFieldName") && e.Contains("empty"));
    }

    [Fact]
    public void Validate_WhereConditionColumnCompareEmptyOperator_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            WhereColumnsAndValues =
            [
                new ColumnCompareWhereCondition { LeftFieldName = "l", Operator = "", RightFieldName = "r" }
            ]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("operator") && e.Contains("empty"));
    }

    [Fact]
    public void Validate_WhereConditionColumnCompareEmptyRightField_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            WhereColumnsAndValues =
            [
                new ColumnCompareWhereCondition { LeftFieldName = "l", Operator = "=", RightFieldName = "" }
            ]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("rightFieldName") && e.Contains("empty"));
    }

    [Fact]
    public void Validate_WhereConditionSubQueryEmptyOperator_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            WhereColumnsAndValues =
            [
                new SubQueryWhereCondition
                {
                    Operator = "",
                    SubQuery = new QueryDefinition { TableName = "t" }
                }
            ]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("operator") && e.Contains("empty"));
    }

    [Fact]
    public void Validate_WhereConditionSubQueryNullSubQuery_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            WhereColumnsAndValues =
            [
                new SubQueryWhereCondition { SubQuery = null! }
            ]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("subQuery") && e.Contains("null"));
    }

    [Fact]
    public void Validate_WhereConditionGroupEmptyGroups_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            WhereColumnsAndValues = [new GroupWhereCondition { Groups = [] }]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("groups") && e.Contains("at least one"));
    }

    [Fact]
    public void Validate_WhereConditionGroupNullEntry_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            WhereColumnsAndValues =
            [
                new GroupWhereCondition
                {
                    Groups =
                    [
                        new BasicWhereCondition { FieldName = "x", Operator = "=", Value = 1 },
                        null!
                    ]
                }
            ]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("groups[1]") && e.Contains("null"));
    }

    [Fact]
    public void Validate_WhereConditionGroupValid_ReturnsNoErrors()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            WhereColumnsAndValues =
            [
                new GroupWhereCondition
                {
                    Groups =
                    [
                        new BasicWhereCondition { FieldName = "x", Operator = "=", Value = 1 }
                    ]
                }
            ]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Empty(errors);
    }

    // ── Joins ────────────────────────────────────────────

    [Fact]
    public void Validate_JoinNullEntry_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            Joins = [null!]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("joins[0]") && e.Contains("null"));
    }

    [Fact]
    public void Validate_JoinNoTableNoSubQuery_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            Joins =
            [
                new JoinCondition
                {
                    Table = "",
                    OnConditions =
                    [
                        new ColumnCompareWhereCondition
                        {
                            LeftFieldName = "a", Operator = "=", RightFieldName = "b"
                        }
                    ]
                }
            ]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("joins[0]") && e.Contains("table") && e.Contains("subQuery"));
    }

    [Fact]
    public void Validate_JoinSubQueryValidated_ReturnsNoErrors()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            Joins =
            [
                new JoinCondition
                {
                    SubQuery = new QueryDefinition { TableName = "sub" },
                    OnConditions =
                    [
                        new ColumnCompareWhereCondition
                        {
                            LeftFieldName = "a", Operator = "=", RightFieldName = "b"
                        }
                    ]
                }
            ]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_JoinNoOnConditions_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            Joins =
            [
                new JoinCondition
                {
                    Table = "orders",
                    OnConditions = []
                }
            ]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("joins[0]") && e.Contains("onConditions"));
    }

    [Fact]
    public void Validate_JoinNullOnConditionEntry_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            Joins =
            [
                new JoinCondition
                {
                    Table = "orders",
                    OnConditions = [null!]
                }
            ]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("onConditions[0]") && e.Contains("null"));
    }

    // ── GroupBy ──────────────────────────────────────────

    [Fact]
    public void Validate_GroupByNullEntry_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            GroupByConditions = [null!]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("groupByConditions[0]") && e.Contains("null"));
    }

    [Fact]
    public void Validate_GroupByFieldEmptyFieldName_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            GroupByConditions = [new FieldGroupByCondition { FieldName = "" }]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("fieldName") && e.Contains("empty"));
    }

    [Fact]
    public void Validate_GroupByFunctionEmptyName_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            GroupByConditions = [new FunctionGroupByCondition { FunctionName = "" }]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("functionName") && e.Contains("empty"));
    }

    [Fact]
    public void Validate_GroupByNullArgument_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            GroupByConditions =
            [
                new FunctionGroupByCondition
                {
                    FunctionName = "ROLLUP",
                    Arguments = [null!]
                }
            ]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("arguments[0]") && e.Contains("null"));
    }

    [Fact]
    public void Validate_GroupByFilterWhereNull_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            GroupByConditions =
            [
                new FunctionGroupByCondition
                {
                    FunctionName = "GROUPING",
                    FilterWhereConditions = [null!]
                }
            ]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("filterWhereConditions[0]") && e.Contains("null"));
    }

    // ── Having ───────────────────────────────────────────

    [Fact]
    public void Validate_HavingNullEntry_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            HavingConditions = [null!]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("havingConditions[0]") && e.Contains("null"));
    }

    [Fact]
    public void Validate_HavingBasicEmptyFieldName_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            HavingConditions = [new BasicHavingCondition { FieldName = "", Operator = ">" }]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("fieldName") && e.Contains("empty"));
    }

    [Fact]
    public void Validate_HavingBasicEmptyOperator_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            HavingConditions = [new BasicHavingCondition { FieldName = "total", Operator = "" }]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("operator") && e.Contains("empty"));
    }

    [Fact]
    public void Validate_HavingFunctionNullLeftFunction_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            HavingConditions =
            [
                new FunctionHavingCondition { LeftFunction = null! }
            ]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("leftFunction") && e.Contains("null"));
    }

    [Fact]
    public void Validate_HavingFunctionEmptyOperator_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            HavingConditions =
            [
                new FunctionHavingCondition
                {
                    LeftFunction = new SqlFunctionCondition
                    {
                        FunctionName = "SUM",
                        Arguments = [new FieldSelectCondition { FieldName = "amount" }]
                    },
                    Operator = ""
                }
            ]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("operator") && e.Contains("empty"));
    }

    [Fact]
    public void Validate_HavingGroupEmptyGroups_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            HavingConditions = [new GroupHavingCondition { Groups = [] }]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("groups") && e.Contains("at least one"));
    }

    [Fact]
    public void Validate_HavingGroupNullEntry_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            HavingConditions =
            [
                new GroupHavingCondition
                {
                    Groups = [null!]
                }
            ]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("groups[0]") && e.Contains("null"));
    }

    // ── OrderBy ──────────────────────────────────────────

    [Fact]
    public void Validate_OrderByNullEntry_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            OrderByColumns = [null!]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("orderByColumns[0]") && e.Contains("null"));
    }

    [Fact]
    public void Validate_OrderByFieldEmptyFieldName_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            OrderByColumns = [new FieldOrderByCondition { FieldName = "" }]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("fieldName") && e.Contains("empty"));
    }

    [Fact]
    public void Validate_OrderByFunctionEmptyName_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            OrderByColumns = [new FunctionOrderByCondition { FunctionName = "" }]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("functionName") && e.Contains("empty"));
    }

    [Fact]
    public void Validate_OrderByFunctionNullArgument_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            OrderByColumns =
            [
                new FunctionOrderByCondition
                {
                    FunctionName = "RAND",
                    Arguments = [null!]
                }
            ]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("arguments[0]") && e.Contains("null"));
    }

    [Fact]
    public void Validate_OrderByFunctionFilterWhereNull_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            OrderByColumns =
            [
                new FunctionOrderByCondition
                {
                    FunctionName = "RANK",
                    FilterWhereConditions = [null!]
                }
            ]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("filterWhereConditions[0]") && e.Contains("null"));
    }

    // ── CombineConditions ────────────────────────────────

    [Fact]
    public void Validate_CombineNullEntry_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            CombineConditions = [null!]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("combineConditions[0]") && e.Contains("null"));
    }

    [Fact]
    public void Validate_CombineNullQuery_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            CombineConditions = [new CombineCondition { Query = null! }]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("combineConditions[0]") && e.Contains("null"));
    }

    [Fact]
    public void Validate_CombineValid_ReturnsNoErrors()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            CombineConditions =
            [
                new CombineCondition { Query = new QueryDefinition { TableName = "archived_users" } }
            ]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Empty(errors);
    }

    // ── SqlFunctionCondition (shared) ────────────────────

    [Fact]
    public void Validate_FunctionConditionEmptyName_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            HavingConditions =
            [
                new FunctionHavingCondition
                {
                    LeftFunction = new SqlFunctionCondition { FunctionName = "" },
                    Operator = ">",
                    Value = 0
                }
            ]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("functionName") && e.Contains("empty"));
    }

    [Fact]
    public void Validate_FunctionConditionNullArgument_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            HavingConditions =
            [
                new FunctionHavingCondition
                {
                    LeftFunction = new SqlFunctionCondition
                    {
                        FunctionName = "SUM",
                        Arguments = [null!]
                    },
                    Operator = ">",
                    Value = 0
                }
            ]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("arguments[0]") && e.Contains("null"));
    }

    [Fact]
    public void Validate_FunctionConditionNullFilterWhere_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            HavingConditions =
            [
                new FunctionHavingCondition
                {
                    LeftFunction = new SqlFunctionCondition
                    {
                        FunctionName = "SUM",
                        Arguments = [new FieldSelectCondition { FieldName = "amount" }],
                        FilterWhereConditions = [null!]
                    },
                    Operator = ">",
                    Value = 0
                }
            ]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("filterWhereConditions[0]") && e.Contains("null"));
    }

    [Fact]
    public void Validate_FunctionConditionWindowNullPartitionBy_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            HavingConditions =
            [
                new FunctionHavingCondition
                {
                    LeftFunction = new SqlFunctionCondition
                    {
                        FunctionName = "ROW_NUMBER",
                        Window = new WindowDefinition
                        {
                            PartitionBy = [null!],
                            OrderBy = [new FieldOrderByCondition { FieldName = "id" }]
                        }
                    },
                    Operator = ">",
                    Value = 0
                }
            ]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("partitionBy[0]") && e.Contains("null"));
    }

    [Fact]
    public void Validate_FunctionConditionWindowNullOrderBy_ReturnsError()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            HavingConditions =
            [
                new FunctionHavingCondition
                {
                    LeftFunction = new SqlFunctionCondition
                    {
                        FunctionName = "ROW_NUMBER",
                        Window = new WindowDefinition
                        {
                            PartitionBy = [new FieldGroupByCondition { FieldName = "dept" }],
                            OrderBy = [null!]
                        }
                    },
                    Operator = ">",
                    Value = 0
                }
            ]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Contains(errors, e => e.Contains("orderBy[0]") && e.Contains("null"));
    }

    [Fact]
    public void Validate_FunctionConditionWindowValid_ReturnsNoErrors()
    {
        var qd = new QueryDefinition
        {
            TableName = "users",
            HavingConditions =
            [
                new FunctionHavingCondition
                {
                    LeftFunction = new SqlFunctionCondition
                    {
                        FunctionName = "ROW_NUMBER",
                        Window = new WindowDefinition
                        {
                            PartitionBy = [new FieldGroupByCondition { FieldName = "dept" }],
                            OrderBy = [new FieldOrderByCondition { FieldName = "salary" }]
                        }
                    },
                    Operator = ">",
                    Value = 0
                }
            ]
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Empty(errors);
    }

    // ── DmlDefinition ────────────────────────────────────

    [Fact]
    public void Validate_DmlNull_ReturnsError()
    {
        var errors = DefinitionValidator.Validate((DmlDefinition?)null);
        Assert.Single(errors);
        Assert.Contains("null", errors[0]);
    }

    [Fact]
    public void Validate_DmlEmptyTableName_ReturnsError()
    {
        var dml = new DmlDefinition { TableName = "" };
        var errors = DefinitionValidator.Validate(dml);
        Assert.Contains(errors, e => e.Contains("tableName") && e.Contains("empty"));
    }

    [Fact]
    public void Validate_DmlValid_ReturnsNoErrors()
    {
        var dml = new DmlDefinition { TableName = "users" };
        var errors = DefinitionValidator.Validate(dml);
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_DmlFromQuery_ValidatesSubQuery()
    {
        var dml = new DmlDefinition
        {
            TableName = "users",
            FromQuery = new QueryDefinition()
        };
        var errors = DefinitionValidator.Validate(dml);
        Assert.Contains(errors, e => e.Contains("fromQuery") && e.Contains("tableName") && e.Contains("fromQuery"));
    }

    [Fact]
    public void Validate_DmlWhereConditionNullEntry_ReturnsError()
    {
        var dml = new DmlDefinition
        {
            TableName = "users",
            WhereConditions = [null!]
        };
        var errors = DefinitionValidator.Validate(dml);
        Assert.Contains(errors, e => e.Contains("whereConditions[0]") && e.Contains("null"));
    }

    [Fact]
    public void Validate_DmlWhereConditionValidated_ReturnsError()
    {
        var dml = new DmlDefinition
        {
            TableName = "users",
            WhereConditions =
            [
                new BasicWhereCondition { FieldName = "", Operator = "=" }
            ]
        };
        var errors = DefinitionValidator.Validate(dml);
        Assert.Contains(errors, e => e.Contains("fieldName") && e.Contains("empty"));
    }

    [Fact]
    public void Validate_DmlValuesNullEntry_ReturnsError()
    {
        var dml = new DmlDefinition
        {
            TableName = "users",
            Values = [null!]
        };
        var errors = DefinitionValidator.Validate(dml);
        Assert.Contains(errors, e => e.Contains("values[0]") && e.Contains("null"));
    }

    [Fact]
    public void Validate_DmlValuesEmptyFieldName_ReturnsError()
    {
        var dml = new DmlDefinition
        {
            TableName = "users",
            Values = [new NameValuePair { FieldName = "", Value = 1 }]
        };
        var errors = DefinitionValidator.Validate(dml);
        Assert.Contains(errors, e => e.Contains("fieldName") && e.Contains("empty"));
    }

    [Fact]
    public void Validate_DmlValuesValid_ReturnsNoErrors()
    {
        var dml = new DmlDefinition
        {
            TableName = "users",
            Values = [new NameValuePair { FieldName = "name", Value = "test" }]
        };
        var errors = DefinitionValidator.Validate(dml);
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_DmlColumnsEmptyEntry_ReturnsError()
    {
        var dml = new DmlDefinition
        {
            TableName = "users",
            Columns = [""]
        };
        var errors = DefinitionValidator.Validate(dml);
        Assert.Contains(errors, e => e.Contains("columns[0]") && e.Contains("empty"));
    }

    [Fact]
    public void Validate_DmlMultiValuesNullEntry_ReturnsError()
    {
        var dml = new DmlDefinition
        {
            TableName = "users",
            Columns = ["name"],
            MultiValues = [null!]
        };
        var errors = DefinitionValidator.Validate(dml);
        Assert.Contains(errors, e => e.Contains("multiValues[0]") && e.Contains("null"));
    }

    [Fact]
    public void Validate_DmlAllFieldsValid_ReturnsNoErrors()
    {
        var dml = new DmlDefinition
        {
            TableName = "users",
            WhereConditions =
            [
                new BasicWhereCondition { FieldName = "id", Operator = "=", Value = 1 }
            ],
            Values = [new NameValuePair { FieldName = "name", Value = "test" }],
            Columns = ["name"],
            MultiValues = [["test"]]
        };
        var errors = DefinitionValidator.Validate(dml);
        Assert.Empty(errors);
    }

    // ── Positive / valid happy paths ─────────────────────

    [Fact]
    public void Validate_ComplexQuery_ReturnsNoErrors()
    {
        var qd = new QueryDefinition
        {
            TableName = "orders",
            Alias = "o",
            SelectColumns =
            [
                new FieldSelectCondition { FieldName = "o.id", Alias = "order_id" },
                new FunctionSelectCondition
                {
                    FunctionName = "SUM",
                    Arguments = [new FieldSelectCondition { FieldName = "o.total" }],
                    Alias = "total_amount"
                },
                new OperationSelectCondition
                {
                    Left = new FieldSelectCondition { FieldName = "o.quantity" },
                    Operator = ArithmeticOperator.Multiply,
                    Right = new FieldSelectCondition { FieldName = "o.unit_price" },
                    Alias = "line_total"
                }
            ],
            Joins =
            [
                new JoinCondition
                {
                    Table = "customers",
                    Alias = "c",
                    Type = JoinType.Left,
                    OnConditions =
                    [
                        new ColumnCompareWhereCondition
                        {
                            LeftFieldName = "o.customer_id",
                            Operator = "=",
                            RightFieldName = "c.id"
                        }
                    ]
                }
            ],
            WhereColumnsAndValues =
            [
                new GroupWhereCondition
                {
                    Groups =
                    [
                        new BasicWhereCondition { FieldName = "o.status", Operator = "=", Value = "active" },
                        new BasicWhereCondition { FieldName = "o.deleted", Operator = "=", Value = false, IsOr = true }
                    ]
                }
            ],
            GroupByConditions =
            [
                new FieldGroupByCondition { FieldName = "o.id" }
            ],
            HavingConditions =
            [
                new BasicHavingCondition { FieldName = "total_amount", Operator = ">", Value = 100 }
            ],
            OrderByColumns =
            [
                new FieldOrderByCondition { FieldName = "total_amount", Direction = SortDirection.Desc }
            ],
            Limit = 10,
            Offset = 0
        };
        var errors = DefinitionValidator.Validate(qd);
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_QueryDefinitionValidFromQuery_ReturnsNoErrors()
    {
        var inner = new QueryDefinition
        {
            TableName = "orders",
            Alias = "o",
            SelectColumns =
            [
                new FieldSelectCondition { FieldName = "o.customer_id" },
                new FieldSelectCondition { FieldName = "o.total" }
            ]
        };
        var outer = new QueryDefinition
        {
            FromQuery = inner,
            Alias = "t",
            SelectColumns =
            [
                new FunctionSelectCondition
                {
                    FunctionName = "SUM",
                    Arguments = [new FieldSelectCondition { FieldName = "t.total" }],
                    Alias = "grand_total"
                }
            ],
            GroupByConditions =
            [
                new FieldGroupByCondition { FieldName = "t.customer_id" }
            ]
        };
        var errors = DefinitionValidator.Validate(outer);
        Assert.Empty(errors);
    }
}
