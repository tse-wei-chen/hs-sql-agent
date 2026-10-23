<script setup lang="ts">
import { computed, onMounted, reactive, ref } from "vue";
import { toast } from "vue-sonner";
import {
  listDmlReconciliation,
  resolveDmlReconciliation,
  inspectDmlTargetWitness,
  type DmlTargetCommitWitnessInspection,
  type DmlReconciliationItem,
  type DmlReconciliationResolution,
} from "@/api/dml-reconciliation";
import {
  buildDmlReconciliationRequest,
  canSubmitDmlReconciliation,
  dmlCommitEvidenceDescription,
  dmlWitnessStatusDescription,
  permittedDmlResolutions,
  type DmlReconciliationForm,
} from "@/lib/dmlReconciliation";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";

definePageMeta({
  layout: "default",
  permission: "/runtime/security.edit",
});

const requests = ref<DmlReconciliationItem[]>([]);
const selected = ref<DmlReconciliationItem | null>(null);
const loading = ref(false);
const busy = ref(false);
const loadError = ref("");
const witnessBusy = ref("");
const witnessResults = ref<Record<string, DmlTargetCommitWitnessInspection>>({});
const witnessErrors = ref<Record<string, string>>({});

const inspectWitness = async (item: DmlReconciliationItem) => {
  if (witnessBusy.value || loading.value || busy.value) return;
  witnessBusy.value = item.requestId;
  witnessResults.value = Object.fromEntries(
    Object.entries(witnessResults.value).filter(([id]) => id !== item.requestId),
  );
  witnessErrors.value = Object.fromEntries(
    Object.entries(witnessErrors.value).filter(([id]) => id !== item.requestId),
  );
  try {
    // Independent target read; this NEVER settles an Executing approval.
    witnessResults.value[item.requestId] = await inspectDmlTargetWitness(item.requestId);
  } catch {
    witnessErrors.value[item.requestId] = "Witness inspection failed. No transaction outcome can be inferred.";
  } finally {
    witnessBusy.value = "";
  }
};

const form = reactive<DmlReconciliationForm>({
  resolution: "",
  evidenceReference: "",
  note: "",
  confirmation: "",
  confirmedIndependentReview: false,
});

const hasMatchingWitness = computed(() =>
  !!selected.value && witnessResults.value[selected.value.requestId]?.matchingCommitWitness === true,
);
const choices = computed(() => permittedDmlResolutions(
  selected.value, hasMatchingWitness.value,
));
const ready = computed(() =>
  canSubmitDmlReconciliation(selected.value, form, hasMatchingWitness.value),
);

const labels: Record<DmlReconciliationResolution, string> = {
  committed: "Attest committed",
  not_committed: "Attest not committed",
  indeterminate: "Keep indeterminate",
};

const formatTime = (value: string) => {
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? value : parsed.toLocaleString();
};

const resetForm = () => {
  form.resolution = "";
  form.evidenceReference = "";
  form.note = "";
  form.confirmation = "";
  form.confirmedIndependentReview = false;
};

const load = async () => {
  loading.value = true;
  loadError.value = "";
  witnessResults.value = {};
  witnessErrors.value = {};
  selected.value = null;
  resetForm();
  try {
    requests.value = await listDmlReconciliation(50);
  } catch {
    requests.value = [];
    loadError.value = "Unable to load the current approval inventory. Verify Security edit permission and retry.";
  } finally {
    loading.value = false;
  }
};

const select = (item: DmlReconciliationItem) => {
  resetForm();
  const permitted = permittedDmlResolutions(item);
  if (!permitted.length) return;
  selected.value = item;
};

const resolve = async () => {
  if (!selected.value || !ready.value || busy.value) return;
  const item = selected.value;
  busy.value = true;
  try {
    await resolveDmlReconciliation(
      item.requestId,
      buildDmlReconciliationRequest(item, form, hasMatchingWitness.value),
    );
    toast.success("Operator attestation and audit evidence were recorded.");
    await load();
  } catch (error: any) {
    const status = error?.response?.status;
    if (status === 409) {
      toast.error("Approval state changed. Reload the evidence before taking another action.");
      await load();
    } else {
      toast.error(error?.response?.data?.error || "Reconciliation was not recorded. Review the audit log before retrying.");
    }
  } finally {
    busy.value = false;
  }
};

onMounted(load);
</script>

<template>
  <div class="space-y-5">
    <div class="flex flex-wrap items-start justify-between gap-3">
      <div>
        <h1 class="text-2xl font-semibold">DML Approval Reconciliation</h1>
        <p class="mt-1 text-sm text-muted-foreground">
          Human-reviewed reconciliation of consumed approvals. This page never executes SQL,
          issues a new approval, or proves a target transaction result.
        </p>
      </div>
      <Button variant="outline" :disabled="loading || busy" @click="load">
        {{ loading ? "Refreshing..." : "Refresh inventory" }}
      </Button>
    </div>

    <Card>
      <CardHeader>
        <CardTitle>Uncertain approvals</CardTitle>
        <CardDescription>
          Most recent 50 requests. A pending or completed approval cannot be reconciled.
          A potentially active Executing request is for investigation only.
        </CardDescription>
      </CardHeader>
      <CardContent class="space-y-3">
        <p v-if="loadError" role="alert" class="text-sm text-destructive">{{ loadError }}</p>
        <p v-else-if="loading" class="text-sm text-muted-foreground">Loading uncertain approvals...</p>
        <p v-else-if="!requests.length" class="text-sm text-muted-foreground">
          No uncertain approvals were returned.
        </p>
        <div v-for="item in requests" :key="item.requestId" class="rounded-md border p-3">
          <div class="flex flex-wrap items-start justify-between gap-3">
            <div class="min-w-0 space-y-1">
              <div class="flex flex-wrap items-center gap-2">
                <span class="break-all font-mono text-xs">{{ item.requestId }}</span>
                <Badge variant="outline">{{ item.status }}</Badge>
              </div>
              <p class="text-xs text-muted-foreground">
                DB #{{ item.dbManagementId }} · {{ item.databaseProvider }}
                · {{ item.databaseIdentity }} · {{ item.statementCount }} statement(s)
              </p>
              <p class="text-xs text-muted-foreground">Last updated: {{ formatTime(item.updatedAt) }}</p>
              <p class="text-sm">{{ dmlCommitEvidenceDescription(item.commitEvidence) }}</p>
            </div>
            <div class="flex flex-wrap gap-2">
              <Button
                variant="outline"
                size="sm"
                :disabled="loading || busy || !!witnessBusy"
                @click="inspectWitness(item)"
              >
                {{ witnessBusy === item.requestId ? "Inspecting..." : "Inspect target witness" }}
              </Button>
              <Button
                variant="outline"
                size="sm"
                :disabled="loading || busy || permittedDmlResolutions(item).length === 0"
                @click="select(item)"
              >
                {{ permittedDmlResolutions(item).length ? "Review evidence" : "Investigation only" }}
              </Button>
            </div>
          </div>
          <p v-if="witnessErrors[item.requestId]" role="alert" class="mt-3 text-sm text-destructive">
            {{ witnessErrors[item.requestId] }}
          </p>
          <div v-if="witnessResults[item.requestId]" class="mt-3 space-y-1 border-t pt-3 text-sm">
            <p class="font-medium">
              Target witness: {{ witnessResults[item.requestId].witnessStatus }}
            </p>
            <p>{{ dmlWitnessStatusDescription(witnessResults[item.requestId].witnessStatus) }}</p>
            <p class="text-xs text-muted-foreground">
              Checked {{ formatTime(witnessResults[item.requestId].inspectedAt) }}.
              A negative result cannot prove the original worker stopped.
            </p>
          </div>
        </div>
      </CardContent>
    </Card>

    <Card v-if="selected">
      <CardHeader>
        <CardTitle>Operator attestation</CardTitle>
        <CardDescription>
          Verify external physical database evidence before recording an irreversible disposition.
          A human attestation does not authorize a retry.
        </CardDescription>
      </CardHeader>
      <CardContent class="space-y-4">
        <div class="rounded-md border p-3 text-sm">
          <p class="break-all font-mono text-xs">{{ selected.requestId }}</p>
          <p class="mt-2">Inspected status: <strong>{{ selected.status }}</strong></p>
          <p>Commit evidence: {{ selected.commitEvidence }}</p>
          <p>Updated: {{ formatTime(selected.updatedAt) }}</p>
          <p v-if="hasMatchingWitness" class="mt-2 font-medium">
            A matching target witness was observed. Only a committed attestation
            can be selected; the server will independently reverify before saving.
          </p>
        </div>
        <label class="block space-y-2 text-sm">
          <span class="font-medium">Operator disposition</span>
          <select
            v-model="form.resolution"
            class="flex h-9 w-full rounded-md border border-input bg-background px-3 text-sm"
            :disabled="busy"
          >
            <option value="">Choose only after independent verification</option>
            <option v-for="choice in choices" :key="choice" :value="choice">
              {{ labels[choice] }}
            </option>
          </select>
        </label>
        <label class="block space-y-2 text-sm">
          <span class="font-medium">Independent evidence reference (required)</span>
          <Input
            v-model="form.evidenceReference"
            maxlength="256"
            :disabled="busy"
            placeholder="Incident or change ticket identifier, database log reference..."
          />
        </label>
        <label class="block space-y-2 text-sm">
          <span class="font-medium">Evidence and rationale (required)</span>
          <Textarea
            v-model="form.note"
            :disabled="busy"
            maxlength="700"
            placeholder="Describe independent database evidence and why the chosen disposition is supported. Do not paste credentials or SQL secrets."
          />
        </label>
        <label class="flex items-start gap-2 text-sm">
          <input v-model="form.confirmedIndependentReview" type="checkbox" :disabled="busy" class="mt-1" />
          <span>I reviewed the target database independently. I understand that this attestation is permanent and does not re-execute the SQL.</span>
        </label>
        <label class="block space-y-2 text-sm">
          <span class="font-medium">Type the exact approval Request ID to confirm</span>
          <Input
            v-model="form.confirmation"
            :disabled="busy"
            autocomplete="off"
            placeholder="Exact request ID"
          />
        </label>
        <div class="flex flex-wrap items-center gap-2">
          <Button
            variant="destructive"
            :disabled="busy || !ready"
            @click="resolve"
          >
            {{ busy ? "Recording..." : "Record irreversible attestation" }}
          </Button>
          <Button variant="outline" :disabled="busy" @click="selected = null; resetForm()">
            Cancel
          </Button>
        </div>
      </CardContent>
    </Card>
  </div>
</template>
