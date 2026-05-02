<script setup lang="ts">
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Trash2 } from "lucide-vue-next";
import { Field, FieldLabel } from "@/components/ui/field";
import ComboboxInput from "@/components/ComboboxInput.vue";
import SqlBuilderSection from "./SqlBuilderSection.vue";

const props = defineProps<{
  orderBys: { table: string; field: string; direction: string }[];
  limit: number | null;
  offset: number | null;
  nowValidTables: string[];
  options: (table: string) => string[];
}>();

const emit = defineEmits<{
  add: [];
  remove: [index: number];
  "update:limit": [val: number | null];
  "update:offset": [val: number | null];
}>();
</script>

<template>
  <div class="space-y-6">
    <SqlBuilderSection
      title="Order By"
      action-label="Add Order By"
      show-action
      @action="emit('add')"
    >
      <div
        v-if="orderBys.length === 0"
        class="text-xs text-muted-foreground p-4 border rounded text-center"
      >
        No order by clauses.
      </div>

      <div v-for="(o, i) in orderBys" :key="i" class="flex items-center gap-2">
        <ComboboxInput
          v-model="o.table"
          :options="nowValidTables"
          placeholder="Target Table"
          class="flex-1"
        />
        <ComboboxInput
          v-model="o.field"
          :options="options(o.table)"
          placeholder="Field Name"
          class="flex-1"
        />
        <Select
          :model-value="o.direction"
          @update:model-value="(val) => (o.direction = val as string)"
        >
          <SelectTrigger class="h-8 text-xs w-32"
            ><SelectValue
          /></SelectTrigger>
          <SelectContent>
            <SelectItem value="asc">ASC</SelectItem>
            <SelectItem value="desc">DESC</SelectItem>
            <SelectItem value="random">RANDOM</SelectItem>
          </SelectContent>
        </Select>
        <Button
          variant="ghost"
          size="icon"
          class="h-8 w-8 text-destructive"
          @click="emit('remove', i)"
          ><Trash2 class="size-4"
        /></Button>
      </div>
    </SqlBuilderSection>

    <div class="grid grid-cols-2 gap-4">
      <Field>
        <FieldLabel class="text-xs">Limit</FieldLabel>
        <Input
          type="number"
          :model-value="limit ?? undefined"
          @update:model-value="(val) => emit('update:limit', val as number)"
          placeholder="100"
          class="h-8 text-xs"
        />
      </Field>
      <Field>
        <FieldLabel class="text-xs">Offset</FieldLabel>
        <Input
          type="number"
          :model-value="offset ?? undefined"
          @update:model-value="(val) => emit('update:offset', val as number)"
          placeholder="0"
          class="h-8 text-xs"
        />
      </Field>
    </div>
  </div>
</template>
