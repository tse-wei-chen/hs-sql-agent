import { ref, computed } from "vue";
import { getSchemas, getTables, getColumns } from "@/api/db-management";
import type {
  SortDirection,
  DmlOperation,
  SelectCondition,
  WhereCondition,
  OrderByCondition,
  JoinCondition,
  NameValuePair,
} from "@/types/query-definition";

export interface SqlBuilderOptions {
  type: "Query" | "DML";
}

// ---------- Internal UI State Types ----------
export interface FunctionArg {
  type: "field" | "constant";
  table: string;
  field: string;
  constant: string;
}

export interface ColumnItem {
  type: "field" | "constant" | "function";
  table: string;
  field: string;
  alias: string;
  constant: string;
  functionName: string;
  isDistinct: boolean;
  arguments: FunctionArg[];
}

export interface WhereItem {
  type: "basic" | "column_compare";
  table: string;
  field: string;
  operator: string;
  value: string;
  isOr: boolean;
  isNot: boolean;
  isDate: boolean;
  leftTable: string;
  leftField: string;
  rightTable: string;
  rightField: string;
  values: string;
}

export interface OnConditionItem {
  leftTable: string;
  leftField: string;
  operator: string;
  rightTable: string;
  rightField: string;
}

export interface JoinItem {
  table: string;
  alias: string;
  type: string;
  onConditions: OnConditionItem[];
}

export interface OrderByItem {
  type: "field" | "function";
  table: string;
  field: string;
  direction: string;
  functionName: string;
  arguments: FunctionArg[];
  isDistinct: boolean;
}

export function useSqlBuilder(options: SqlBuilderOptions) {
  // --- DB / Metadata State ---
  const dbId = ref<number | null>(null);
  const schema = ref("_default_");
  const table = ref("");
  const alias = ref("");

  const availableSchemas = ref<string[]>([]);
  const availableTables = ref<string[]>([]);
  const availableColumns = ref<{ name: string; dataType: string }[]>([]);

  const allTableAliasMap = computed(() => {
    const map: Record<string, string> = {};
    if (table.value) {
      map[table.value] = alias.value;
    }
    joins.value.forEach((j) => {
      if (j.table) {
        map[j.table] = j.alias;
      }
    });
    return map;
  });

  // --- Query Builder State ---
  const distinct = ref(false);
  const selectColumns = ref<ColumnItem[]>([]);
  const whereConditions = ref<WhereItem[]>([]);
  const joins = ref<JoinItem[]>([]);
  const orderBys = ref<OrderByItem[]>([]);
  const limit = ref<number | null>(100);
  const offset = ref<number | null>(0);

  // --- DML Builder State ---
  const dmlOperation = ref("insert");
  const insertValues = ref<{ fieldName: string; value: string }[]>([]);

  // --- Logic Helpers ---
  const addColumn = () =>
    selectColumns.value.push({
      type: "field",
      table: "",
      field: "",
      alias: "",
      constant: "",
      functionName: "",
      isDistinct: false,
      arguments: [],
    });
  const removeColumn = (i: number) => selectColumns.value.splice(i, 1);

  const addWhere = () =>
    whereConditions.value.push({
      type: "basic",
      field: "",
      operator: "=",
      value: "",
      isOr: false,
      isNot: false,
      isDate: false,
      table: "",
      leftTable: "",
      leftField: "",
      rightTable: "",
      rightField: "",
      values: "",
    });
  const removeWhere = (i: number) => whereConditions.value.splice(i, 1);

  const addJoin = () =>
    joins.value.push({
      table: "",
      alias: "",
      type: "Inner",
      onConditions: [
        {
          leftTable: "",
          leftField: "",
          operator: "=",
          rightTable: "",
          rightField: "",
        },
      ],
    });
  const removeJoin = (i: number) => joins.value.splice(i, 1);
  const addJoinOnCondition = (joinIdx: number) =>
    joins.value[joinIdx]!.onConditions.push({
      leftTable: "",
      leftField: "",
      operator: "=",
      rightTable: "",
      rightField: "",
    });
  const removeJoinOnCondition = (joinIdx: number, condIdx: number) =>
    joins.value[joinIdx]!.onConditions.splice(condIdx, 1);

  const addOrderBy = () =>
    orderBys.value.push({
      type: "field",
      table: "",
      field: "",
      direction: "asc",
      functionName: "",
      arguments: [],
      isDistinct: false,
    });
  const removeOrderBy = (i: number) => orderBys.value.splice(i, 1);
  const addColumnArg = (colIdx: number) =>
    selectColumns.value[colIdx]!.arguments.push({
      type: "field",
      table: "",
      field: "",
      constant: "",
    });
  const removeColumnArg = (colIdx: number, argIdx: number) =>
    selectColumns.value[colIdx]!.arguments.splice(argIdx, 1);
  const addOrderByArg = (obIdx: number) =>
    orderBys.value[obIdx]!.arguments.push({
      type: "field",
      table: "",
      field: "",
      constant: "",
    });
  const removeOrderByArg = (obIdx: number, argIdx: number) =>
    orderBys.value[obIdx]!.arguments.splice(argIdx, 1);

  const addInsertValue = () =>
    insertValues.value.push({ fieldName: "", value: "" });
  const removeInsertValue = (i: number) => insertValues.value.splice(i, 1);

  // --- Fetching Logic ---
  const onDbChange = async () => {
    schema.value = "_default_";
    table.value = "";
    availableSchemas.value = [];
    availableTables.value = [];
    availableColumns.value = [];

    if (!dbId.value) return;
    try {
      const schemas = await getSchemas(dbId.value);
      availableSchemas.value = schemas.filter((s) => s);
    } catch (e) {}

    try {
      availableTables.value = await getTables(dbId.value, "");
    } catch (e2) {}
  };

  const onSchemaChange = async () => {
    table.value = "";
    availableTables.value = [];
    availableColumns.value = [];

    if (!dbId.value) return;
    const s = schema.value === "_default_" ? "" : schema.value;
    try {
      availableTables.value = await getTables(dbId.value, s);
    } catch (e) {}
  };

  const onTableChange = async () => {
    availableColumns.value = [];
    if (!dbId.value || !table.value) return;
    const s = schema.value === "_default_" ? "" : schema.value;
    try {
      const rawColumns = await getColumns(dbId.value, table.value, s);
      availableColumns.value = rawColumns.map((c: any) => ({
        name: c.column,
        dataType: c.type,
      }));
    } catch (e) {}
  };

  const joinTableColumns = ref<Record<string, string[]>>({});
  const fetchJoinColumns = async (index: number) => {
    const join = joins.value[index];
    if (!join || !join.table || !dbId.value) return;

    const fullTable = join.table.split(" ")[0];
    if (!fullTable || joinTableColumns.value[fullTable]) return;

    let s = "";
    let t = fullTable;
    if (fullTable.includes(".")) {
      const parts = fullTable.split(".");
      s = parts[0] || "";
      t = parts[1] || "";
    } else {
      s = schema.value === "_default_" ? "" : schema.value;
    }

    try {
      const rawColumns = await getColumns(dbId.value, t, s);
      joinTableColumns.value[fullTable] = rawColumns.map((c: any) => c.column);
    } catch (e) {}
  };

  // --- Column / Alias Logic ---
  const filterColumnOptionsByTable = (t: string) => {
    if (t === table.value) {
      return mainTableColumnNames.value;
    }
    return joinTableColumns.value[t] || [];
  };

  const mainTableColumnNames = computed(() => {
    return availableColumns.value.map((c) => c.name);
  });

  const allAvailableColumnNames = computed(() => {
    const all = [...mainTableColumnNames.value];
    joins.value.forEach((j) =>
      all.push(...(joinTableColumns.value[j.table] || [])),
    );
    return all;
  });

  const qualifiedAvailableTables = computed(() => {
    return availableTables.value;
  });

  const nowValidTables = computed(() => {
    var mainAndJoinTables = [table.value, ...joins.value.map((j) => j.table)];
    return availableTables.value.filter((t) => mainAndJoinTables.includes(t));
  });

  // --- Autofill ---
  const autofillColumns = () => {
    if (options.type === "Query") {
      selectColumns.value = mainTableColumnNames.value.map((name) => ({
        type: "field" as const,
        table: table.value,
        field: name,
        alias: "",
        constant: "",
        functionName: "",
        isDistinct: false,
        arguments: [],
      }));
    } else {
      insertValues.value = availableColumns.value.map((c) => ({
        fieldName: c.name,
        value: `{{${c.name}}}`,
      }));
    }
  };

  // --- Helpers ---
  const getQualifiedTableName = (tbl: string) => {
    const s = schema.value === "_default_" ? "" : schema.value;
    return s && tbl && !tbl.startsWith(`${s}.`) ? `${s}.${tbl}` : tbl;
  };

  const q = (tbl: string, rawField: string) =>
    rawField ? `${allTableAliasMap.value[tbl] || tbl}.${rawField}` : "";

  const capitalize = (s: string) => s.charAt(0).toUpperCase() + s.slice(1);

  // --- Build polymorphic objects ---
  function buildSelectColumns(): SelectCondition[] {
    return selectColumns.value.map((c) => {
      const aliasVal = c.alias || undefined;
      switch (c.type) {
        case "field":
          return {
            type: "field",
            fieldName: q(c.table, c.field),
            alias: aliasVal,
          } as SelectCondition;
        case "constant":
          return {
            type: "constant",
            constant: c.constant,
            alias: aliasVal,
          } as SelectCondition;
        case "function": {
          const fnArgs = c.arguments.map((a) =>
            a.type === "field"
              ? { type: "field" as const, fieldName: q(a.table, a.field) }
              : {
                  type: "constant" as const,
                  constant: a.constant || undefined,
                },
          );
          return {
            type: "function",
            functionName: c.functionName,
            arguments: fnArgs.length ? fnArgs : undefined,
            isDistinct: c.isDistinct || undefined,
            alias: aliasVal,
          } as SelectCondition;
        }
      }
    });
  }

  function buildWhereConditions(source?: WhereItem[]): WhereCondition[] {
    const items = source ?? whereConditions.value;
    return items.map((w) => {
      switch (w.type) {
        case "basic": {
          const cond: WhereCondition = {
            type: "basic",
            fieldName: q(w.table, w.field),
            operator: w.operator,
            value: w.value || undefined,
          };
          if (w.isOr) (cond as any).isOr = true;
          if (w.isNot) (cond as any).isNot = true;
          if (w.isDate) (cond as any).isDate = true;
          return cond;
        }
        case "column_compare": {
          const cond: WhereCondition = {
            type: "column_compare",
            leftFieldName: q(w.leftTable, w.leftField),
            operator: w.operator,
            rightFieldName: q(w.rightTable, w.rightField),
          };
          if (w.isOr) (cond as any).isOr = true;
          if (w.isNot) (cond as any).isNot = true;
          return cond;
        }
        case "in": {
          const cond: WhereCondition = {
            type: "basic",
            fieldName: q(w.table, w.field),
            operator: "IN",
            values: w.values
              ? w.values
                  .split(",")
                  .map((v) => v.trim())
                  .filter(Boolean)
              : [],
          };
          if (w.isOr) (cond as any).isOr = true;
          if (w.isNot) (cond as any).isNot = true;
          if (w.isDate) (cond as any).isDate = true;
          return cond;
        }
      }
    });
  }

  function buildOrderBys(): OrderByCondition[] {
    return orderBys.value.map((o) => {
      const dir = capitalize(o.direction) as SortDirection;
      switch (o.type) {
        case "field":
          return {
            type: "field",
            fieldName: q(o.table, o.field),
            direction: dir,
          } as OrderByCondition;
        case "function": {
          const fnArgs = o.arguments.map((a) =>
            a.type === "field"
              ? { type: "field" as const, fieldName: q(a.table, a.field) }
              : {
                  type: "constant" as const,
                  constant: a.constant || undefined,
                },
          );
          return {
            type: "function",
            functionName: o.functionName,
            arguments: fnArgs.length ? fnArgs : undefined,
            isDistinct: o.isDistinct || undefined,
            direction: dir,
          } as OrderByCondition;
        }
      }
    });
  }

  function buildJoins(): JoinCondition[] {
    return joins.value.map((j) => ({
      table: getQualifiedTableName(j.table),
      alias: j.alias || undefined,
      type: capitalize(j.type) as any,
      onConditions: j.onConditions.map((oc) => ({
        type: "column_compare" as const,
        leftFieldName: q(oc.leftTable, oc.leftField),
        operator: oc.operator,
        rightFieldName: q(oc.rightTable, oc.rightField),
      })),
    }));
  }

  // --- Final Output ---
  const generateJson = () => {
    if (options.type === "Query") {
      const output: Record<string, any> = {
        tableName: getQualifiedTableName(table.value),
        alias: alias.value || undefined,
        distinct: distinct.value,
      };

      const cols = buildSelectColumns();
      output.selectColumns =
        cols.length > 0 ? cols : [{ type: "field", fieldName: "*" }];

      const wheres = buildWhereConditions();
      if (wheres.length > 0) output.whereColumnsAndValues = wheres;
      const orders = buildOrderBys();
      if (orders.length > 0) output.orderByColumns = orders;

      const j = buildJoins();
      if (j.length > 0) output.joins = j;

      if (limit.value != null) output.limit = limit.value;
      if (offset.value != null) output.offset = offset.value;

      return JSON.stringify(output, null, 2);
    } else {
      const output: Record<string, any> = {
        operation: capitalize(dmlOperation.value) as DmlOperation,
        tableName: getQualifiedTableName(table.value),
      };

      if (insertValues.value.length > 0) {
        output.values = insertValues.value.map(
          (v) =>
            ({
              fieldName: v.fieldName,
              value: v.value || undefined,
            }) as NameValuePair,
        );
      }

      const wheres = buildWhereConditions();
      if (wheres.length > 0) output.whereConditions = wheres;

      return JSON.stringify(output, null, 2);
    }
  };

  return {
    // State
    dbId,
    schema,
    table,
    alias,
    availableSchemas,
    availableTables,
    availableColumns,
    distinct,
    selectColumns,
    whereConditions,
    joins,
    orderBys,
    limit,
    offset,
    dmlOperation,
    insertValues,
    joinTableColumns,

    // Computed
    mainTableColumnNames,
    allAvailableColumnNames,
    qualifiedAvailableTables,
    nowValidTables,

    // Actions
    onDbChange,
    onSchemaChange,
    onTableChange,
    fetchJoinColumns,
    addColumn,
    removeColumn,
    addWhere,
    removeWhere,
    addJoin,
    removeJoin,
    addJoinOnCondition,
    removeJoinOnCondition,
    addOrderBy,
    removeOrderBy,
    addColumnArg,
    removeColumnArg,
    addOrderByArg,
    removeOrderByArg,
    addInsertValue,
    removeInsertValue,
    autofillColumns,
    filterColumnOptionsByTable,
    generateJson,
  };
}
