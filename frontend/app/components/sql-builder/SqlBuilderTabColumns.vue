<script setup lang="ts">
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Trash2 } from "lucide-vue-next";
import ComboboxInput from "@/components/ComboboxInput.vue";
import SqlBuilderSection from "./SqlBuilderSection.vue";

const props = defineProps<{
  selectColumns: { field: string; alias: string }[];
  distinct: boolean;
  options: string[];
  canAutofill: boolean;
}>();

const emit = defineEmits<{
  "update:distinct": [val: boolean];
  add: [];
  remove: [index: number];
  autofill: [];
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
      <ComboboxInput
        v-model="col.field"
        :options="options"
        placeholder="Field Name (e.g. u.id)"
        class="flex-1"
      />
      <Input
        v-model="col.alias"
        placeholder="Alias (e.g. userId)"
        class="h-8 text-xs flex-1"
      />
      <Button
        variant="ghost"
        size="icon"
        class="h-8 w-8 text-destructive"
        @click="emit('remove', i)"
        ><Trash2 class="size-4"
      /></Button>
    </div>
  </SqlBuilderSection>
</template>
