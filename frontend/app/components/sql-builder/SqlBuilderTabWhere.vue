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
import ComboboxInput from "@/components/ComboboxInput.vue";
import SqlBuilderSection from "./SqlBuilderSection.vue";
import type { WhereItem } from "@/composables/useSqlBuilder";

const props = defineProps<{
  whereConditions: WhereItem[];
  nowValidTables: string[];
  options: (table: string) => string[];
  type: "Query" | "DML";
}>();

const emit = defineEmits<{
  add: [];
  remove: [index: number];
}>();
</script>

<template>
  <SqlBuilderSection
    title="Where Conditions"
    action-label="Add Condition"
    show-action
    @action="emit('add')"
  >
    <div
      v-if="whereConditions.length === 0"
      class="text-xs text-muted-foreground p-4 border rounded text-center"
    >
      No conditions defined.
    </div>

    <div
      v-for="(w, i) in whereConditions"
      :key="i"
      class="flex flex-col gap-2 p-2 border rounded bg-muted/5"
    >
      <div class="flex items-center gap-2">
        <Select
          :model-value="w.type"
          @update:model-value="(val) => (w.type = val as WhereItem['type'])"
        >
          <SelectTrigger class="h-8 text-xs w-32"><SelectValue /></SelectTrigger>
          <SelectContent>
            <SelectItem value="basic">Basic</SelectItem>
            <SelectItem value="column_compare">Column Compare</SelectItem>
            <SelectItem value="in">IN</SelectItem>
          </SelectContent>
        </Select>

        <template v-if="type === 'Query' && w.type !== 'column_compare'">
          <label class="flex flex-col items-center gap-1 text-[0.6rem] whitespace-nowrap">
            <span>OR?</span>
            <input
              type="checkbox"
              v-model="w.isOr"
              class="rounded border-gray-300"
            />
          </label>
          <label class="flex flex-col items-center gap-1 text-[0.6rem] whitespace-nowrap">
            <span>NOT?</span>
            <input
              type="checkbox"
              v-model="w.isNot"
              class="rounded border-gray-300"
            />
          </label>
        </template>

        <Button
          variant="ghost"
          size="icon"
          class="h-8 w-8 text-destructive shrink-0 ml-auto"
          @click="emit('remove', i)"
          ><Trash2 class="size-4"
        /></Button>
      </div>

      <!-- Basic type -->
      <template v-if="w.type === 'basic'">
        <div class="flex items-center gap-2">
          <ComboboxInput
            v-model="w.table"
            :options="nowValidTables"
            placeholder="Target Table"
            class="flex-1"
          />
          <ComboboxInput
            v-model="w.field"
            :options="options(w.table)"
            placeholder="Field"
            class="flex-1"
          />
          <Input
            v-model="w.operator"
            placeholder="Operator (=, >, LIKE)"
            class="h-8 text-xs w-24"
          />
          <Input
            v-model="w.value"
            placeholder="Value (e.g. {{userId}})"
            class="h-8 text-xs flex-1"
          />
          <label class="flex items-center gap-1 text-[0.6rem] whitespace-nowrap">
            <input
              type="checkbox"
              v-model="w.isDate"
              class="rounded border-gray-300"
            />
            Date
          </label>
        </div>
      </template>

      <!-- Column Compare type -->
      <template v-if="w.type === 'column_compare'">
        <div class="flex items-center gap-2">
          <ComboboxInput
            v-model="w.leftTable"
            :options="nowValidTables"
            placeholder="Left Table"
            class="flex-1"
          />
          <ComboboxInput
            v-model="w.leftField"
            :options="options(w.leftTable)"
            placeholder="Left Field"
            class="flex-1"
          />
          <Input
            v-model="w.operator"
            placeholder="="
            class="h-8 text-xs w-16 text-center"
          />
          <ComboboxInput
            v-model="w.rightTable"
            :options="nowValidTables"
            placeholder="Right Table"
            class="flex-1"
          />
          <ComboboxInput
            v-model="w.rightField"
            :options="options(w.rightTable)"
            placeholder="Right Field"
            class="flex-1"
          />
        </div>
      </template>

      <!-- IN type -->
      <template v-if="w.type === 'in'">
        <div class="flex items-center gap-2">
          <ComboboxInput
            v-model="w.table"
            :options="nowValidTables"
            placeholder="Target Table"
            class="flex-1"
          />
          <ComboboxInput
            v-model="w.field"
            :options="options(w.table)"
            placeholder="Field"
            class="flex-1"
          />
          <span class="text-xs font-semibold">IN</span>
          <Input
            v-model="w.values"
            placeholder="val1, val2, val3"
            class="h-8 text-xs flex-1"
          />
          <label class="flex items-center gap-1 text-[0.6rem] whitespace-nowrap">
            <input
              type="checkbox"
              v-model="w.isDate"
              class="rounded border-gray-300"
            />
            Date
          </label>
        </div>
      </template>
    </div>
  </SqlBuilderSection>
</template>
