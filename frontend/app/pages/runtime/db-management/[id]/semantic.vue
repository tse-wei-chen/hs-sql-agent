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
  getSemanticModel,
  upsertSemantic,
  upsertSemanticRelationship,
  deleteSemanticRelationship,
  upsertSemanticMetric,
  deleteSemanticMetric,
  type DbSemantic,
  type DbSemanticRelationship,
  type DbSemanticMetric,
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
const relationships = ref<DbSemanticRelationship[]>([]);
const metrics = ref<DbSemanticMetric[]>([]);

// For editing
const tableDescription = ref("");
const tableDisplayName = ref("");
const tableSynonyms = ref("");
const columnAnnotations = ref<
  Record<string, { description: string; displayName: string; synonyms: string }>
>({});

const splitSynonyms = (value: string) => value
  .split(",")
  .map((item) => item.trim())
  .filter(Boolean);

type RelationshipDraft = Omit<DbSemanticRelationship, "sourceSchema" | "targetSchema" | "description"> & {
  sourceSchema: string; targetSchema: string; description: string;
};
type MetricDraft = Omit<DbSemanticMetric, "displayName" | "description" | "grain" | "filter"> & {
  displayName: string; description: string; grain: string; filter: string;
};

const relationshipDraft = ref<RelationshipDraft>({
  id: 0, dbManagementId: dbId, name: "", sourceSchema: "", sourceTable: "",
  sourceColumn: "", targetSchema: "", targetTable: "", targetColumn: "",
  cardinality: "many-to-one", direction: "source-to-target", description: "",
});
const metricDraft = ref<MetricDraft>({
  id: 0, dbManagementId: dbId, schemaName: "", tableName: "", name: "", displayName: "", description: "",
  formula: "", aggregation: "custom", grain: "", filter: "", synonyms: [], executable: false,
});
const metricSynonyms = ref("");

const loadInitialData = async () => {
  loading.value = true;
  try {
    const [dbData, schemasData, semanticModel] = await Promise.all([
      getDbManagement(dbId),
      getSchemas(dbId),
      getSemanticModel(dbId),
    ]);
    db.value = dbData;
    schemas.value = schemasData;
    semantics.value = semanticModel.entities;
    relationships.value = semanticModel.relationships;
    metrics.value = semanticModel.metrics;

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
  tableSynonyms.value = tableSemantic?.synonyms.join(", ") || "";

  const newAnnotations: Record<
    string,
    { description: string; displayName: string; synonyms: string }
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
      synonyms: colSemantic?.synonyms.join(", ") || "",
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
      synonyms: splitSynonyms(tableSynonyms.value),
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
        synonyms: splitSynonyms(ann.synonyms),
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

const refreshSemanticModel = async () => {
  const model = await getSemanticModel(dbId);
  semantics.value = model.entities;
  relationships.value = model.relationships;
  metrics.value = model.metrics;
};

const saveRelationship = async () => {
  try {
    await upsertSemanticRelationship(relationshipDraft.value);
    relationshipDraft.value = {
      id: 0, dbManagementId: dbId, name: "", sourceSchema: "", sourceTable: "",
      sourceColumn: "", targetSchema: "", targetTable: "", targetColumn: "",
      cardinality: "many-to-one", direction: "source-to-target", description: "",
    };
    await refreshSemanticModel();
  } catch (e: any) {
    toast.error(e?.response?.data || "Failed to save relationship.");
  }
};

const removeRelationship = async (id: number) => {
  await deleteSemanticRelationship(id);
  await refreshSemanticModel();
};

const saveMetric = async () => {
  try {
    await upsertSemanticMetric({
      ...metricDraft.value,
      schemaName: selectedSchema.value,
      tableName: selectedTable.value,
      synonyms: splitSynonyms(metricSynonyms.value),
    });
    metricDraft.value = {
      id: 0, dbManagementId: dbId, schemaName: "", tableName: "", name: "", displayName: "", description: "",
      formula: "", aggregation: "custom", grain: "", filter: "", synonyms: [], executable: false,
    };
    metricSynonyms.value = "";
    await refreshSemanticModel();
  } catch (e: any) {
    toast.error(e?.response?.data || "Failed to save metric metadata.");
  }
};

const removeMetric = async (id: number) => {
  await deleteSemanticMetric(id);
  await refreshSemanticModel();
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
            <div class="max-h-[500px] overflow-y-auto border rounded-md p-1 space-y-1">
              <button v-for="t in tables" :key="t" @click="selectedTable = t"
                class="w-full text-left px-3 py-2 text-sm rounded-sm transition-colors" :class="selectedTable === t
                    ? 'bg-primary text-primary-foreground'
                    : 'hover:bg-muted'
                  ">
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
            <CardDescription>Provide a business description for this table.</CardDescription>
          </CardHeader>
          <CardContent class="space-y-4">
            <div class="grid gap-4 md:grid-cols-3">
              <div class="space-y-2">
                <label class="text-xs font-medium uppercase tracking-wider text-muted-foreground">Display Name</label>
                <Input v-model="tableDisplayName" placeholder="e.g. Sales Orders" />
              </div>
              <div class="space-y-2">
                <label class="text-xs font-medium uppercase tracking-wider text-muted-foreground">Synonyms</label>
                <Input v-model="tableSynonyms" placeholder="orders, purchases" />
              </div>
              <div class="space-y-2">
                <label class="text-xs font-medium uppercase tracking-wider text-muted-foreground">Description</label>
                <Input v-model="tableDescription" placeholder="Contains all customer order history including status." />
              </div>
            </div>
          </CardContent>
        </Card>

        <Card v-if="selectedTable">
          <CardHeader>
            <CardTitle>Columns</CardTitle>
            <CardDescription>Explain what each field means to help the AI understand the
              schema.</CardDescription>
          </CardHeader>
          <CardContent class="max-h-[400px] overflow-y-auto">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead class="w-[200px]">Column</TableHead>
                  <TableHead class="w-[100px]">Type</TableHead>
                  <TableHead class="w-[200px]">Display Name</TableHead>
                  <TableHead class="w-[180px]">Synonyms</TableHead>
                  <TableHead>Description / Business Logic</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                <TableRow v-for="col in columns" :key="col.column">
                  <TableCell class="font-mono text-xs">{{
                    col.column
                  }}</TableCell>
                  <TableCell>
                    <span class="text-[0.65rem] px-1.5 py-0.5 rounded bg-muted font-mono uppercase">
                      {{ col.type }}
                    </span>
                  </TableCell>
                  <TableCell>
                    <Input v-if="columnAnnotations[col.column]" v-model="columnAnnotations[col.column]!.displayName"
                      placeholder="Business Term" class="h-8 text-sm" />
                  </TableCell>
                  <TableCell>
                    <Input v-if="columnAnnotations[col.column]" v-model="columnAnnotations[col.column]!.synonyms"
                      placeholder="comma-separated" class="h-8 text-sm" />
                  </TableCell>
                  <TableCell>
                    <Input v-if="columnAnnotations[col.column]" v-model="columnAnnotations[col.column]!.description"
                      placeholder="Explain purpose, units, or constraints..." class="h-8 text-sm" />
                  </TableCell>
                </TableRow>
              </TableBody>
            </Table>
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>Relationships</CardTitle>
            <CardDescription>Describe join keys, direction, and cardinality for agent discovery.</CardDescription>
          </CardHeader>
          <CardContent class="space-y-4">
            <div class="grid gap-3 md:grid-cols-4">
              <Input v-model="relationshipDraft.name" placeholder="Relationship name" />
              <Input v-model="relationshipDraft.sourceTable" placeholder="Source table" />
              <Input v-model="relationshipDraft.sourceColumn" placeholder="Source column" />
              <Input v-model="relationshipDraft.targetTable" placeholder="Target table" />
              <Input v-model="relationshipDraft.targetColumn" placeholder="Target column" />
              <Select v-model="relationshipDraft.cardinality">
                <SelectTrigger>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="one-to-one">one-to-one</SelectItem>
                  <SelectItem value="one-to-many">one-to-many</SelectItem>
                  <SelectItem value="many-to-one">many-to-one</SelectItem>
                  <SelectItem value="many-to-many">many-to-many</SelectItem>
                </SelectContent>
              </Select>
              <Select v-model="relationshipDraft.direction">
                <SelectTrigger>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="source-to-target">source-to-target</SelectItem>
                  <SelectItem value="target-to-source">target-to-source</SelectItem>
                  <SelectItem value="bidirectional">bidirectional</SelectItem>
                </SelectContent>
              </Select>
              <Button
                :disabled="!relationshipDraft.name || !relationshipDraft.sourceTable || !relationshipDraft.sourceColumn || !relationshipDraft.targetTable || !relationshipDraft.targetColumn"
                @click="saveRelationship" v-permission="'/runtime/db-management/semantic.edit'">Add
                relationship</Button>
            </div>
            <div v-for="item in relationships" :key="item.id"
              class="flex items-center justify-between rounded border p-3 text-sm">
              <div>
                <span class="font-medium">{{ item.name }}</span>
                <span class="ml-2 font-mono text-xs text-muted-foreground">{{ item.sourceTable }}.{{ item.sourceColumn
                  }} → {{ item.targetTable }}.{{ item.targetColumn }} ({{ item.cardinality }})</span>
              </div>
              <Button variant="destructive" size="sm" @click="removeRelationship(item.id)"
                v-permission="'/runtime/db-management/semantic.edit'">Delete</Button>
            </div>
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>Metric metadata</CardTitle>
            <CardDescription>Metrics are discovery metadata only; formulas are not executed as SQL.</CardDescription>
          </CardHeader>
          <CardContent class="space-y-4">
            <div class="grid gap-3 md:grid-cols-3">
              <Input v-model="metricDraft.name" placeholder="Metric name" />
              <Input v-model="metricDraft.displayName" placeholder="Display name" />
              <Select v-model="metricDraft.aggregation">
                <SelectTrigger>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="sum">sum</SelectItem>
                  <SelectItem value="count">count</SelectItem>
                  <SelectItem value="count-distinct">count-distinct</SelectItem>
                  <SelectItem value="avg">avg</SelectItem>
                  <SelectItem value="min">min</SelectItem>
                  <SelectItem value="max">max</SelectItem>
                  <SelectItem value="custom">custom</SelectItem>
                </SelectContent>
              </Select>
              <Input v-model="metricDraft.formula" placeholder="Formula (metadata)" />
              <Input v-model="metricDraft.grain" placeholder="Grain" />
              <Input v-model="metricDraft.filter" placeholder="Filter description" />
              <Input v-model="metricSynonyms" placeholder="Synonyms, comma-separated" />
              <Input v-model="metricDraft.description" placeholder="Description" />
              <Button :disabled="!metricDraft.name || !metricDraft.formula" @click="saveMetric"
                v-permission="'/runtime/db-management/semantic.edit'">Add metric</Button>
            </div>
            <div
              v-for="item in metrics.filter((metric) => metric.schemaName === selectedSchema && metric.tableName === selectedTable)"
              :key="item.id" class="flex items-center justify-between rounded border p-3 text-sm">
              <div>
                <span class="font-medium">{{ item.displayName || item.name }}</span>
                <span class="ml-2 font-mono text-xs text-muted-foreground">{{ item.aggregation }}({{ item.formula
                  }})</span>
              </div>
              <Button variant="destructive" size="sm" @click="removeMetric(item.id)"
                v-permission="'/runtime/db-management/semantic.edit'">Delete</Button>
            </div>
          </CardContent>
        </Card>

        <div v-if="!selectedTable"
          class="flex flex-col items-center justify-center h-[400px] border-2 border-dashed rounded-xl bg-muted/20 text-muted-foreground">
          <Database class="size-12 mb-4 opacity-20" />
          <p>Select a table to start annotating your schema.</p>
        </div>
      </div>
    </div>
  </div>
</template>
