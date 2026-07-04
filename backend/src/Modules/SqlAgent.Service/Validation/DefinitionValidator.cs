using System.Text.Json;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;

namespace SqlAgent.Service.Validation;

public static class DefinitionValidator
{
    public static List<string> Validate(QueryDefinition? definition)
    {
        var errors = new List<string>();
        if (definition == null)
        {
            errors.Add("Validation error: QueryDefinition is null.");
            return errors;
        }
        ValidateQueryDefinition(definition, "", errors);
        return errors;
    }

    public static List<string> Validate(DmlDefinition? dml)
    {
        var errors = new List<string>();
        if (dml == null)
        {
            errors.Add("Validation error: DmlDefinition is null.");
            return errors;
        }
        if (string.IsNullOrWhiteSpace(dml.TableName))
            errors.Add("Validation error at `tableName`: must not be empty.");
        if (dml.FromQuery != null)
            ValidateQueryDefinition(dml.FromQuery, "fromQuery", errors);

        if (dml.WhereConditions?.Count > 0)
        {
            for (int i = 0; i < dml.WhereConditions.Count; i++)
            {
                if (dml.WhereConditions[i] == null)
                {
                    AddError(errors, $"whereConditions[{i}]", "must not be null.");
                    continue;
                }
                ValidateWhereCondition(dml.WhereConditions[i], $"whereConditions[{i}]", errors);
            }
        }

        if (dml.Values?.Count > 0)
        {
            for (int i = 0; i < dml.Values.Count; i++)
            {
                if (dml.Values[i] == null)
                    AddError(errors, $"values[{i}]", "must not be null.");
                else if (string.IsNullOrWhiteSpace(dml.Values[i].FieldName))
                    AddError(errors, $"values[{i}].fieldName", "must not be empty.");
            }
        }

        if (dml.Columns?.Count > 0)
        {
            for (int i = 0; i < dml.Columns.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(dml.Columns[i]))
                    AddError(errors, $"columns[{i}]", "must not be empty.");
            }
        }

        if (dml.MultiValues?.Count > 0)
        {
            for (int i = 0; i < dml.MultiValues.Count; i++)
            {
                if (dml.MultiValues[i] == null)
                    AddError(errors, $"multiValues[{i}]", "must not be null.");
            }
        }

        return errors;
    }

    private static void ValidateQueryDefinition(QueryDefinition qd, string path, List<string> errors)
    {
        var hasTableName = !string.IsNullOrWhiteSpace(qd.TableName);
        var hasFromQuery = qd.FromQuery != null;

        if (!hasTableName && !hasFromQuery)
            AddError(errors, path, "must have either `tableName` or `fromQuery`.");

        if (hasFromQuery)
            ValidateQueryDefinition(qd.FromQuery!, AppendPath(path, "fromQuery"), errors);

        if (qd.CteConditions?.Count > 0)
        {
            for (int i = 0; i < qd.CteConditions.Count; i++)
            {
                var cte = qd.CteConditions[i];
                var ctePath = AppendPath(path, $"cteConditions[{i}]");
                if (string.IsNullOrWhiteSpace(cte.CteAliasName))
                    AddError(errors, AppendPath(ctePath, "cteAliasName"), "must not be empty.");
                if (cte.Query == null)
                    AddError(errors, ctePath, "`query` must not be null.");
                else
                    ValidateQueryDefinition(cte.Query, ctePath, errors);
            }
        }

        if (qd.SelectColumns?.Count > 0)
        {
            for (int i = 0; i < qd.SelectColumns.Count; i++)
            {
                if (qd.SelectColumns[i] == null)
                {
                    AddError(errors, AppendPath(path, $"selectColumns[{i}]"), "must not be null.");
                    continue;
                }
                ValidateSelectCondition(qd.SelectColumns[i], AppendPath(path, $"selectColumns[{i}]"), errors);
            }
        }

        if (qd.WhereColumnsAndValues?.Count > 0)
        {
            for (int i = 0; i < qd.WhereColumnsAndValues.Count; i++)
            {
                if (qd.WhereColumnsAndValues[i] == null)
                {
                    AddError(errors, AppendPath(path, $"whereColumnsAndValues[{i}]"), "must not be null.");
                    continue;
                }
                ValidateWhereCondition(qd.WhereColumnsAndValues[i], AppendPath(path, $"whereColumnsAndValues[{i}]"), errors);
            }
        }

        if (qd.Joins?.Count > 0)
        {
            for (int i = 0; i < qd.Joins.Count; i++)
            {
                var join = qd.Joins[i];
                var joinPath = AppendPath(path, $"joins[{i}]");
                if (join == null)
                {
                    AddError(errors, joinPath, "must not be null.");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(join.Table) && join.SubQuery == null)
                    AddError(errors, joinPath, "must have either `table` or `subQuery`.");
                if (join.SubQuery != null)
                    ValidateQueryDefinition(join.SubQuery, AppendPath(joinPath, "subQuery"), errors);
                var requiresOnConditions = join.Type != JoinType.Cross;
                if (requiresOnConditions && (join.OnConditions == null || join.OnConditions.Count == 0))
                    AddError(errors, joinPath, "must have at least one `onConditions` entry.");
                else if (join.OnConditions?.Count > 0)
                {
                    for (int j = 0; j < join.OnConditions.Count; j++)
                    {
                        if (join.OnConditions[j] == null)
                        {
                            AddError(errors, AppendPath(joinPath, $"onConditions[{j}]"), "must not be null.");
                            continue;
                        }
                        ValidateWhereCondition(join.OnConditions[j], AppendPath(joinPath, $"onConditions[{j}]"), errors);
                    }
                }
            }
        }

        if (qd.GroupByConditions?.Count > 0)
        {
            for (int i = 0; i < qd.GroupByConditions.Count; i++)
            {
                if (qd.GroupByConditions[i] == null)
                {
                    AddError(errors, AppendPath(path, $"groupByConditions[{i}]"), "must not be null.");
                    continue;
                }
                ValidateGroupByCondition(qd.GroupByConditions[i], AppendPath(path, $"groupByConditions[{i}]"), errors);
            }
        }

        if (qd.HavingConditions?.Count > 0)
        {
            for (int i = 0; i < qd.HavingConditions.Count; i++)
            {
                if (qd.HavingConditions[i] == null)
                {
                    AddError(errors, AppendPath(path, $"havingConditions[{i}]"), "must not be null.");
                    continue;
                }
                ValidateHavingCondition(qd.HavingConditions[i], AppendPath(path, $"havingConditions[{i}]"), errors);
            }
        }

        if (qd.OrderByColumns?.Count > 0)
        {
            for (int i = 0; i < qd.OrderByColumns.Count; i++)
            {
                if (qd.OrderByColumns[i] == null)
                {
                    AddError(errors, AppendPath(path, $"orderByColumns[{i}]"), "must not be null.");
                    continue;
                }
                ValidateOrderByCondition(qd.OrderByColumns[i], AppendPath(path, $"orderByColumns[{i}]"), errors);
            }
        }

        if (qd.CombineConditions?.Count > 0)
        {
            for (int i = 0; i < qd.CombineConditions.Count; i++)
            {
                var cc = qd.CombineConditions[i];
                var ccPath = AppendPath(path, $"combineConditions[{i}]");
                if (cc == null)
                {
                    AddError(errors, ccPath, "must not be null.");
                    continue;
                }
                if (cc.Query == null)
                    AddError(errors, ccPath, "`query` must not be null.");
                else
                    ValidateQueryDefinition(cc.Query, ccPath, errors);
            }
        }
    }

    private static void ValidateSelectCondition(SelectCondition sc, string path, List<string> errors)
    {
        switch (sc)
        {
            case FieldSelectCondition field:
                if (string.IsNullOrWhiteSpace(field.FieldName))
                    AddError(errors, AppendPath(path, "fieldName"), "must not be empty.");
                else if (field.FieldName.Contains('(') || field.FieldName.Contains(')'))
                    AddError(errors, AppendPath(path, "fieldName"),
                        $"value '{field.FieldName}' contains parentheses. " +
                        "type: 'field' only allows pure column references. " +
                        "Use type: 'function' for SQL functions like COUNT, SUM, AVG.");
                break;

            case OperationSelectCondition op:
                if (op.Left == null)
                    AddError(errors, AppendPath(path, "left"), "must not be null for type: 'operation'.");
                else
                    ValidateSelectCondition(op.Left, AppendPath(path, "left"), errors);

                if (op.Right == null)
                    AddError(errors, AppendPath(path, "right"), "must not be null for type: 'operation'.");
                else
                    ValidateSelectCondition(op.Right, AppendPath(path, "right"), errors);
                break;

            case ConstantSelectCondition constant:
                if (constant.Constant is JsonElement je && je.ValueKind == JsonValueKind.Null)
                    AddError(errors, AppendPath(path, "constant"), "must not be explicitly null for type: 'constant'.");
                break;

            case FunctionSelectCondition func:
                if (string.IsNullOrWhiteSpace(func.FunctionName))
                    AddError(errors, AppendPath(path, "functionName"), "must not be empty for type: 'function'.");
                if (func.Arguments?.Count > 0)
                {
                    for (int i = 0; i < func.Arguments.Count; i++)
                    {
                        if (func.Arguments[i] == null)
                        {
                            AddError(errors, AppendPath(path, $"arguments[{i}]"), "must not be null.");
                            continue;
                        }
                        ValidateSelectCondition(func.Arguments[i], AppendPath(path, $"arguments[{i}]"), errors);
                    }
                }
                if (func.FilterWhereConditions?.Count > 0)
                {
                    for (int i = 0; i < func.FilterWhereConditions.Count; i++)
                    {
                        if (func.FilterWhereConditions[i] == null)
                        {
                            AddError(errors, AppendPath(path, $"filterWhereConditions[{i}]"), "must not be null.");
                            continue;
                        }
                        ValidateWhereCondition(func.FilterWhereConditions[i], AppendPath(path, $"filterWhereConditions[{i}]"), errors);
                    }
                }
                ValidateWindowDefinition(func.Window, AppendPath(path, "window"), errors);
                break;

            case CaseWhenSelectCondition caseWhen:
                if (caseWhen.CaseWhen == null || caseWhen.CaseWhen.Count == 0)
                    AddError(errors, AppendPath(path, "caseWhen"), "must have at least one case for type: 'case_when'.");
                else
                {
                    for (int i = 0; i < caseWhen.CaseWhen.Count; i++)
                    {
                        var clause = caseWhen.CaseWhen[i];
                        var clausePath = AppendPath(path, $"caseWhen[{i}]");
                        if (clause == null)
                        {
                            AddError(errors, clausePath, "must not be null.");
                            continue;
                        }
                        if (clause.Condition == null)
                            AddError(errors, AppendPath(clausePath, "condition"), "must not be null.");
                        else
                            ValidateWhereCondition(clause.Condition, AppendPath(clausePath, "condition"), errors);
                    }
                }
                break;

            case SubQuerySelectCondition sub:
                if (string.IsNullOrWhiteSpace(sub.TableName) && sub.FromQuery == null)
                    AddError(errors, path, "type: 'subquery' must have either `tableName` or `fromQuery`.");
                ValidateQueryDefinition(ConvertSubQueryToDefinition(sub), path, errors);
                break;
        }
    }

    private static void ValidateWhereCondition(WhereCondition wc, string path, List<string> errors)
    {
        switch (wc)
        {
            case BasicWhereCondition basic:
                if (string.IsNullOrWhiteSpace(basic.FieldName))
                    AddError(errors, AppendPath(path, "fieldName"), "must not be empty for type: 'basic'.");
                if (string.IsNullOrWhiteSpace(basic.Operator))
                    AddError(errors, AppendPath(path, "operator"), "must not be empty for type: 'basic'.");
                break;

            case ColumnCompareWhereCondition cc:
                if (string.IsNullOrWhiteSpace(cc.LeftFieldName))
                    AddError(errors, AppendPath(path, "leftFieldName"), "must not be empty for type: 'column_compare'.");
                if (string.IsNullOrWhiteSpace(cc.Operator))
                    AddError(errors, AppendPath(path, "operator"), "must not be empty for type: 'column_compare'.");
                if (string.IsNullOrWhiteSpace(cc.RightFieldName))
                    AddError(errors, AppendPath(path, "rightFieldName"), "must not be empty for type: 'column_compare'.");
                break;

            case ExpressionWhereCondition ex:
                if (ex.LeftExpression == null)
                    AddError(errors, AppendPath(path, "leftExpression"), "must not be null for type: 'expression'.");
                if (string.IsNullOrWhiteSpace(ex.Operator))
                    AddError(errors, AppendPath(path, "operator"), "must not be empty for type: 'expression'.");
                break;

            case SubQueryWhereCondition sq:
                if (string.IsNullOrWhiteSpace(sq.Operator))
                    AddError(errors, AppendPath(path, "operator"), "must not be empty for type: 'subquery'.");
                if (sq.SubQuery == null)
                    AddError(errors, AppendPath(path, "subQuery"), "must not be null for type: 'subquery'.");
                else
                    ValidateQueryDefinition(sq.SubQuery, AppendPath(path, "subQuery"), errors);
                break;

            case GroupWhereCondition group:
                if (group.Groups == null || group.Groups.Count == 0)
                    AddError(errors, AppendPath(path, "groups"), "must have at least one condition for type: 'group'.");
                else
                {
                    for (int i = 0; i < group.Groups.Count; i++)
                    {
                        if (group.Groups[i] == null)
                        {
                            AddError(errors, AppendPath(path, $"groups[{i}]"), "must not be null.");
                            continue;
                        }
                        ValidateWhereCondition(group.Groups[i], AppendPath(path, $"groups[{i}]"), errors);
                    }
                }
                break;
        }
    }

    private static void ValidateHavingCondition(HavingCondition hc, string path, List<string> errors)
    {
        switch (hc)
        {
            case BasicHavingCondition basic:
                if (string.IsNullOrWhiteSpace(basic.FieldName))
                    AddError(errors, AppendPath(path, "fieldName"), "must not be empty for type: 'basic'.");
                if (string.IsNullOrWhiteSpace(basic.Operator))
                    AddError(errors, AppendPath(path, "operator"), "must not be empty for type: 'basic'.");
                break;

            case FunctionHavingCondition func:
                if (func.LeftFunction == null)
                    AddError(errors, AppendPath(path, "leftFunction"), "must not be null for type: 'function_compare'.");
                else
                    ValidateFunctionCondition(func.LeftFunction, AppendPath(path, "leftFunction"), errors);
                if (string.IsNullOrWhiteSpace(func.Operator))
                    AddError(errors, AppendPath(path, "operator"), "must not be empty for type: 'function_compare'.");
                break;

            case ExpressionHavingCondition ex:
                if (ex.LeftExpression == null)
                    AddError(errors, AppendPath(path, "leftExpression"), "must not be null for type: 'expression'.");
                else
                    ValidateSelectCondition(ex.LeftExpression, AppendPath(path, "leftExpression"), errors);
                if (string.IsNullOrWhiteSpace(ex.Operator))
                    AddError(errors, AppendPath(path, "operator"), "must not be empty for type: 'expression'.");
                if (ex.RightExpression != null)
                    ValidateSelectCondition(ex.RightExpression, AppendPath(path, "rightExpression"), errors);
                break;

            case GroupHavingCondition group:
                if (group.Groups == null || group.Groups.Count == 0)
                    AddError(errors, AppendPath(path, "groups"), "must have at least one condition for type: 'group'.");
                else
                {
                    for (int i = 0; i < group.Groups.Count; i++)
                    {
                        if (group.Groups[i] == null)
                        {
                            AddError(errors, AppendPath(path, $"groups[{i}]"), "must not be null.");
                            continue;
                        }
                        ValidateHavingCondition(group.Groups[i], AppendPath(path, $"groups[{i}]"), errors);
                    }
                }
                break;
        }
    }

    private static void ValidateOrderByCondition(OrderByCondition obc, string path, List<string> errors)
    {
        switch (obc)
        {
            case FieldOrderByCondition field:
                if (string.IsNullOrWhiteSpace(field.FieldName))
                    AddError(errors, AppendPath(path, "fieldName"), "must not be empty for type: 'field'.");
                break;

            case FunctionOrderByCondition func:
                if (string.IsNullOrWhiteSpace(func.FunctionName))
                    AddError(errors, AppendPath(path, "functionName"), "must not be empty for type: 'function'.");
                if (func.Arguments?.Count > 0)
                {
                    for (int i = 0; i < func.Arguments.Count; i++)
                    {
                        if (func.Arguments[i] == null)
                        {
                            AddError(errors, AppendPath(path, $"arguments[{i}]"), "must not be null.");
                            continue;
                        }
                        ValidateSelectCondition(func.Arguments[i], AppendPath(path, $"arguments[{i}]"), errors);
                    }
                }
                if (func.FilterWhereConditions?.Count > 0)
                {
                    for (int i = 0; i < func.FilterWhereConditions.Count; i++)
                    {
                        if (func.FilterWhereConditions[i] == null)
                        {
                            AddError(errors, AppendPath(path, $"filterWhereConditions[{i}]"), "must not be null.");
                            continue;
                        }
                        ValidateWhereCondition(func.FilterWhereConditions[i], AppendPath(path, $"filterWhereConditions[{i}]"), errors);
                    }
                }
                break;
        }
    }

    private static void ValidateGroupByCondition(GroupByCondition gbc, string path, List<string> errors)
    {
        switch (gbc)
        {
            case FieldGroupByCondition field:
                if (string.IsNullOrWhiteSpace(field.FieldName))
                    AddError(errors, AppendPath(path, "fieldName"), "must not be empty for type: 'field'.");
                break;

            case FunctionGroupByCondition func:
                if (string.IsNullOrWhiteSpace(func.FunctionName))
                    AddError(errors, AppendPath(path, "functionName"), "must not be empty for type: 'function'.");
                if (func.Arguments?.Count > 0)
                {
                    for (int i = 0; i < func.Arguments.Count; i++)
                    {
                        if (func.Arguments[i] == null)
                        {
                            AddError(errors, AppendPath(path, $"arguments[{i}]"), "must not be null.");
                            continue;
                        }
                        ValidateSelectCondition(func.Arguments[i], AppendPath(path, $"arguments[{i}]"), errors);
                    }
                }
                if (func.FilterWhereConditions?.Count > 0)
                {
                    for (int i = 0; i < func.FilterWhereConditions.Count; i++)
                    {
                        if (func.FilterWhereConditions[i] == null)
                        {
                            AddError(errors, AppendPath(path, $"filterWhereConditions[{i}]"), "must not be null.");
                            continue;
                        }
                        ValidateWhereCondition(func.FilterWhereConditions[i], AppendPath(path, $"filterWhereConditions[{i}]"), errors);
                    }
                }
                break;
        }
    }

    private static void ValidateFunctionCondition(SqlFunctionCondition func, string path, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(func.FunctionName))
            AddError(errors, AppendPath(path, "functionName"), "must not be empty.");
        if (func.Arguments?.Count > 0)
        {
            for (int i = 0; i < func.Arguments.Count; i++)
            {
                if (func.Arguments[i] == null)
                {
                    AddError(errors, AppendPath(path, $"arguments[{i}]"), "must not be null.");
                    continue;
                }
                ValidateSelectCondition(func.Arguments[i], AppendPath(path, $"arguments[{i}]"), errors);
            }
        }
        if (func.FilterWhereConditions?.Count > 0)
        {
            for (int i = 0; i < func.FilterWhereConditions.Count; i++)
            {
                if (func.FilterWhereConditions[i] == null)
                {
                    AddError(errors, AppendPath(path, $"filterWhereConditions[{i}]"), "must not be null.");
                    continue;
                }
                ValidateWhereCondition(func.FilterWhereConditions[i], AppendPath(path, $"filterWhereConditions[{i}]"), errors);
            }
        }
        ValidateWindowDefinition(func.Window, AppendPath(path, "window"), errors);
    }

    private static void ValidateWindowDefinition(WindowDefinition? window, string path, List<string> errors)
    {
        if (window != null)
        {
            if (window.PartitionBy?.Count > 0)
            {
                for (int i = 0; i < window.PartitionBy.Count; i++)
                {
                    if (window.PartitionBy[i] == null)
                    {
                        AddError(errors, AppendPath(path, $"partitionBy[{i}]"), "must not be null.");
                        continue;
                    }
                    ValidateGroupByCondition(window.PartitionBy[i], AppendPath(path, $"partitionBy[{i}]"), errors);
                }
            }
            if (window.OrderBy?.Count > 0)
            {
                for (int i = 0; i < window.OrderBy.Count; i++)
                {
                    if (window.OrderBy[i] == null)
                    {
                        AddError(errors, AppendPath(path, $"orderBy[{i}]"), "must not be null.");
                        continue;
                    }
                    ValidateOrderByCondition(window.OrderBy[i], AppendPath(path, $"orderBy[{i}]"), errors);
                }
            }
        }
    }

    private static QueryDefinition ConvertSubQueryToDefinition(SubQuerySelectCondition sq)
    {
        return new QueryDefinition
        {
            TableName = sq.TableName,
            FromQuery = sq.FromQuery,
            Alias = sq.Alias,
            Distinct = sq.Distinct,
            SelectColumns = sq.SelectColumns,
            WhereColumnsAndValues = sq.WhereColumnsAndValues,
            OrderByColumns = sq.OrderByColumns,
            GroupByConditions = sq.GroupByConditions,
            HavingConditions = sq.HavingConditions,
            Joins = sq.Joins,
            CombineConditions = sq.CombineConditions,
            CteConditions = sq.CteConditions,
            Limit = sq.Limit,
            Offset = sq.Offset
        };
    }

    private static string AppendPath(string basePath, string segment)
    {
        return string.IsNullOrEmpty(basePath) ? segment : $"{basePath}.{segment}";
    }

    private static void AddError(List<string> errors, string path, string message)
    {
        errors.Add($"Validation error at `{path}`: {message}");
    }
}
