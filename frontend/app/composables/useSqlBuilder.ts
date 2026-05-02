import { ref, watch, computed } from "vue";
import { getSchemas, getTables, getColumns } from "@/api/db-management";

export interface SqlBuilderOptions {
  type: "Query" | "DML";
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
    })
    console.log(allTableAliasMap.value);
    return map;
  });
  // --- Query Builder State ---
  const distinct = ref(false);
  const selectColumns = ref<{ table: string; field: string; alias: string }[]>([]);
  const whereConditions = ref<
    {
      field: string;
      operator: string;
      value: string;
      isOr: boolean;
      isNot: boolean;
      table: string;
    }[]
  >([]);
  const joins = ref<
    {
      table: string;
      alias: string;
      type: string;
      firstTable: string;
      first: string;
      operator: string;
      secondTable: string;
      second: string;
    }[]
  >([]);
  const orderBys = ref<{ table: string; field: string; direction: string }[]>([]);
  const limit = ref<number | null>(100);
  const offset = ref<number | null>(0);

  // --- DML Builder State ---
  const dmlOperation = ref("insert");
  const insertValues = ref<{ name: string; value: string }[]>([]);

  // --- Logic Helpers ---
  const addColumn = () => selectColumns.value.push({ table: "", field: "", alias: "" });
  const removeColumn = (i: number) => selectColumns.value.splice(i, 1);
  const addWhere = () =>
    whereConditions.value.push({
      field: "",
      operator: "=",
      value: "",
      isOr: false,
      isNot: false,
      table: "",
    });
  const removeWhere = (i: number) => whereConditions.value.splice(i, 1);
  const addJoin = () =>
    joins.value.push({
      table: "",
      alias: "",
      type: "INNER",
      firstTable: "",
      first: "",
      operator: "=",
      secondTable: "",
      second: "",
    });
  const removeJoin = (i: number) => joins.value.splice(i, 1);
  const addOrderBy = () => orderBys.value.push({ table: "", field: "", direction: "asc" });
  const removeOrderBy = (i: number) => orderBys.value.splice(i, 1);
  const addInsertValue = () => insertValues.value.push({ name: "", value: "" });
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
    } catch (e) { }

    try {
      availableTables.value = await getTables(dbId.value, "");
    } catch (e2) { }
  };

  const onSchemaChange = async () => {
    table.value = "";
    availableTables.value = [];
    availableColumns.value = [];

    if (!dbId.value) return;
    const s = schema.value === "_default_" ? "" : schema.value;
    try {
      availableTables.value = await getTables(dbId.value, s);
    } catch (e) { }
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
    } catch (e) { }
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
      joinTableColumns.value[fullTable] = rawColumns.map(
        (c: any) => c.column,
      );
    } catch (e) { }
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
    joins.value.forEach((j) => all.push(...(joinTableColumns.value[j.table] || [])));
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
        table: table.value,
        field: name,
        alias: "",
      }));
    } else {
      insertValues.value = availableColumns.value.map((c) => ({
        name: c.name,
        value: `{{${c.name}}}`,
      }));
    }
  };

  // --- Final Output ---
  const generateJson = () => {
    const getQualifiedTableName = (tbl: string) => {
      const s = schema.value === "_default_" ? "" : schema.value;
      return s && tbl && !tbl.startsWith(`${s}.`) ? `${s}.${tbl}` : tbl;
    };

    if (options.type === "Query") {
      return JSON.stringify(
        {
          tableName: getQualifiedTableName(table.value),
          alias: alias.value || undefined,
          distinct: distinct.value,
          selectColumns:
            selectColumns.value.length > 0
              ? selectColumns.value.map((c) => ({
                field: `${allTableAliasMap.value[c.table]}.${c.field}`,
                ...(c.alias ? { alias: c.alias } : {}),
              }))
              : [{ field: "*" }],
          whereColumnsAndValues: whereConditions.value.map((w) => ({
            field: `${allTableAliasMap.value[w.table]}.${w.field}`,
            operator: w.operator,
            value: w.value,
            isOr: w.isOr,
            isNot: w.isNot,
          })),
          orderByColumns: orderBys.value.map((o) => ({
            field: `${allTableAliasMap.value[o.table]}.${o.field}`,
            direction: o.direction,
          })),
          groupByConditions: [],
          havingConditions: [],
          joins: joins.value.map((j) => ({
            alias: j.alias || undefined,
            table: getQualifiedTableName(j.table),
            type: j.type,
            first: `${allTableAliasMap.value[j.firstTable]}.${j.first}`,
            operator: j.operator,
            second: `${allTableAliasMap.value[j.secondTable]}.${j.second}`,
          })),
          limit: limit.value,
          offset: offset.value,
        },
        null,
        2,
      );
    } else {
      return JSON.stringify(
        {
          operation: dmlOperation.value,
          tableName: getQualifiedTableName(table.value),
          values: insertValues.value.map((v) => ({
            name: v.name,
            value: v.value,
          })),
          whereConditions: whereConditions.value.map((w) => ({
            field: `${allTableAliasMap.value[w.table]}.${w.field}`,
            operator: w.operator,
            value: w.value,
          })),
        },
        null,
        2,
      );
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
    addOrderBy,
    removeOrderBy,
    addInsertValue,
    removeInsertValue,
    autofillColumns,
    filterColumnOptionsByTable,
    generateJson,
  };
}
