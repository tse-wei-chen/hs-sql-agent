// ===== Enums =====
export type ArithmeticOperator = "Add" | "Subtract" | "Multiply" | "Divide";
export type JoinType = "Inner" | "Left" | "Right" | "Full" | "Cross";
export type CombineType = "Union" | "UnionAll" | "Intersect" | "Except";
export type SortDirection = "Asc" | "Desc" | "Random";
export type DmlOperation = "Insert" | "Update" | "Delete";

// ===== QueryDefinition =====
export interface QueryDefinition {
  tableName: string;
  fromQuery?: QueryDefinition | null;
  alias?: string | null;
  distinct: boolean;
  selectColumns?: SelectCondition[] | null;
  whereColumnsAndValues?: WhereCondition[] | null;
  orderByColumns?: OrderByCondition[] | null;
  groupByConditions?: GroupByCondition[] | null;
  havingConditions?: HavingCondition[] | null;
  joins?: JoinCondition[] | null;
  combineConditions?: CombineCondition[] | null;
  cteConditions?: CteCondition[] | null;
  limit?: number | null;
  offset?: number | null;
}

// ===== SelectCondition (polymorphic) =====
export type SelectCondition =
  | FieldSelectCondition
  | OperationSelectCondition
  | ConstantSelectCondition
  | FunctionSelectCondition
  | CaseWhenSelectCondition
  | SubQuerySelectCondition;

export interface FieldSelectCondition {
  type: "field";
  fieldName: string;
  alias?: string | null;
}

export interface OperationSelectCondition {
  type: "operation";
  left: SelectArithmeticCondition;
  operator: ArithmeticOperator;
  right: SelectArithmeticCondition;
  alias?: string | null;
}

export interface ConstantSelectCondition {
  type: "constant";
  constant: unknown;
  alias?: string | null;
}

export interface FunctionSelectCondition {
  type: "function";
  functionName: string;
  arguments?: SqlFunctionArgument[] | null;
  isDistinct?: boolean;
  filterWhereConditions?: WhereCondition[] | null;
  alias?: string | null;
}

export interface CaseWhenSelectCondition {
  type: "case_when";
  caseWhen: CaseWhenClause[];
  elseValue?: unknown;
  alias?: string | null;
}

export interface SubQuerySelectCondition {
  type: "subquery";
  tableName: string;
  fromQuery?: QueryDefinition | null;
  alias?: string | null;
  distinct: boolean;
  selectColumns?: SelectCondition[] | null;
  whereColumnsAndValues?: WhereCondition[] | null;
  orderByColumns?: OrderByCondition[] | null;
  groupByConditions?: GroupByCondition[] | null;
  havingConditions?: HavingCondition[] | null;
  joins?: JoinCondition[] | null;
  combineConditions?: CombineCondition[] | null;
  cteConditions?: CteCondition[] | null;
  limit?: number | null;
  offset?: number | null;
}

// ===== SelectArithmeticCondition (polymorphic) =====
export type SelectArithmeticCondition =
  | FieldArithmeticCondition
  | ConstantArithmeticCondition
  | OperationArithmeticCondition
  | FunctionArithmeticCondition
  | CaseWhenArithmeticCondition;

export interface FieldArithmeticCondition {
  type: "field";
  fieldName: string;
}

export interface ConstantArithmeticCondition {
  type: "constant";
  constant: unknown;
}

export interface OperationArithmeticCondition {
  type: "operation";
  left: SelectArithmeticCondition;
  operator: ArithmeticOperator;
  right: SelectArithmeticCondition;
}

export interface FunctionArithmeticCondition {
  type: "function";
  functionName: string;
  arguments?: SqlFunctionArgument[] | null;
  isDistinct?: boolean;
  filterWhereConditions?: WhereCondition[] | null;
}

export interface CaseWhenArithmeticCondition {
  type: "case_when";
  caseWhen: CaseWhenClause[];
  elseValue?: unknown;
}

// ===== WhereCondition (polymorphic) =====
export type WhereCondition =
  | BasicWhereCondition
  | ColumnCompareWhereCondition
  | InWhereCondition
  | SubQueryWhereCondition
  | GroupWhereCondition;

export interface BasicWhereCondition {
  type: "basic";
  fieldName: string;
  operator: string;
  value?: unknown;
  isDate?: boolean;
  isOr?: boolean;
  isNot?: boolean;
}

export interface ColumnCompareWhereCondition {
  type: "column_compare";
  leftFieldName: string;
  operator: string;
  rightFieldName: string;
  isOr?: boolean;
  isNot?: boolean;
}

export interface InWhereCondition {
  type: "in";
  fieldName: string;
  operator: string;
  values: unknown[];
  isDate?: boolean;
  isOr?: boolean;
  isNot?: boolean;
}

export interface SubQueryWhereCondition {
  type: "subquery";
  fieldName?: string | null;
  operator: string;
  subQuery: QueryDefinition;
  isOr?: boolean;
  isNot?: boolean;
}

export interface GroupWhereCondition {
  type: "group";
  groups: WhereCondition[];
  isOr?: boolean;
  isNot?: boolean;
}

// ===== OrderByCondition (polymorphic) =====
export type OrderByCondition =
  | FieldOrderByCondition
  | FunctionOrderByCondition;

export interface FieldOrderByCondition {
  type: "field";
  fieldName: string;
  direction: SortDirection;
}

export interface FunctionOrderByCondition {
  type: "function";
  functionName: string;
  arguments?: SqlFunctionArgument[] | null;
  isDistinct?: boolean;
  filterWhereConditions?: WhereCondition[] | null;
  direction: SortDirection;
}

// ===== GroupByCondition (polymorphic) =====
export type GroupByCondition =
  | FieldGroupByCondition
  | FunctionGroupByCondition;

export interface FieldGroupByCondition {
  type: "field";
  fieldName: string;
}

export interface FunctionGroupByCondition {
  type: "function";
  functionName: string;
  arguments?: SqlFunctionArgument[] | null;
  isDistinct?: boolean;
  filterWhereConditions?: WhereCondition[] | null;
}

// ===== HavingCondition (polymorphic) =====
export type HavingCondition =
  | BasicHavingCondition
  | FunctionHavingCondition
  | GroupHavingCondition;

export interface BasicHavingCondition {
  type: "basic";
  fieldName: string;
  operator: string;
  value?: unknown;
  isDate?: boolean;
  isOr?: boolean;
  isNot?: boolean;
}

export interface FunctionHavingCondition {
  type: "function_compare";
  leftFunction: SqlFunctionCondition;
  operator: string;
  value?: unknown;
  isOr?: boolean;
  isNot?: boolean;
}

export interface GroupHavingCondition {
  type: "group";
  groups: HavingCondition[];
  isOr?: boolean;
  isNot?: boolean;
}

// ===== JoinCondition =====
export interface JoinCondition {
  table: string;
  subQuery?: QueryDefinition | null;
  alias?: string | null;
  type: JoinType;
  onConditions: WhereCondition[];
}

// ===== CombineCondition =====
export interface CombineCondition {
  type: CombineType;
  query: QueryDefinition;
}

// ===== CteCondition =====
export interface CteCondition {
  cteAliasName: string;
  query: QueryDefinition;
}

// ===== SqlFunctionCondition =====
export interface SqlFunctionCondition {
  functionName: string;
  arguments?: SqlFunctionArgument[] | null;
  isDistinct?: boolean;
  filterWhereConditions?: WhereCondition[] | null;
  window?: WindowDefinition | null;
}

// ===== SqlFunctionArgument (polymorphic) =====
export type SqlFunctionArgument =
  | FieldFunctionArgument
  | ConstantFunctionArgument
  | NestedFunctionArgument
  | ArithmeticFunctionArgument;

export interface FieldFunctionArgument {
  type: "field";
  fieldName: string;
}

export interface ConstantFunctionArgument {
  type: "constant";
  constant: unknown;
}

export interface NestedFunctionArgument {
  type: "function";
  functionName: string;
  arguments?: SqlFunctionArgument[] | null;
  isDistinct?: boolean;
  filterWhereConditions?: WhereCondition[] | null;
}

export interface ArithmeticFunctionArgument {
  type: "operation";
  left: SelectArithmeticCondition;
  operator: ArithmeticOperator;
  right: SelectArithmeticCondition;
}

// ===== WindowDefinition =====
export interface WindowDefinition {
  partitionBy?: GroupByCondition[] | null;
  orderBy?: OrderByCondition[] | null;
}

// ===== CaseWhenClause =====
export interface CaseWhenClause {
  condition: WhereCondition;
  value: unknown;
}

// ===== DmlDefinition =====
export interface DmlDefinition {
  operation: DmlOperation;
  tableName: string;
  whereConditions?: WhereCondition[] | null;
  values?: NameValuePair[] | null;
  columns?: string[] | null;
  multiValues?: unknown[][] | null;
  fromQuery?: QueryDefinition | null;
  confirmToken?: string | null;
}

// ===== NameValuePair =====
export interface NameValuePair {
  fieldName: string;
  value?: unknown;
}
