<script setup lang="ts">
import { ref, watch, computed } from "vue";
import {
  Dialog,
  DialogScrollContent,
  DialogHeader,
  DialogTitle,
  DialogDescription,
  DialogFooter,
} from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Field, FieldLabel } from "@/components/ui/field";
import type { DbManagement } from "@/api/db-management";
import { useSqlBuilder } from "@/composables/useSqlBuilder";

// Sub-components
import SqlBuilderTableGrid from "./sql-builder/SqlBuilderTableGrid.vue";
import SqlBuilderTabColumns from "./sql-builder/SqlBuilderTabColumns.vue";
import SqlBuilderTabWhere from "./sql-builder/SqlBuilderTabWhere.vue";
import SqlBuilderTabJoins from "./sql-builder/SqlBuilderTabJoins.vue";
import SqlBuilderTabOrder from "./sql-builder/SqlBuilderTabOrder.vue";
import SqlBuilderTabValues from "./sql-builder/SqlBuilderTabValues.vue";

// --- Props & Emits ---
const props = defineProps<{
  open: boolean;
  type: "Query" | "DML";
  dbs: DbManagement[];
}>();

const emit = defineEmits<{
  "update:open": [value: boolean];
  apply: [json: string];
}>();

// --- Initialization ---
const {
  dbId,
  schema,
  table,
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
  mainTableColumnNames,
  allAvailableColumnNames,
  qualifiedAvailableTables,
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
} = useSqlBuilder({ type: props.type });

// --- UI Only State ---
const dialogOpen = computed({
  get: () => props.open,
  set: (val) => emit("update:open", val),
});

const activeTab = ref("columns");
const tabs = computed(() => {
  if (props.type === "Query") {
    return [
      { id: "columns", label: "Columns" },
      { id: "where", label: "Where" },
      { id: "joins", label: "Joins" },
      { id: "order", label: "Order & Limit" },
    ];
  } else {
    return [
      { id: "values", label: "Values" },
      { id: "where", label: "Where" },
    ];
  }
});

// Reset tab when type changes
watch(
  () => props.type,
  (newVal) => {
    activeTab.value = newVal === "Query" ? "columns" : "values";
  },
);

const apply = () => {
  emit("apply", generateJson());
  dialogOpen.value = false;
};
</script>

<template>
  <Dialog v-model:open="dialogOpen">
    <DialogScrollContent class="sm:max-w-4xl h-[85vh] flex flex-col">
      <DialogHeader>
        <DialogTitle>Advanced JSON Builder - {{ type }}</DialogTitle>
        <DialogDescription>
          Construct your SQL tool definition visually. Complex features like
          subqueries must be added manually in the raw JSON after generation.
        </DialogDescription>
      </DialogHeader>

      <div class="flex-1 overflow-y-auto pr-4 py-4 space-y-6">
        <!-- Table Selection -->
        <SqlBuilderTableGrid
          v-model:db-id="dbId"
          v-model:schema="schema"
          v-model:table="table"
          :dbs="dbs"
          :available-schemas="availableSchemas"
          :available-tables="availableTables"
          @db-change="onDbChange"
          @schema-change="onSchemaChange"
          @table-change="onTableChange"
        />

        <div v-if="type === 'DML'" class="border p-4 rounded-lg bg-muted/10">
          <Field class="max-w-xs">
            <FieldLabel class="text-xs">DML Operation</FieldLabel>
            <Select v-model="dmlOperation">
              <SelectTrigger class="h-8"><SelectValue /></SelectTrigger>
              <SelectContent>
                <SelectItem value="insert">INSERT</SelectItem>
                <SelectItem value="update">UPDATE</SelectItem>
                <SelectItem value="delete">DELETE</SelectItem>
              </SelectContent>
            </Select>
          </Field>
        </div>

        <!-- Navigation Tabs -->
        <div class="flex gap-2 border-b pb-2">
          <Button
            v-for="tab in tabs"
            :key="tab.id"
            variant="ghost"
            size="sm"
            class="text-xs font-semibold rounded-none border-b-2 transition-colors"
            :class="
              activeTab === tab.id
                ? 'border-primary text-primary'
                : 'border-transparent text-muted-foreground'
            "
            @click="activeTab = tab.id"
          >
            {{ tab.label }}
          </Button>
        </div>

        <!-- Tab Content -->
        <div class="min-h-[200px]">
          <!-- Columns Tab -->
          <SqlBuilderTabColumns
            v-if="activeTab === 'columns'"
            v-model:distinct="distinct"
            :select-columns="selectColumns"
            :options="allAvailableColumnNames"
            :can-autofill="!!availableColumns.length"
            @add="addColumn"
            @remove="removeColumn"
            @autofill="autofillColumns"
          />

          <!-- Where Tab -->
          <SqlBuilderTabWhere
            v-if="activeTab === 'where'"
            :where-conditions="whereConditions"
            :options="allAvailableColumnNames"
            :type="type"
            @add="addWhere"
            @remove="removeWhere"
          />

          <!-- Joins Tab -->
          <SqlBuilderTabJoins
            v-if="activeTab === 'joins'"
            :joins="joins"
            :qualified-tables="qualifiedAvailableTables"
            :main-column-options="mainTableColumnNames"
            :get-join-column-options="joinColumnOptions"
            @add="addJoin"
            @remove="removeJoin"
            @fetch-columns="fetchJoinColumns"
          />

          <!-- Order & Limit Tab -->
          <SqlBuilderTabOrder
            v-if="activeTab === 'order'"
            v-model:limit="limit"
            v-model:offset="offset"
            :order-bys="orderBys"
            :options="allAvailableColumnNames"
            @add="addOrderBy"
            @remove="removeOrderBy"
          />

          <!-- DML Values Tab -->
          <SqlBuilderTabValues
            v-if="activeTab === 'values'"
            :insert-values="insertValues"
            :options="mainTableColumnNames"
            :can-autofill="!!availableColumns.length"
            @add="addInsertValue"
            @remove="removeInsertValue"
            @autofill="autofillColumns"
          />
        </div>
      </div>

      <DialogFooter class="border-t pt-4 mt-auto">
        <Button variant="outline" @click="dialogOpen = false">Cancel</Button>
        <Button @click="apply" :disabled="!table">Generate JSON</Button>
      </DialogFooter>
    </DialogScrollContent>
  </Dialog>
</template>
