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
import { Trash2, Plus } from "lucide-vue-next";
import ComboboxInput from "@/components/ComboboxInput.vue";
import SqlBuilderSection from "./SqlBuilderSection.vue";
import type { JoinItem } from "@/composables/useSqlBuilder";

const props = defineProps<{
  joins: JoinItem[];
  qualifiedTables: string[];
  nowValidTables: string[];
  mainColumnOptions: string[];
  getJoinColumnOptions: (table: string) => string[];
}>();

const emit = defineEmits<{
  add: [];
  remove: [index: number];
  addOnCondition: [joinIndex: number];
  removeOnCondition: [joinIndex: number, condIndex: number];
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
            <SelectItem value="Inner">INNER JOIN</SelectItem>
            <SelectItem value="Left">LEFT JOIN</SelectItem>
            <SelectItem value="Right">RIGHT JOIN</SelectItem>
            <SelectItem value="Full">FULL JOIN</SelectItem>
            <SelectItem value="Cross">CROSS JOIN</SelectItem>
          </SelectContent>
        </Select>
        <ComboboxInput
          v-model="j.table"
          :options="qualifiedTables"
          placeholder="Target Table"
          class="flex-1"
          @update:model-value="emit('fetchColumns', i)"
        />
        <Input
          v-model="j.alias"
          placeholder="Alias (optional)"
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

      <div class="ml-2 pl-3 border-l-2 border-muted-foreground/20 space-y-2">
        <div class="flex items-center justify-between">
          <span class="text-xs font-semibold text-muted-foreground">ON Conditions</span>
          <Button
            variant="ghost"
            size="sm"
            class="h-6 text-xs gap-1"
            @click="emit('addOnCondition', i)"
          >
            <Plus class="size-3" /> Add Condition
          </Button>
        </div>

        <div
          v-for="(oc, ci) in j.onConditions"
          :key="ci"
          class="flex items-center gap-2"
        >
          <span class="text-xs text-muted-foreground w-4">{{ ci === 0 ? '' : 'AND' }}</span>
          <ComboboxInput
            v-model="oc.leftTable"
            :options="nowValidTables"
            placeholder="Source Table"
            class="flex-1"
          />
          <ComboboxInput
            v-model="oc.leftField"
            :options="getJoinColumnOptions(oc.leftTable)"
            placeholder="Source Field"
            class="flex-1"
          />
          <Input
            v-model="oc.operator"
            placeholder="="
            class="h-8 text-xs w-16 text-center"
          />
          <ComboboxInput
            v-model="oc.rightTable"
            :options="nowValidTables"
            placeholder="Target Table"
            class="flex-1"
          />
          <ComboboxInput
            v-model="oc.rightField"
            :options="getJoinColumnOptions(oc.rightTable)"
            placeholder="Target Field"
            class="flex-1"
          />
          <Button
            variant="ghost"
            size="icon"
            class="h-8 w-8 text-destructive shrink-0"
            @click="emit('removeOnCondition', i, ci)"
            :disabled="j.onConditions.length <= 1"
            ><Trash2 class="size-4"
          /></Button>
        </div>
      </div>
    </div>
  </SqlBuilderSection>
</template>
