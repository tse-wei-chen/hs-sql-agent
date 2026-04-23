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
  const mainAlias = ref("");

  const availableSchemas = ref<string[]>([]);
  const availableTables = ref<string[]>([]);
  const availableColumns = ref<{ name: string; dataType: string }[]>([]);

  // --- Query Builder State ---
  const distinct = ref(false);
  const selectColumns = ref<{ field: string; alias: string }[]>([]);
  const whereConditions = ref<
    {
      field: string;
      operator: string;
      value: string;
      isOr: boolean;
      isNot: boolean;
    }[]
  >([]);
  const joins = ref<
    {
      table: string;
      alias: string;
      type: string;
      first: string;
      operator: string;
      second: string;
    }[]
  >([]);
  const orderBys = ref<{ field: string; direction: string }[]>([]);
  const limit = ref<number | null>(100);
  const offset = ref<number | null>(0);

  // --- DML Builder State ---
  const dmlOperation = ref("insert");
  const insertValues = ref<{ name: string; value: string }[]>([]);

  // --- Logic Helpers ---
  const addColumn = () => selectColumns.value.push({ field: "", alias: "" });
  const removeColumn = (i: number) => selectColumns.value.splice(i, 1);
  const addWhere = () =>
    whereConditions.value.push({
      field: "",
      operator: "=",
      value: "",
      isOr: false,
      isNot: false,
    });
  const removeWhere = (i: number) => whereConditions.value.splice(i, 1);
  const addJoin = () =>
    joins.value.push({
      table: "",
      alias: "",
      type: "INNER",
      first: "",
      operator: "=",
      second: "",
    });
  const removeJoin = (i: number) => joins.value.splice(i, 1);
  const addOrderBy = () => orderBys.value.push({ field: "", direction: "asc" });
  const removeOrderBy = (i: number) => orderBys.value.splice(i, 1);
  const addInsertValue = () => insertValues.value.push({ name: "", value: "" });
  const removeInsertValue = (i: number) => insertValues.value.splice(i, 1);

  // --- Fetching Logic ---
  const onDbChange = async () => {
    schema.value = "_default_";
    table.value = "";
    mainAlias.value = "";
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
        name: c.column || c.Column || c.name || c.Name,
        dataType: c.type || c.Type || c.dataType || c.DataType,
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
        (c: any) => c.column || c.Column || c.name || c.Name,
      );
    } catch (e) { }
  };

  // --- Column / Alias Logic ---
  const joinColumnOptions = (join: any) => {
    const fullTable = join.table.split(" ")[0];
    const cols = joinTableColumns.value[fullTable] || [];
    let prefix = join.alias;
    if (!prefix && join.table) {
      const parts = join.table.split(".");
      prefix = parts[parts.length - 1];
    }
    return prefix ? cols.map((c) => `${prefix}.${c}`) : cols;
  };

  const mainTableColumnNames = computed(() => {
    const cols = availableColumns.value.map((c) => c.name);
    let prefix = mainAlias.value;
    if (!prefix && table.value) {
      const parts = table.value.split(".");
      prefix = parts[parts.length - 1] || "";
    }
    return prefix ? cols.map((c) => `${prefix}.${c}`) : cols;
  });

  const allAvailableColumnNames = computed(() => {
    const all = [...mainTableColumnNames.value];
    joins.value.forEach((j) => all.push(...joinColumnOptions(j)));
    return all;
  });

  const qualifiedAvailableTables = computed(() => {
    const s = schema.value === "_default_" ? "" : schema.value;
    return s
      ? availableTables.value.map((t) =>
        t.startsWith(`${s}.`) ? t : `${s}.${t}`,
      )
      : availableTables.value;
  });

  // --- Prefix Update Reactivity ---
  const updateAllFieldsPrefix = (oldPrefix: string, newPrefix: string) => {
    if (!oldPrefix || !newPrefix || oldPrefix === newPrefix) return;
    const oldDot = oldPrefix + ".";
    const newDot = newPrefix + ".";
    const replace = (s: string) =>
      s.startsWith(oldDot) ? newDot + s.substring(oldDot.length) : s;

    selectColumns.value.forEach((c) => (c.field = replace(c.field)));
    whereConditions.value.forEach((w) => (w.field = replace(w.field)));
    orderBys.value.forEach((o) => (o.field = replace(o.field)));
    joins.value.forEach((j) => {
      j.first = replace(j.first);
      j.second = replace(j.second);
    });
  };

  watch(mainAlias, (newVal, oldVal) => {
    const oldPrefix = oldVal || table.value;
    const newPrefix = newVal || table.value;
    updateAllFieldsPrefix(oldPrefix, newPrefix);
  });

  const prevJoins = ref<{ table: string; alias: string }[]>([]);
  watch(
    joins,
    (newVal) => {
      newVal.forEach((j, i) => {
        const prev = prevJoins.value[i];
        if (prev) {
          const oldPrefix = prev.alias || prev.table.split(" ")[0];
          const newPrefix = j.alias || j.table.split(" ")[0];
          if (
            oldPrefix &&
            newPrefix &&
            oldPrefix !== newPrefix &&
            oldPrefix !== (mainAlias.value || table.value)
          ) {
            updateAllFieldsPrefix(oldPrefix, newPrefix);
          }
        }
      });
      prevJoins.value = newVal.map((j) => ({ table: j.table, alias: j.alias }));
    },
    { deep: true },
  );

  // --- Autofill ---
  const autofillColumns = () => {
    if (options.type === "Query") {
      selectColumns.value = mainTableColumnNames.value.map((name) => ({
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

    const targetTableName = getQualifiedTableName(table.value);

    if (options.type === "Query") {
      return JSON.stringify(
        {
          tableName: targetTableName,
          alias: mainAlias.value,
          distinct: distinct.value,
          selectColumns:
            selectColumns.value.length > 0
              ? selectColumns.value.map((c) => ({
                field: c.field,
                ...(c.alias ? { alias: c.alias } : {}),
              }))
              : [{ field: "*" }],
          whereColumnsAndValues: whereConditions.value.map((w) => ({
            field: w.field,
            operator: w.operator,
            value: w.value,
            isOr: w.isOr,
            isNot: w.isNot,
          })),
          orderByColumns: orderBys.value.map((o) => ({
            field: o.field,
            direction: o.direction,
          })),
          groupByConditions: [],
          havingConditions: [],
          joins: joins.value.map((j) => ({
            table: j.table,
            alias: j.alias,
            type: j.type,
            first: j.first,
            operator: j.operator,
            second: j.second,
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
          tableName: targetTableName,
          values: insertValues.value.map((v) => ({
            name: v.name,
            value: v.value,
          })),
          whereConditions: whereConditions.value.map((w) => ({
            field: w.field,
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
    mainAlias,
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
    joinColumnOptions,
    generateJson,
  };
}
