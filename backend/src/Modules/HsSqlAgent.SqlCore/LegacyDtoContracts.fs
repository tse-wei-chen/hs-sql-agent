
namespace HsSqlAgent.SqlCore.Models

open System
open System.Collections.Generic
open HsSqlAgent.SqlCore.Enums

type QueryDefinition() =
    member val SourceDialect = Nullable<SqlAgentToolType>() with get, set
    member val TableName = String.Empty with get, set
    member val FromQuery: QueryDefinition | null = null with get, set
    member val Alias: string | null = null with get, set
    member val Distinct = false with get, set
    member val SelectColumns: List<SelectCondition> | null = null with get, set
    member val WhereColumnsAndValues: List<WhereCondition> | null = null with get, set
    member val OrderByColumns: List<OrderByCondition> | null = null with get, set
    member val GroupByConditions: List<GroupByCondition> | null = null with get, set
    member val HavingConditions: List<HavingCondition> | null = null with get, set
    member val Joins: List<JoinCondition> | null = null with get, set
    member val CombineConditions: List<CombineCondition> | null = null with get, set
    member val CteConditions: List<CteCondition> | null = null with get, set
    member val Limit = Nullable<int>() with get, set
    member val Offset = Nullable<int>() with get, set

and DmlDefinition() =
    member val Operation = DmlOperation.Insert with get, set
    member val TableName = String.Empty with get, set
    member val WhereConditions: List<WhereCondition> | null = null with get, set
    member val Values: List<NameValuePair> | null = null with get, set
    member val Columns: List<string> | null = null with get, set
    member val MultiValues: List<List<obj>> | null = null with get, set
    member val FromQuery: QueryDefinition | null = null with get, set
    member val ConfirmToken: string | null = null with get, set

and [<AbstractClass>] SelectCondition() =
    member val Alias: string | null = null with get, set

and FieldSelectCondition() =
    inherit SelectCondition()
    member val FieldName = String.Empty with get, set

and OperationSelectCondition() =
    inherit SelectCondition()
    member val Left: SelectCondition = Unchecked.defaultof<SelectCondition> with get, set
    member val Operator = ArithmeticOperator.Add with get, set
    member val Right: SelectCondition = Unchecked.defaultof<SelectCondition> with get, set

and ConstantSelectCondition() =
    inherit SelectCondition()
    member val Constant: obj = String.Empty :> obj with get, set

and CastSelectCondition() =
    inherit SelectCondition()
    member val Expression: SelectCondition = Unchecked.defaultof<SelectCondition> with get, set
    member val TypeName = String.Empty with get, set

and IntervalSelectCondition() =
    inherit SelectCondition()
    member val Literal = String.Empty with get, set

and FunctionSelectCondition() =
    inherit SelectCondition()
    member val FunctionName = String.Empty with get, set
    member val Arguments: List<SelectCondition> | null = null with get, set
    member val IsDistinct = false with get, set
    member val FilterWhereConditions: List<WhereCondition> | null = null with get, set
    member val Window: WindowDefinition | null = null with get, set

and CaseWhenSelectCondition() =
    inherit SelectCondition()
    member val CaseWhen = List<CaseWhenClause>() with get, set
    member val ElseValue: obj | null = null with get, set

and SubQuerySelectCondition() =
    inherit SelectCondition()
    member val TableName = String.Empty with get, set
    member val FromQuery: QueryDefinition | null = null with get, set
    member val Distinct = false with get, set
    member val SelectColumns: List<SelectCondition> | null = null with get, set
    member val WhereColumnsAndValues: List<WhereCondition> | null = null with get, set
    member val OrderByColumns: List<OrderByCondition> | null = null with get, set
    member val GroupByConditions: List<GroupByCondition> | null = null with get, set
    member val HavingConditions: List<HavingCondition> | null = null with get, set
    member val Joins: List<JoinCondition> | null = null with get, set
    member val CombineConditions: List<CombineCondition> | null = null with get, set
    member val CteConditions: List<CteCondition> | null = null with get, set
    member val Limit = Nullable<int>() with get, set
    member val Offset = Nullable<int>() with get, set

and TemplateSqlTokenSelectCondition() =
    inherit SelectCondition()
    member val Token = String.Empty with get, set

and TemplateExtractSelectCondition() =
    inherit SelectCondition()
    member val Unit: SelectCondition = Unchecked.defaultof<SelectCondition> with get, set
    member val Expression: SelectCondition = Unchecked.defaultof<SelectCondition> with get, set

and TemplateCaseSelectCondition() =
    inherit SelectCondition()
    member val Cases = List<TemplateCaseBranch>() with get, set
    member val ElseExpression: SelectCondition | null = null with get, set

and TemplateCaseBranch() =
    member val Condition: SelectCondition = Unchecked.defaultof<SelectCondition> with get, set
    member val Value: SelectCondition = Unchecked.defaultof<SelectCondition> with get, set

and [<AbstractClass>] WhereCondition() =
    member val IsOr = false with get, set
    member val IsNot = false with get, set

and BasicWhereCondition() =
    inherit WhereCondition()
    member val FieldName = String.Empty with get, set
    member val Operator = "=" with get, set
    member val Value: obj | null = null with get, set
    member val Values = List<obj>() with get, set
    member val IsDate = false with get, set

and ColumnCompareWhereCondition() =
    inherit WhereCondition()
    member val LeftFieldName = String.Empty with get, set
    member val Operator = "=" with get, set
    member val RightFieldName = String.Empty with get, set

and ExpressionWhereCondition() =
    inherit WhereCondition()
    member val LeftExpression: SelectCondition = Unchecked.defaultof<SelectCondition> with get, set
    member val Operator = "=" with get, set
    member val RightExpression: SelectCondition | null = null with get, set

and SubQueryWhereCondition() =
    inherit WhereCondition()
    member val FieldName: string | null = null with get, set
    member val Operator = "IN" with get, set
    member val SubQuery = QueryDefinition() with get, set

and GroupWhereCondition() =
    inherit WhereCondition()
    member val Groups = List<WhereCondition>() with get, set

and [<AbstractClass>] HavingCondition() =
    member val IsOr = false with get, set
    member val IsNot = false with get, set

and BasicHavingCondition() =
    inherit HavingCondition()
    member val FieldName = String.Empty with get, set
    member val Operator = "=" with get, set
    member val Value: obj | null = null with get, set
    member val IsDate = false with get, set

and FunctionHavingCondition() =
    inherit HavingCondition()
    member val LeftFunction = SqlFunctionCondition() with get, set
    member val Operator = ">" with get, set
    member val Value: obj | null = null with get, set

and ExpressionHavingCondition() =
    inherit HavingCondition()
    member val LeftExpression: SelectCondition = Unchecked.defaultof<SelectCondition> with get, set
    member val Operator = ">" with get, set
    member val RightExpression: SelectCondition | null = null with get, set

and GroupHavingCondition() =
    inherit HavingCondition()
    member val Groups = List<HavingCondition>() with get, set

and [<AbstractClass>] OrderByCondition() =
    member val Direction = SortDirection.Asc with get, set
    member val NullOrdering = HsSqlAgent.SqlCore.Enums.NullOrdering.Default with get, set

and FieldOrderByCondition() =
    inherit OrderByCondition()
    member val FieldName = String.Empty with get, set

and FunctionOrderByCondition() =
    inherit OrderByCondition()
    member val FunctionName = String.Empty with get, set
    member val Arguments: List<SelectCondition> | null = null with get, set
    member val IsDistinct = false with get, set
    member val FilterWhereConditions: List<WhereCondition> | null = null with get, set

and [<AbstractClass>] GroupByCondition() = class end

and FieldGroupByCondition() =
    inherit GroupByCondition()
    member val FieldName = String.Empty with get, set

and FunctionGroupByCondition() =
    inherit GroupByCondition()
    member val FunctionName = String.Empty with get, set
    member val Arguments: List<SelectCondition> | null = null with get, set
    member val IsDistinct = false with get, set
    member val FilterWhereConditions: List<WhereCondition> | null = null with get, set

and SqlFunctionCondition() =
    member val FunctionName = String.Empty with get, set
    member val Arguments: List<SelectCondition> | null = null with get, set
    member val IsDistinct = false with get, set
    member val FilterWhereConditions: List<WhereCondition> | null = null with get, set
    member val Window: WindowDefinition | null = null with get, set

and WindowDefinition() =
    member val PartitionBy: List<GroupByCondition> | null = null with get, set
    member val OrderBy: List<OrderByCondition> | null = null with get, set
    member val Frame: WindowFrameDefinition | null = null with get, set

and WindowFrameDefinition() =
    member val Unit = WindowFrameUnit.Rows with get, set
    member val Start = WindowFrameBound() with get, set
    member val End: WindowFrameBound | null = null with get, set

and WindowFrameBound() =
    member val Kind = WindowFrameBoundKind.CurrentRow with get, set
    member val Offset = Nullable<int>() with get, set

and JoinCondition() =
    let mutable subQuery: QueryDefinition | null = null
    member val Table = String.Empty with get, set
    member _.SubQuery
        with get() =
            if isNull subQuery then null
            elif String.IsNullOrWhiteSpace(subQuery.TableName) && isNull subQuery.FromQuery then null
            else subQuery
        and set value = subQuery <- value
    member val Alias: string | null = null with get, set
    member val Type = JoinType.Inner with get, set
    member val OnConditions = List<WhereCondition>() with get, set

and CteCondition() =
    member val CteAliasName = String.Empty with get, set
    member val Query = QueryDefinition() with get, set

and CombineCondition() =
    member val Type = CombineType.Union with get, set
    member val Query = QueryDefinition() with get, set

and CaseWhenClause() =
    member val Condition: WhereCondition = Unchecked.defaultof<WhereCondition> with get, set
    member val Value: obj = String.Empty :> obj with get, set

and NameValuePair() =
    member val FieldName = String.Empty with get, set
    member val Value: obj | null = null with get, set

type TestDbConnectionBase() =
    member val SqlProvider = Nullable<SqlAgentToolType>() with get, set
    member val Host: string | null = null with get, set
    member val Port: string | null = null with get, set
    member val Username: string | null = null with get, set
    member val Password: string | null = null with get, set
    member val Database: string | null = null with get, set
    member val ExtraSettings: string | null = null with get, set

type TestDbConnectionRequest() =
    inherit TestDbConnectionBase()
    member val DbSettingMode = 0 with get, set
    member val DbManagementId = Nullable<int>() with get, set

type TestDbConnectionVM() =
    member val IsSuccess = false with get, set
    member val ErrorMessage: string | null = null with get, set
