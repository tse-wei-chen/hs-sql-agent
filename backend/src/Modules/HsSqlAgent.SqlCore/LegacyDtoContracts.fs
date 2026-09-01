#nowarn "3261" "3262"

namespace HsSqlAgent.SqlCore.Models

open System
open System.Collections.Generic
open HsSqlAgent.SqlCore.Enums

[<AllowNullLiteral>]
type QueryDefinition() =
    member val SourceDialect = Nullable<SqlAgentToolType>() with get, set
    member val TableName = String.Empty with get, set
    member val FromQuery: QueryDefinition = null with get, set
    member val Alias: string = null with get, set
    member val Distinct = false with get, set
    member val SelectColumns: List<SelectCondition> = null with get, set
    member val WhereColumnsAndValues: List<WhereCondition> = null with get, set
    member val OrderByColumns: List<OrderByCondition> = null with get, set
    member val GroupByConditions: List<GroupByCondition> = null with get, set
    member val HavingConditions: List<HavingCondition> = null with get, set
    member val Joins: List<JoinCondition> = null with get, set
    member val CombineConditions: List<CombineCondition> = null with get, set
    member val CteConditions: List<CteCondition> = null with get, set
    member val Limit = Nullable<int>() with get, set
    member val Offset = Nullable<int>() with get, set

and [<AllowNullLiteral>] DmlDefinition() =
    member val Operation = DmlOperation.Insert with get, set
    member val TableName = String.Empty with get, set
    member val WhereConditions: List<WhereCondition> = null with get, set
    member val Values: List<NameValuePair> = null with get, set
    member val Columns: List<string> = null with get, set
    member val MultiValues: List<List<obj>> = null with get, set
    member val FromQuery: QueryDefinition = null with get, set
    member val ConfirmToken: string = null with get, set

and [<AbstractClass; AllowNullLiteral>] SelectCondition() =
    member val Alias: string = null with get, set

and [<AllowNullLiteral>] FieldSelectCondition() =
    inherit SelectCondition()
    member val FieldName = String.Empty with get, set

and [<AllowNullLiteral>] OperationSelectCondition() =
    inherit SelectCondition()
    member val Left: SelectCondition = null with get, set
    member val Operator = ArithmeticOperator.Add with get, set
    member val Right: SelectCondition = null with get, set

and [<AllowNullLiteral>] ConstantSelectCondition() =
    inherit SelectCondition()
    member val Constant: obj = String.Empty :> obj with get, set

and [<AllowNullLiteral>] CastSelectCondition() =
    inherit SelectCondition()
    member val Expression: SelectCondition = null with get, set
    member val TypeName = String.Empty with get, set

and [<AllowNullLiteral>] IntervalSelectCondition() =
    inherit SelectCondition()
    member val Literal = String.Empty with get, set

and [<AllowNullLiteral>] FunctionSelectCondition() =
    inherit SelectCondition()
    member val FunctionName = String.Empty with get, set
    member val Arguments: List<SelectCondition> = null with get, set
    member val IsDistinct = false with get, set
    member val FilterWhereConditions: List<WhereCondition> = null with get, set
    member val Window: WindowDefinition = null with get, set

and [<AllowNullLiteral>] CaseWhenSelectCondition() =
    inherit SelectCondition()
    member val CaseWhen = List<CaseWhenClause>() with get, set
    member val ElseValue: obj = null with get, set

and [<AllowNullLiteral>] SubQuerySelectCondition() =
    inherit SelectCondition()
    member val TableName = String.Empty with get, set
    member val FromQuery: QueryDefinition = null with get, set
    member val Distinct = false with get, set
    member val SelectColumns: List<SelectCondition> = null with get, set
    member val WhereColumnsAndValues: List<WhereCondition> = null with get, set
    member val OrderByColumns: List<OrderByCondition> = null with get, set
    member val GroupByConditions: List<GroupByCondition> = null with get, set
    member val HavingConditions: List<HavingCondition> = null with get, set
    member val Joins: List<JoinCondition> = null with get, set
    member val CombineConditions: List<CombineCondition> = null with get, set
    member val CteConditions: List<CteCondition> = null with get, set
    member val Limit = Nullable<int>() with get, set
    member val Offset = Nullable<int>() with get, set

and [<AllowNullLiteral>] TemplateSqlTokenSelectCondition() =
    inherit SelectCondition()
    member val Token = String.Empty with get, set

and [<AllowNullLiteral>] TemplateExtractSelectCondition() =
    inherit SelectCondition()
    member val Unit: SelectCondition = null with get, set
    member val Expression: SelectCondition = null with get, set

and [<AllowNullLiteral>] TemplateCaseSelectCondition() =
    inherit SelectCondition()
    member val Cases = List<TemplateCaseBranch>() with get, set
    member val ElseExpression: SelectCondition = null with get, set

and [<AllowNullLiteral>] TemplateCaseBranch() =
    member val Condition: SelectCondition = null with get, set
    member val Value: SelectCondition = null with get, set

and [<AbstractClass; AllowNullLiteral>] WhereCondition() =
    member val IsOr = false with get, set
    member val IsNot = false with get, set

and [<AllowNullLiteral>] BasicWhereCondition() =
    inherit WhereCondition()
    member val FieldName = String.Empty with get, set
    member val Operator = "=" with get, set
    member val Value: obj = null with get, set
    member val Values = List<obj>() with get, set
    member val IsDate = false with get, set

and [<AllowNullLiteral>] ColumnCompareWhereCondition() =
    inherit WhereCondition()
    member val LeftFieldName = String.Empty with get, set
    member val Operator = "=" with get, set
    member val RightFieldName = String.Empty with get, set

and [<AllowNullLiteral>] ExpressionWhereCondition() =
    inherit WhereCondition()
    member val LeftExpression: SelectCondition = null with get, set
    member val Operator = "=" with get, set
    member val RightExpression: SelectCondition = null with get, set

and [<AllowNullLiteral>] SubQueryWhereCondition() =
    inherit WhereCondition()
    member val FieldName: string = null with get, set
    member val Operator = "IN" with get, set
    member val SubQuery = QueryDefinition() with get, set

and [<AllowNullLiteral>] GroupWhereCondition() =
    inherit WhereCondition()
    member val Groups = List<WhereCondition>() with get, set

and [<AbstractClass; AllowNullLiteral>] HavingCondition() =
    member val IsOr = false with get, set
    member val IsNot = false with get, set

and [<AllowNullLiteral>] BasicHavingCondition() =
    inherit HavingCondition()
    member val FieldName = String.Empty with get, set
    member val Operator = "=" with get, set
    member val Value: obj = null with get, set
    member val IsDate = false with get, set

and [<AllowNullLiteral>] FunctionHavingCondition() =
    inherit HavingCondition()
    member val LeftFunction = SqlFunctionCondition() with get, set
    member val Operator = ">" with get, set
    member val Value: obj = null with get, set

and [<AllowNullLiteral>] ExpressionHavingCondition() =
    inherit HavingCondition()
    member val LeftExpression: SelectCondition = null with get, set
    member val Operator = ">" with get, set
    member val RightExpression: SelectCondition = null with get, set

and [<AllowNullLiteral>] GroupHavingCondition() =
    inherit HavingCondition()
    member val Groups = List<HavingCondition>() with get, set

and [<AbstractClass; AllowNullLiteral>] OrderByCondition() =
    member val Direction = SortDirection.Asc with get, set
    member val NullOrdering = HsSqlAgent.SqlCore.Enums.NullOrdering.Default with get, set

and [<AllowNullLiteral>] FieldOrderByCondition() =
    inherit OrderByCondition()
    member val FieldName = String.Empty with get, set

and [<AllowNullLiteral>] FunctionOrderByCondition() =
    inherit OrderByCondition()
    member val FunctionName = String.Empty with get, set
    member val Arguments: List<SelectCondition> = null with get, set
    member val IsDistinct = false with get, set
    member val FilterWhereConditions: List<WhereCondition> = null with get, set

and [<AbstractClass; AllowNullLiteral>] GroupByCondition() = class end

and [<AllowNullLiteral>] FieldGroupByCondition() =
    inherit GroupByCondition()
    member val FieldName = String.Empty with get, set

and [<AllowNullLiteral>] FunctionGroupByCondition() =
    inherit GroupByCondition()
    member val FunctionName = String.Empty with get, set
    member val Arguments: List<SelectCondition> = null with get, set
    member val IsDistinct = false with get, set
    member val FilterWhereConditions: List<WhereCondition> = null with get, set

and [<AllowNullLiteral>] SqlFunctionCondition() =
    member val FunctionName = String.Empty with get, set
    member val Arguments: List<SelectCondition> = null with get, set
    member val IsDistinct = false with get, set
    member val FilterWhereConditions: List<WhereCondition> = null with get, set
    member val Window: WindowDefinition = null with get, set

and [<AllowNullLiteral>] WindowDefinition() =
    member val PartitionBy: List<GroupByCondition> = null with get, set
    member val OrderBy: List<OrderByCondition> = null with get, set
    member val Frame: WindowFrameDefinition = null with get, set

and [<AllowNullLiteral>] WindowFrameDefinition() =
    member val Unit = WindowFrameUnit.Rows with get, set
    member val Start = WindowFrameBound() with get, set
    member val End: WindowFrameBound = null with get, set

and [<AllowNullLiteral>] WindowFrameBound() =
    member val Kind = WindowFrameBoundKind.CurrentRow with get, set
    member val Offset = Nullable<int>() with get, set

and [<AllowNullLiteral>] JoinCondition() =
    let mutable subQuery: QueryDefinition = null
    member val Table = String.Empty with get, set
    member _.SubQuery
        with get() =
            if isNull subQuery then null
            elif String.IsNullOrWhiteSpace(subQuery.TableName) && isNull subQuery.FromQuery then null
            else subQuery
        and set value = subQuery <- value
    member val Alias: string = null with get, set
    member val Type = JoinType.Inner with get, set
    member val OnConditions = List<WhereCondition>() with get, set

and [<AllowNullLiteral>] CteCondition() =
    member val CteAliasName = String.Empty with get, set
    member val Query = QueryDefinition() with get, set

and [<AllowNullLiteral>] CombineCondition() =
    member val Type = CombineType.Union with get, set
    member val Query = QueryDefinition() with get, set

and [<AllowNullLiteral>] CaseWhenClause() =
    member val Condition: WhereCondition = null with get, set
    member val Value: obj = String.Empty :> obj with get, set

and [<AllowNullLiteral>] NameValuePair() =
    member val FieldName = String.Empty with get, set
    member val Value: obj = null with get, set

[<AllowNullLiteral>]
type TestDbConnectionBase() =
    member val SqlProvider = Nullable<SqlAgentToolType>() with get, set
    member val Host: string = null with get, set
    member val Port: string = null with get, set
    member val Username: string = null with get, set
    member val Password: string = null with get, set
    member val Database: string = null with get, set
    member val ExtraSettings: string = null with get, set

[<AllowNullLiteral>]
type TestDbConnectionRequest() =
    inherit TestDbConnectionBase()
    member val DbSettingMode = 0 with get, set
    member val DbManagementId = Nullable<int>() with get, set

[<AllowNullLiteral>]
type TestDbConnectionVM() =
    member val IsSuccess = false with get, set
    member val ErrorMessage: string = null with get, set
