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
import { Trash2, Plus } from "@lucide/vue";
import ComboboxInput from "@/components/ComboboxInput.vue";
import SqlBuilderSection from "./SqlBuilderSection.vue";
import type { ColumnItem } from "@/composables/useSqlBuilder";

const props = defineProps<{
  selectColumns: ColumnItem[];
  distinct: boolean;
  options: (table: string) => string[];
  canAutofill: boolean;
  nowValidTables: string[];
}>();

const emit = defineEmits<{
  "update:distinct": [val: boolean];
  add: [];
  remove: [index: number];
  autofill: [];
  addArg: [colIndex: number];
  removeArg: [colIndex: number, argIndex: number];
}>();
</script>

<template>
  <SqlBuilderSection
    title="Selected Columns"
    action-label="Add Column"
    show-action
    @action="emit('add')"
  >
    <template #header-suffix>
      <label class="flex items-center gap-2 text-xs">
        <input
          type="checkbox"
          :checked="distinct"
          @change="
            (e) =>
              emit('update:distinct', (e.target as HTMLInputElement).checked)
          "
          class="rounded border-gray-300"
        />
        DISTINCT
      </label>
    </template>

    <template #actions>
      <Button
        variant="outline"
        size="sm"
        class="h-7 text-xs"
        @click="emit('autofill')"
        :disabled="!canAutofill"
        >Auto-fill All</Button
      >
    </template>

    <div
      v-if="selectColumns.length === 0"
      class="text-xs text-muted-foreground p-4 border rounded text-center"
    >
      No columns defined. (Defaults to * if left empty)
    </div>

    <div
      v-for="(col, i) in selectColumns"
      :key="i"
      class="flex items-center gap-2"
    >
      <Select
        :model-value="col.type"
        @update:model-value="(val) => (col.type = val as ColumnItem['type'])"
      >
        <SelectTrigger class="h-8 text-xs w-28"><SelectValue /></SelectTrigger>
        <SelectContent>
          <SelectItem value="field">Field</SelectItem>
          <SelectItem value="constant">Constant</SelectItem>
          <SelectItem value="function">Function</SelectItem>
        </SelectContent>
      </Select>

      <template v-if="col.type === 'field'">
        <ComboboxInput
          v-model="col.table"
          :options="nowValidTables"
          placeholder="Target Table"
          class="flex-1"
        />
        <ComboboxInput
          v-model="col.field"
          :options="options(col.table)"
          placeholder="Field Name"
          class="flex-1"
        />
      </template>

      <Input
        v-if="col.type === 'constant'"
        v-model="col.constant"
        placeholder="Constant value (e.g. 100, 'active')"
        class="h-8 text-xs flex-1"
      />

      <template v-if="col.type === 'function'">
        <div class="flex flex-col gap-1 flex-1">
          <div class="flex items-center gap-2">
            <Input
              v-model="col.functionName"
              placeholder="Function (e.g. COUNT, SUM)"
              class="h-8 text-xs flex-1"
            />
            <label class="flex items-center gap-1 text-[0.6rem] whitespace-nowrap">
              <input type="checkbox" v-model="col.isDistinct" class="rounded border-gray-300" />
              DISTINCT
            </label>
          </div>
          <div v-if="col.functionName" class="flex flex-col gap-1 ml-1 pl-2 border-l-2 border-muted-foreground/20">
            <div class="flex items-center gap-1">
              <span class="text-[0.6rem] text-muted-foreground">Arguments</span>
              <Button variant="ghost" size="sm" class="h-5 text-xs gap-0.5 px-1" @click="emit('addArg', i)">
                <Plus class="size-2.5" /> Add
              </Button>
            </div>
            <div v-for="(arg, ai) in col.arguments" :key="ai" class="flex items-center gap-1">
              <Select :model-value="arg.type" @update:model-value="(val) => (arg.type = val as 'field' | 'constant')">
                <SelectTrigger class="h-7 text-xs w-20"><SelectValue /></SelectTrigger>
                <SelectContent>
                  <SelectItem value="field">Field</SelectItem>
                  <SelectItem value="constant">Const</SelectItem>
                </SelectContent>
              </Select>
              <template v-if="arg.type === 'field'">
                <ComboboxInput v-model="arg.table" :options="nowValidTables" placeholder="Table" class="flex-1" />
                <ComboboxInput v-model="arg.field" :options="options(arg.table)" placeholder="Field" class="flex-1" />
              </template>
              <Input v-else v-model="arg.constant" placeholder="value" class="h-7 text-xs flex-1" />
              <Button variant="ghost" size="icon" class="h-7 w-7 text-destructive shrink-0" @click="emit('removeArg', i, ai)"><Trash2 class="size-3" /></Button>
            </div>
          </div>
        </div>
      </template>

      <Input
        v-model="col.alias"
        placeholder="Alias"
        class="h-8 text-xs w-32"
      />
      <Button
        variant="ghost"
        size="icon"
        class="h-8 w-8 text-destructive shrink-0"
        @click="emit('remove', i)"
        ><Trash2 class="size-4"
      /></Button>
    </div>
  </SqlBuilderSection>
</template>
