<script setup lang="ts">
import { computed, onMounted, ref, watch } from "vue";
import { toast } from "vue-sonner";
import {
  explainSql,
  getSqlExplainContext,
  type SqlExplainContext,
  type SqlExplainResult,
} from "@/api/security";
import ComboboxInput from "@/components/ComboboxInput.vue";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import {
  GLOBAL_POLICY_LABEL,
  accessKeyOptions,
  databaseLabel,
  databaseOptions,
  explainOutcome,
  formatParameterValue,
  resolveAccessKey,
  resolveDatabase,
} from "@/lib/sqlExplainPresentation";

const dialects = ["Postgres", "MySQL", "MsSqlServer", "Oracle", "Sqlite", "Firebird"];
const context = ref<SqlExplainContext>({ databases: [], accessKeys: [] });
const loadingContext = ref(false);
const explaining = ref(false);
const selectedDatabaseLabel = ref("");
const selectedAccessKeyLabel = ref(GLOBAL_POLICY_LABEL);
const statementType = ref<"Query" | "DML">("Query");
const sourceDialect = ref("");
const sql = ref("SELECT * FROM public.users WHERE id = 1");
const result = ref<SqlExplainResult | null>(null);

const dbOptions = computed(() => databaseOptions(context.value));
const selectedDatabase = computed(() =>
  resolveDatabase(context.value, selectedDatabaseLabel.value),
);
const keyOptions = computed(() =>
  accessKeyOptions(context.value, selectedDatabase.value?.id),
);
const selectedAccessKey = computed(() =>
  resolveAccessKey(context.value, selectedAccessKeyLabel.value),
);
const outcome = computed(() => (result.value ? explainOutcome(result.value) : null));

watch(selectedDatabaseLabel, () => {
  sourceDialect.value = selectedDatabase.value?.sqlProvider ?? "";
  selectedAccessKeyLabel.value = GLOBAL_POLICY_LABEL;
  result.value = null;
});

watch(statementType, () => {
  result.value = null;
});

const loadContext = async () => {
  loadingContext.value = true;
  try {
    context.value = await getSqlExplainContext();
    if (!selectedDatabaseLabel.value && context.value.databases.length > 0) {
      selectedDatabaseLabel.value = databaseLabel(context.value.databases[0]!);
    }
  } catch (error: any) {
    toast.error(error?.response?.data?.error || "Failed to load SQL explain context.");
  } finally {
    loadingContext.value = false;
  }
};

const runExplain = async () => {
  const database = selectedDatabase.value;
  if (!database) {
    toast.error("Select a target database first.");
    return;
  }
  if (!sql.value.trim()) {
    toast.error("Enter SQL to explain.");
    return;
  }

  explaining.value = true;
  result.value = null;
  try {
    result.value = await explainSql({
      dbManagementId: database.id,
      sql: sql.value,
      statementType: statementType.value,
      sourceDialect: sourceDialect.value || database.sqlProvider,
      accessKeyId: selectedAccessKey.value?.id ?? null,
    });
  } catch (error: any) {
    toast.error(error?.response?.data?.error || "SQL explain request failed.");
  } finally {
    explaining.value = false;
  }
};

onMounted(loadContext);
</script>

<template>
  <Card>
    <CardHeader class="border-b">
      <div class="flex flex-wrap items-start justify-between gap-3">
        <div>
          <CardTitle>SQL Explain / Policy Simulator</CardTitle>
          <CardDescription>
            Run the real compiler against the current security policy, runtime database profile,
            and optional MCP key scope.
          </CardDescription>
        </div>
        <Badge variant="outline">Compile only · no SQL is executed</Badge>
      </div>
    </CardHeader>

    <CardContent class="space-y-5 pt-5">
      <div
        class="rounded-lg border border-sky-300 bg-sky-50 p-3 text-sm text-sky-900 dark:border-sky-900 dark:bg-sky-950/30 dark:text-sky-100"
      >
        The simulator may open the target database connection only to verify its runtime capability
        profile. It never executes the SQL text, starts a mutation transaction, or requests DML
        approval.
      </div>

      <div class="grid gap-4 md:grid-cols-2 xl:grid-cols-4">
        <label class="space-y-2 text-sm">
          <span class="font-medium">Target database</span>
          <ComboboxInput
            v-model="selectedDatabaseLabel"
            :options="dbOptions"
            :allow-custom="false"
            :disabled="loadingContext"
            placeholder="Select database..."
            search-placeholder="Search databases..."
            class="w-full"
          />
        </label>

        <label class="space-y-2 text-sm">
          <span class="font-medium">Statement type</span>
          <select
            v-model="statementType"
            class="flex h-9 w-full rounded-md border border-input bg-background px-3 py-1 text-sm shadow-xs outline-none focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50"
          >
            <option value="Query">Query</option>
            <option value="DML">DML</option>
          </select>
        </label>

        <label class="space-y-2 text-sm">
          <span class="font-medium">Source dialect</span>
          <ComboboxInput
            v-model="sourceDialect"
            :options="dialects"
            :allow-custom="false"
            placeholder="Use target provider..."
            search-placeholder="Search dialects..."
            class="w-full"
          />
        </label>

        <label class="space-y-2 text-sm">
          <span class="font-medium">MCP key scope</span>
          <ComboboxInput
            v-model="selectedAccessKeyLabel"
            :options="keyOptions"
            :allow-custom="false"
            :disabled="!selectedDatabase"
            placeholder="Global policy only"
            search-placeholder="Search keys..."
            class="w-full"
          />
        </label>
      </div>

      <label class="block space-y-2 text-sm">
        <span class="font-medium">SQL</span>
        <textarea
          v-model="sql"
          rows="7"
          spellcheck="false"
          class="min-h-36 w-full resize-y rounded-md border border-input bg-background px-3 py-2 font-mono text-sm shadow-xs outline-none focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50"
          placeholder="Enter SELECT or DML SQL..."
        />
      </label>

      <div class="flex flex-wrap items-center justify-between gap-3">
        <p class="text-xs text-muted-foreground">
          <template v-if="selectedAccessKey">
            Simulating key <span class="font-medium">{{ selectedAccessKey.name }}</span> against its
            current tool and table scope.
          </template>
          <template v-else>
            Global policy mode does not apply an MCP key table whitelist.
          </template>
        </p>
        <Button :disabled="explaining || loadingContext || !selectedDatabase" @click="runExplain">
          {{ explaining ? "Explaining..." : "Explain SQL" }}
        </Button>
      </div>

      <div v-if="result" class="space-y-4 border-t pt-5">
        <div class="flex flex-wrap items-center gap-2">
          <Badge
            variant="outline"
            :class="
              outcome?.tone === 'success'
                ? 'border-emerald-300 bg-emerald-50 text-emerald-800 dark:border-emerald-900 dark:bg-emerald-950/30 dark:text-emerald-200'
                : 'border-red-300 bg-red-50 text-red-800 dark:border-red-900 dark:bg-red-950/30 dark:text-red-200'
            "
          >
            {{ outcome?.label }}
          </Badge>
          <span class="text-sm text-muted-foreground">{{ outcome?.detail }}</span>
          <Badge variant="outline">Executed: {{ result.executed ? "yes" : "no" }}</Badge>
        </div>

        <div class="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
          <div class="rounded-lg border p-3">
            <div class="text-xs text-muted-foreground">Tool scope</div>
            <div class="mt-1 text-sm font-medium">{{ result.scope.toolScope }}</div>
            <div class="mt-1 text-xs text-muted-foreground">
              {{ result.scope.requiredTool }} · {{ result.scope.toolAllowed ? "allowed" : "denied" }}
            </div>
          </div>
          <div class="rounded-lg border p-3">
            <div class="text-xs text-muted-foreground">Table scope</div>
            <div class="mt-1 text-sm font-medium">{{ result.scope.tableScope }}</div>
            <div v-if="result.scope.accessKeyName" class="mt-1 text-xs text-muted-foreground">
              {{ result.scope.accessKeyName }}
            </div>
          </div>
          <div class="rounded-lg border p-3">
            <div class="text-xs text-muted-foreground">Query row cap</div>
            <div class="mt-1 text-sm font-medium">{{ result.policy.queryMaxRows }}</div>
            <div class="mt-1 text-xs text-muted-foreground">
              Timeout {{ result.policy.queryTimeoutSeconds }}s
            </div>
          </div>
          <div class="rounded-lg border p-3">
            <div class="text-xs text-muted-foreground">DML affected-row cap</div>
            <div class="mt-1 text-sm font-medium">{{ result.policy.dmlMaxAffectedRows }}</div>
            <div class="mt-1 text-xs text-muted-foreground">
              {{
                result.policy.dmlAffectedRowLimitEvaluated
                  ? "Evaluated"
                  : "Runtime preview only"
              }}
            </div>
          </div>
        </div>

        <div
          v-if="result.failure"
          class="rounded-lg border border-red-300 bg-red-50 p-4 text-sm dark:border-red-900 dark:bg-red-950/30"
        >
          <div class="font-medium text-red-800 dark:text-red-200">
            {{ result.failure.stage }} · {{ result.failure.code }}
          </div>
          <p class="mt-1 text-red-700 dark:text-red-300">{{ result.failure.message }}</p>
          <div v-if="result.failure.diagnostics.length" class="mt-3 space-y-2">
            <div
              v-for="diagnostic in result.failure.diagnostics"
              :key="`${diagnostic.code}-${diagnostic.start ?? 'none'}`"
              class="rounded border border-red-200/80 bg-background/70 p-2 text-xs dark:border-red-900"
            >
              <div class="font-medium">
                {{ diagnostic.stage }} / {{ diagnostic.category }} · {{ diagnostic.code }}
              </div>
              <div class="mt-1">{{ diagnostic.message }}</div>
              <div v-if="diagnostic.start != null" class="mt-1 text-muted-foreground">
                span {{ diagnostic.start }}..{{ diagnostic.end }} (length {{ diagnostic.length }})
              </div>
            </div>
          </div>
        </div>

        <div v-if="statementType === 'DML' && !result.policy.dmlAffectedRowLimitEvaluated" class="rounded-lg border border-amber-300 bg-amber-50 p-3 text-sm text-amber-900 dark:border-amber-900 dark:bg-amber-950/30 dark:text-amber-100">
          The compiler can prove DML syntax, capability, predicate policy, and table scope here. The
          affected-row limit requires the real preview row set, so it is intentionally not evaluated
          by this compile-only simulator.
        </div>

        <div v-for="statement in result.statements" :key="statement.index" class="rounded-lg border">
          <div class="flex flex-wrap items-center justify-between gap-2 border-b px-4 py-3">
            <div class="font-medium">Statement {{ statement.index }} · {{ statement.kind }}</div>
            <Badge variant="outline">{{ statement.returnsRows ? "Returns rows" : "No row result" }}</Badge>
          </div>
          <div class="space-y-4 p-4">
            <div>
              <div class="mb-2 text-xs font-medium uppercase tracking-wide text-muted-foreground">
                Rendered target SQL
              </div>
              <pre class="overflow-x-auto rounded-md bg-muted p-3 text-xs"><code>{{ statement.renderedSql }}</code></pre>
            </div>

            <div v-if="statement.parameters.length">
              <div class="mb-2 text-xs font-medium uppercase tracking-wide text-muted-foreground">
                Parameters
              </div>
              <div class="grid gap-2 sm:grid-cols-2 xl:grid-cols-3">
                <div
                  v-for="parameter in statement.parameters"
                  :key="parameter.name"
                  class="rounded-md border px-3 py-2 font-mono text-xs"
                >
                  {{ parameter.name }} = {{ formatParameterValue(parameter.value) }}
                </div>
              </div>
            </div>

            <div v-if="statement.queryFacts" class="grid gap-3 md:grid-cols-3">
              <div class="rounded-md border p-3 md:col-span-2">
                <div class="text-xs text-muted-foreground">Referenced tables</div>
                <div class="mt-1 text-sm">
                  {{ statement.queryFacts.referencedTables.join(", ") || "None" }}
                </div>
              </div>
              <div class="rounded-md border p-3">
                <div class="text-xs text-muted-foreground">Query shape</div>
                <div class="mt-1 text-sm">
                  CTE {{ statement.queryFacts.containsCte ? "yes" : "no" }} · Subquery
                  {{ statement.queryFacts.containsSubquery ? "yes" : "no" }}
                </div>
              </div>
            </div>

            <details v-if="statement.evidence" class="rounded-md border p-3">
              <summary class="cursor-pointer text-sm font-medium">Compiler evidence</summary>
              <div class="mt-3 space-y-3 text-xs">
                <div class="grid gap-2 md:grid-cols-2 xl:grid-cols-4">
                  <div><span class="text-muted-foreground">Decision:</span> {{ statement.evidence.decisionBoundary }} / {{ statement.evidence.decisionCode }}</div>
                  <div><span class="text-muted-foreground">Schema:</span> {{ statement.evidence.schemaVersion }}</div>
                  <div><span class="text-muted-foreground">Capability matrix:</span> {{ statement.evidence.capabilityMatrixVersion }}</div>
                  <div><span class="text-muted-foreground">Plan:</span> {{ statement.evidence.planFingerprint || statement.planFingerprint }}</div>
                </div>

                <div v-if="statement.evidence.sourceCapabilities.length || statement.evidence.targetCapabilities.length" class="grid gap-3 lg:grid-cols-2">
                  <div class="rounded-md bg-muted/50 p-3">
                    <div class="mb-2 font-medium">Source capabilities</div>
                    <div v-if="!statement.evidence.sourceCapabilities.length" class="text-muted-foreground">No capability evidence recorded.</div>
                    <div v-for="capability in statement.evidence.sourceCapabilities" :key="`source-${capability.id}`" class="mb-2 last:mb-0">
                      <div>{{ capability.id }} · {{ capability.status }}</div>
                      <div class="text-muted-foreground">{{ capability.detail }}</div>
                    </div>
                  </div>
                  <div class="rounded-md bg-muted/50 p-3">
                    <div class="mb-2 font-medium">Target capabilities</div>
                    <div v-if="!statement.evidence.targetCapabilities.length" class="text-muted-foreground">No capability evidence recorded.</div>
                    <div v-for="capability in statement.evidence.targetCapabilities" :key="`target-${capability.id}`" class="mb-2 last:mb-0">
                      <div>{{ capability.id }} · {{ capability.status }}</div>
                      <div class="text-muted-foreground">{{ capability.detail }}</div>
                    </div>
                  </div>
                </div>
              </div>
            </details>
          </div>
        </div>
      </div>
    </CardContent>
  </Card>
</template>
