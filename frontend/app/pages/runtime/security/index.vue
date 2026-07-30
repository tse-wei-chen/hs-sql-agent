<script setup lang="ts">
import { onMounted, reactive, ref } from "vue";
import { toast } from "vue-sonner";
import {
  getSecurityPolicy,
  updateSecurityPolicy,
  type SecurityPolicy,
} from "@/api/security";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Field, FieldDescription, FieldLabel } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Switch } from "@/components/ui/switch";

definePageMeta({
  layout: "default",
  permission: "/runtime/security.view",
});

const defaults: SecurityPolicy = {
  queryMaxRows: 1000,
  queryTimeoutSeconds: 30,
  requireWhereForUpdate: true,
  requireWhereForDelete: true,
  allowFullTableUpdate: false,
  allowFullTableDelete: false,
  dmlMaxAffectedRows: 100,
  keyPermitLimit: 120,
  keyWindowSeconds: 60,
  maxConcurrentSql: 16,
};

const policy = reactive<SecurityPolicy>({ ...defaults });
const loading = ref(false);
const saving = ref(false);

const load = async () => {
  loading.value = true;
  try {
    Object.assign(policy, await getSecurityPolicy());
  } catch (error: any) {
    toast.error(error?.response?.data?.error || "Failed to load security policy.");
  } finally {
    loading.value = false;
  }
};

const save = async () => {
  saving.value = true;
  try {
    Object.assign(policy, await updateSecurityPolicy({ ...policy }));
    toast.success("Security policy updated.");
  } catch (error: any) {
    toast.error(
      error?.response?.data?.error ||
        error?.response?.data ||
        "Failed to update security policy.",
    );
  } finally {
    saving.value = false;
  }
};

onMounted(load);
</script>

<template>
  <div class="space-y-4">
    <div>
      <h1 class="text-2xl font-semibold">Security Policy</h1>
      <p class="text-sm text-muted-foreground">
        Server-enforced limits apply to built-in and custom SQL tools.
      </p>
    </div>

    <div v-if="loading" class="text-sm text-muted-foreground">Loading policy...</div>

    <template v-else>
      <Card>
        <CardHeader>
          <CardTitle>Query limits</CardTitle>
          <CardDescription>Bound the cost of each SELECT operation.</CardDescription>
        </CardHeader>
        <CardContent class="grid gap-4 md:grid-cols-2">
          <Field>
            <FieldLabel for="queryMaxRows">Maximum returned rows</FieldLabel>
            <Input id="queryMaxRows" v-model.number="policy.queryMaxRows" type="number" min="1" max="100000" />
            <FieldDescription>The server clamps larger or missing LIMIT values.</FieldDescription>
          </Field>
          <Field>
            <FieldLabel for="queryTimeoutSeconds">SQL timeout (seconds)</FieldLabel>
            <Input id="queryTimeoutSeconds" v-model.number="policy.queryTimeoutSeconds" type="number" min="1" max="600" />
          </Field>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>DML guardrails</CardTitle>
          <CardDescription>
            These checks run before Elicitation and cannot be overridden by approval.
          </CardDescription>
        </CardHeader>
        <CardContent class="grid gap-5 md:grid-cols-2">
          <Field>
            <FieldLabel for="dmlMaxAffectedRows">Maximum affected rows</FieldLabel>
            <Input id="dmlMaxAffectedRows" v-model.number="policy.dmlMaxAffectedRows" type="number" min="1" max="1000000" />
          </Field>
          <div />
          <Field orientation="horizontal">
            <div>
              <FieldLabel>Require WHERE for UPDATE</FieldLabel>
              <FieldDescription>Reject UPDATE statements without conditions.</FieldDescription>
            </div>
            <Switch v-model="policy.requireWhereForUpdate" />
          </Field>
          <Field orientation="horizontal">
            <div>
              <FieldLabel>Allow full-table UPDATE</FieldLabel>
              <FieldDescription>Both this and the WHERE requirement must permit it.</FieldDescription>
            </div>
            <Switch v-model="policy.allowFullTableUpdate" />
          </Field>
          <Field orientation="horizontal">
            <div>
              <FieldLabel>Require WHERE for DELETE</FieldLabel>
              <FieldDescription>Reject DELETE statements without conditions.</FieldDescription>
            </div>
            <Switch v-model="policy.requireWhereForDelete" />
          </Field>
          <Field orientation="horizontal">
            <div>
              <FieldLabel>Allow full-table DELETE</FieldLabel>
              <FieldDescription>Both this and the WHERE requirement must permit it.</FieldDescription>
            </div>
            <Switch v-model="policy.allowFullTableDelete" />
          </Field>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Resource limits</CardTitle>
          <CardDescription>
            Key and concurrency limits protect authenticated SQL workloads.
            Pre-auth IP limits are configured at server startup.
          </CardDescription>
        </CardHeader>
        <CardContent class="grid gap-4 md:grid-cols-2">
          <Field>
            <FieldLabel>Key requests per window</FieldLabel>
            <Input v-model.number="policy.keyPermitLimit" type="number" min="1" />
          </Field>
          <Field>
            <FieldLabel>Key window (seconds)</FieldLabel>
            <Input v-model.number="policy.keyWindowSeconds" type="number" min="1" />
          </Field>
          <Field>
            <FieldLabel>Maximum concurrent SQL operations</FieldLabel>
            <Input v-model.number="policy.maxConcurrentSql" type="number" min="1" />
          </Field>
        </CardContent>
      </Card>

      <div class="flex justify-end">
        <Button v-permission="'edit'" :disabled="saving" @click="save">
          {{ saving ? "Saving..." : "Save policy" }}
        </Button>
      </div>
    </template>
  </div>
</template>
