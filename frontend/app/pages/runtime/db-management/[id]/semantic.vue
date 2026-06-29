<script setup lang="ts">
import { onMounted, ref, watch } from "vue";
import { useRoute, useRouter } from "vue-router";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { Input } from "@/components/ui/input";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import {
  getDbManagement,
  getSchemas,
  getTables,
  getColumns,
  type DbManagement,
} from "@/api/db-management";
import {
  getSemanticsByDbId,
  upsertSemantic,
  deleteSemantic,
  type DbSemantic,
} from "@/api/db-semantic";
import { toast } from "vue-sonner"
import { ChevronLeft, Save, Loader2, Database } from "@lucide/vue";

definePageMeta({
  layout: "default",
  permission: "/runtime/db-management/semantic.view",
});

const route = useRoute();
const router = useRouter();
const dbId = parseInt(route.params.id as string);

const db = ref<DbManagement | null>(null);
const schemas = ref<string[]>([]);
const tables = ref<string[]>([]);
const columns = ref<{ column: string; type: string }[]>([]);
const semantics = ref<DbSemantic[]>([]);

const selectedSchema = ref("");
const selectedTable = ref("");
const loading = ref(false);
const saving = ref(false);

// For editing
const tableDescription = ref("");
const tableDisplayName = ref("");
const columnAnnotations = ref<
  Record<string, { description: string; displayName: string }>
>({});

const loadInitialData = async () => {
  loading.value = true;
  try {
    const [dbData, schemasData, semanticsData] = await Promise.all([
      getDbManagement(dbId),
      getSchemas(dbId),
      getSemanticsByDbId(dbId),
    ]);
    db.value = dbData;
    schemas.value = schemasData;
    semantics.value = semanticsData;

    if (schemas.value.length > 0) {
      selectedSchema.value = schemas.value[0] ?? "";
    }
  } catch (e: any) {
    toast.error(e?.response?.data || "Failed to load database info.");
  } finally {
    loading.value = false;
  }
};

const loadTables = async () => {
  if (!selectedSchema.value) return;
  tables.value = await getTables(dbId, selectedSchema.value);
  if (tables.value.length > 0) {
    selectedTable.value = tables.value[0] ?? "";
  } else {
    selectedTable.value = "";
  }
};

const loadColumns = async () => {
  if (!selectedTable.value) {
    columns.value = [];
    return;
  }
  const columnsData = await getColumns(
    dbId,
    selectedTable.value,
    selectedSchema.value,
  );

  // Sync with semantics
  const tableSemantic = semantics.value.find(
    (s) =>
      s.schemaName === selectedSchema.value &&
      s.tableName === selectedTable.value &&
      !s.columnName,
  );
  tableDescription.value = tableSemantic?.description || "";
  tableDisplayName.value = tableSemantic?.displayName || "";

  const newAnnotations: Record<
    string,
    { description: string; displayName: string }
  > = {};
  columnsData.forEach((col) => {
    const colSemantic = semantics.value.find(
      (s) =>
        s.schemaName === selectedSchema.value &&
        s.tableName === selectedTable.value &&
        s.columnName === col.column,
    );
    newAnnotations[col.column] = {
      description: colSemantic?.description || "",
      displayName: colSemantic?.displayName || "",
    };
  });

  columnAnnotations.value = newAnnotations;
  columns.value = columnsData;
};

watch(selectedSchema, loadTables);
watch(selectedTable, loadColumns);

onMounted(loadInitialData);

const save = async () => {
  saving.value = true;
  try {
    // Save table semantic
    await upsertSemantic({
      dbManagementId: dbId,
      schemaName: selectedSchema.value,
      tableName: selectedTable.value,
      description: tableDescription.value,
      displayName: tableDisplayName.value,
    });

    // Save column semantics
    for (const [colName, ann] of Object.entries(columnAnnotations.value)) {
      await upsertSemantic({
        dbManagementId: dbId,
        schemaName: selectedSchema.value,
        tableName: selectedTable.value,
        columnName: colName,
        description: ann.description,
        displayName: ann.displayName,
      });
    }

    // Refresh semantics
    semantics.value = await getSemanticsByDbId(dbId);
  } catch (e: any) {
    toast.error(e?.response?.data || "Failed to save semantics.");
  } finally {
    saving.value = false;
  }
};
</script>

<template>
  <div class="space-y-6">
    <div class="flex items-center justify-between">
      <div class="flex items-center gap-4">
        <Button variant="ghost" size="icon" @click="router.back()">
          <ChevronLeft class="size-4" />
        </Button>
        <div>
          <h1 class="text-2xl font-bold tracking-tight">Semantic Layer</h1>
          <p class="text-sm text-muted-foreground">
            Annotate
            <span class="font-mono text-primary">{{ db?.name }}</span> with
            business context.
          </p>
        </div>
      </div>
      <Button :disabled="saving || !selectedTable" @click="save" v-permission="'/runtime/db-management/semantic.edit'">
        <Loader2 v-if="saving" class="mr-2 size-4 animate-spin" />
        <Save v-else class="mr-2 size-4" />
        Save Annotations
      </Button>
    </div>

    <div class="grid gap-6 md:grid-cols-4">
      <Card class="md:col-span-1">
        <CardHeader>
          <CardTitle class="text-sm">Navigation</CardTitle>
          <CardDescription>Select schema and table</CardDescription>
        </CardHeader>
        <CardContent class="space-y-4">
          <div class="space-y-2">
            <label class="text-xs font-medium">Schema</label>
            <Select v-model="selectedSchema">
              <SelectTrigger class="w-full">
                <SelectValue placeholder="Select schema" />
              </SelectTrigger>
              <SelectContent>
                <SelectItem v-for="s in schemas" :key="s" :value="s">
                  {{ s }}
                </SelectItem>
              </SelectContent>
            </Select>
          </div>

          <div class="space-y-2">
            <label class="text-xs font-medium">Table</label>
            <div
              class="max-h-[500px] overflow-y-auto border rounded-md p-1 space-y-1"
            >
              <button
                v-for="t in tables"
                :key="t"
                @click="selectedTable = t"
                class="w-full text-left px-3 py-2 text-sm rounded-sm transition-colors"
                :class="
                  selectedTable === t
                    ? 'bg-primary text-primary-foreground'
                    : 'hover:bg-muted'
                "
              >
                {{ t }}
              </button>
            </div>
          </div>
        </CardContent>
      </Card>

      <div class="md:col-span-3 space-y-6">
        <Card v-if="selectedTable">
          <CardHeader>
            <CardTitle>Table: {{ selectedTable }}</CardTitle>
            <CardDescription
              >Provide a business description for this table.</CardDescription
            >
          </CardHeader>
          <CardContent class="space-y-4">
            <div class="grid gap-4 md:grid-cols-2">
              <div class="space-y-2">
                <label
                  class="text-xs font-medium uppercase tracking-wider text-muted-foreground"
                  >Display Name</label
                >
                <Input
                  v-model="tableDisplayName"
                  placeholder="e.g. Sales Orders"
                />
              </div>
              <div class="space-y-2">
                <label
                  class="text-xs font-medium uppercase tracking-wider text-muted-foreground"
                  >Description</label
                >
                <Input
                  v-model="tableDescription"
                  placeholder="Contains all customer order history including status."
                />
              </div>
            </div>
          </CardContent>
        </Card>

        <Card v-if="selectedTable">
          <CardHeader>
            <CardTitle>Columns</CardTitle>
            <CardDescription
              >Explain what each field means to help the AI understand the
              schema.</CardDescription
            >
          </CardHeader>
          <CardContent class="max-h-[400px] overflow-y-auto">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead class="w-[200px]">Column</TableHead>
                  <TableHead class="w-[100px]">Type</TableHead>
                  <TableHead class="w-[200px]">Display Name</TableHead>
                  <TableHead>Description / Business Logic</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                <TableRow v-for="col in columns" :key="col.column">
                  <TableCell class="font-mono text-xs">{{
                    col.column
                  }}</TableCell>
                  <TableCell>
                    <span
                      class="text-[0.65rem] px-1.5 py-0.5 rounded bg-muted font-mono uppercase"
                    >
                      {{ col.type }}
                    </span>
                  </TableCell>
                  <TableCell>
                    <Input
                      v-if="columnAnnotations[col.column]"
                      v-model="columnAnnotations[col.column]!.displayName"
                      placeholder="Business Term"
                      class="h-8 text-sm"
                    />
                  </TableCell>
                  <TableCell>
                    <Input
                      v-if="columnAnnotations[col.column]"
                      v-model="columnAnnotations[col.column]!.description"
                      placeholder="Explain purpose, units, or constraints..."
                      class="h-8 text-sm"
                    />
                  </TableCell>
                </TableRow>
              </TableBody>
            </Table>
          </CardContent>
        </Card>

        <div
          v-if="!selectedTable"
          class="flex flex-col items-center justify-center h-[400px] border-2 border-dashed rounded-xl bg-muted/20 text-muted-foreground"
        >
          <Database class="size-12 mb-4 opacity-20" />
          <p>Select a table to start annotating your schema.</p>
        </div>
      </div>
    </div>
  </div>
</template>
