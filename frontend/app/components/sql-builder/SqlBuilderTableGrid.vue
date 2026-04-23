<script setup lang="ts">
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Input } from "@/components/ui/input";
import { Field, FieldLabel } from "@/components/ui/field";
import type { DbManagement } from "@/api/db-management";

const props = defineProps<{
  dbs: DbManagement[];
  dbId: number | null;
  schema: string;
  table: string;
  mainAlias: string;
  availableSchemas: string[];
  availableTables: string[];
}>();

const emit = defineEmits<{
  (e: "update:dbId", val: number | null): void;
  (e: "update:schema", val: string): void;
  (e: "update:table", val: string): void;
  (e: "update:mainAlias", val: string): void;
  (e: "dbChange"): void;
  (e: "schemaChange"): void;
  (e: "tableChange"): void;
}>();
</script>

<template>
  <div
    class="grid grid-cols-1 sm:grid-cols-4 gap-4 border p-4 rounded-lg bg-muted/10"
  >
    <Field>
      <FieldLabel class="text-xs">Database</FieldLabel>
      <Select
        :model-value="dbId?.toString()"
        @update:model-value="
          (val) => {
            emit('update:dbId', val ? parseInt(val as string) : null);
            emit('dbChange');
          }
        "
      >
        <SelectTrigger class="h-8"
          ><SelectValue placeholder="Select Database"
        /></SelectTrigger>
        <SelectContent>
          <SelectItem
            v-for="db in dbs"
            :key="db.id"
            :value="db.id.toString()"
            >{{ db.name }}</SelectItem
          >
        </SelectContent>
      </Select>
    </Field>
    <Field>
      <FieldLabel class="text-xs">Schema</FieldLabel>
      <Select
        :model-value="schema"
        @update:model-value="
          (val) => {
            emit('update:schema', val as string);
            emit('schemaChange');
          }
        "
        :disabled="!availableSchemas.length"
      >
        <SelectTrigger class="h-8"
          ><SelectValue placeholder="Select Schema"
        /></SelectTrigger>
        <SelectContent>
          <SelectItem value="_default_">(Default/None)</SelectItem>
          <SelectItem v-for="s in availableSchemas" :key="s" :value="s">{{
            s
          }}</SelectItem>
        </SelectContent>
      </Select>
    </Field>
    <Field>
      <FieldLabel class="text-xs">Table</FieldLabel>
      <Select
        :model-value="table"
        @update:model-value="
          (val) => {
            emit('update:table', val as string);
            emit('tableChange');
          }
        "
        :disabled="!availableTables.length"
      >
        <SelectTrigger class="h-8"
          ><SelectValue placeholder="Select Table"
        /></SelectTrigger>
        <SelectContent>
          <SelectItem v-for="t in availableTables" :key="t" :value="t">{{
            t
          }}</SelectItem>
        </SelectContent>
      </Select>
    </Field>
    <Field>
      <FieldLabel class="text-xs">Alias</FieldLabel>
      <Input
        :model-value="mainAlias"
        @update:model-value="(val) => emit('update:mainAlias', val as string)"
        placeholder="Alias (e.g. u)"
        class="h-8 text-xs"
      />
    </Field>
  </div>
</template>
