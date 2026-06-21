<script setup lang="ts">
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Trash2 } from "@lucide/vue";
import ComboboxInput from "@/components/ComboboxInput.vue";
import SqlBuilderSection from "./SqlBuilderSection.vue";

defineProps<{
  insertValues: { fieldName: string; value: string }[];
  options: string[];
  canAutofill: boolean;
}>();

const emit = defineEmits<{
  add: [];
  remove: [index: number];
  autofill: [];
}>();
</script>

<template>
  <SqlBuilderSection
    title="Operation Values"
    action-label="Add Value"
    show-action
    @action="emit('add')"
  >
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
      v-if="insertValues.length === 0"
      class="text-xs text-muted-foreground p-4 border rounded text-center"
    >
      No values defined.
    </div>

    <div
      v-for="(v, i) in insertValues"
      :key="i"
      class="flex items-center gap-2"
    >
      <ComboboxInput
        v-model="v.fieldName"
        :options="options"
        placeholder="Column Name"
        class="flex-1"
      />
      <Input
        v-model="v.value"
        placeholder="Value (e.g. {{field}})"
        class="h-8 text-xs flex-1"
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
