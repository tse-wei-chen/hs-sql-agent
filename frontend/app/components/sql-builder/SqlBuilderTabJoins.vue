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

const props = defineProps<{
  joins: {
    table: string;
    type: string;
    first: string;
    operator: string;
    second: string;
  }[];
  qualifiedTables: string[];
  mainColumnOptions: string[];
  getJoinColumnOptions: (join: any) => string[];
}>();

const emit = defineEmits<{
  add: [];
  remove: [index: number];
  fetchColumns: [index: number];
}>();
</script>

<template>
  <SqlBuilderSection
    title="Join Conditions"
    action-label="Add Join"
    show-action
    @action="emit('add')"
  >
    <div
      v-if="joins.length === 0"
      class="text-xs text-muted-foreground p-4 border rounded text-center"
    >
      No joins defined.
    </div>

    <div
      v-for="(j, i) in joins"
      :key="i"
      class="flex flex-col gap-2 p-3 border rounded bg-muted/5"
    >
      <div class="flex items-center gap-2">
        <Select
          :model-value="j.type"
          @update:model-value="(val) => (j.type = val as string)"
        >
          <SelectTrigger class="h-8 text-xs w-32"
            ><SelectValue
          /></SelectTrigger>
          <SelectContent>
            <SelectItem value="INNER">INNER JOIN</SelectItem>
            <SelectItem value="LEFT">LEFT JOIN</SelectItem>
            <SelectItem value="RIGHT">RIGHT JOIN</SelectItem>
          </SelectContent>
        </Select>
        <ComboboxInput
          v-model="j.table"
          :options="qualifiedTables"
          placeholder="Target Table"
          class="flex-1"
          @update:model-value="emit('fetchColumns', i)"
        />
        <Button
          variant="ghost"
          size="icon"
          class="h-8 w-8 text-destructive shrink-0"
          @click="emit('remove', i)"
          ><Trash2 class="size-4"
        /></Button>
      </div>
      <div class="flex items-center gap-2">
        <span class="text-xs font-semibold px-2">ON</span>
        <ComboboxInput
          v-model="j.first"
          :options="mainColumnOptions"
          placeholder="Source Field (e.g. u.id)"
          class="flex-1"
        />
        <Input
          v-model="j.operator"
          placeholder="="
          class="h-8 text-xs w-16 text-center"
        />
        <ComboboxInput
          v-model="j.second"
          :options="getJoinColumnOptions(j)"
          placeholder="Target Field (e.g. o.user_id)"
          class="flex-1"
        />
      </div>
    </div>
  </SqlBuilderSection>
</template>
