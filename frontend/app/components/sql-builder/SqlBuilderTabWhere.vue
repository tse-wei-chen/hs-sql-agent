<script setup lang="ts">
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Trash2 } from "lucide-vue-next";
import ComboboxInput from "@/components/ComboboxInput.vue";
import SqlBuilderSection from "./SqlBuilderSection.vue";

defineProps<{
  whereConditions: {
    field: string;
    operator: string;
    value: string;
    isOr: boolean;
    isNot: boolean;
  }[];
  options: string[];
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
      class="flex items-center gap-2 p-2 border rounded bg-muted/5"
    >
      <template v-if="type === 'Query'">
        <label
          class="flex flex-col items-center gap-1 text-[0.6rem] whitespace-nowrap"
        >
          <span>OR?</span>
          <input
            type="checkbox"
            v-model="w.isOr"
            class="rounded border-gray-300"
          />
        </label>
        <label
          class="flex flex-col items-center gap-1 text-[0.6rem] whitespace-nowrap"
        >
          <span>NOT?</span>
          <input
            type="checkbox"
            v-model="w.isNot"
            class="rounded border-gray-300"
          />
        </label>
      </template>

      <ComboboxInput
        v-model="w.field"
        :options="options"
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
