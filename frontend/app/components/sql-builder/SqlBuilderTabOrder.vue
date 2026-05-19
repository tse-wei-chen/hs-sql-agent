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
import { Field, FieldLabel } from "@/components/ui/field";
import ComboboxInput from "@/components/ComboboxInput.vue";
import SqlBuilderSection from "./SqlBuilderSection.vue";
import type { OrderByItem } from "@/composables/useSqlBuilder";

const props = defineProps<{
  orderBys: OrderByItem[];
  limit: number | null;
  offset: number | null;
  nowValidTables: string[];
  options: (table: string) => string[];
}>();

const emit = defineEmits<{
  add: [];
  remove: [index: number];
  addArg: [obIndex: number];
  removeArg: [obIndex: number, argIndex: number];
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
        <Select
          :model-value="o.type"
          @update:model-value="(val) => (o.type = val as OrderByItem['type'])"
        >
          <SelectTrigger class="h-8 text-xs w-28"
            ><SelectValue
          /></SelectTrigger>
          <SelectContent>
            <SelectItem value="field">Field</SelectItem>
            <SelectItem value="function">Function</SelectItem>
          </SelectContent>
        </Select>

        <template v-if="o.type === 'field'">
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
        </template>

        <template v-if="o.type === 'function'">
          <div class="flex flex-col gap-1 flex-1">
            <Input
              v-model="o.functionName"
              placeholder="Function (e.g. COUNT, SUM)"
              class="h-8 text-xs"
            />
            <div
              v-if="o.functionName"
              class="flex flex-col gap-1 ml-1 pl-2 border-l-2 border-muted-foreground/20"
            >
              <div class="flex items-center gap-1">
                <span class="text-[0.6rem] text-muted-foreground"
                  >Arguments</span
                >
                <Button
                  variant="ghost"
                  size="sm"
                  class="h-5 text-xs gap-0.5 px-1"
                  @click="emit('addArg', i)"
                >
                  <Plus class="size-2.5" /> Add
                </Button>
              </div>
              <div
                v-for="(arg, ai) in o.arguments"
                :key="ai"
                class="flex items-center gap-1"
              >
                <Select
                  :model-value="arg.type"
                  @update:model-value="
                    (val) => (arg.type = val as 'field' | 'constant')
                  "
                >
                  <SelectTrigger class="h-7 text-xs w-20"
                    ><SelectValue
                  /></SelectTrigger>
                  <SelectContent>
                    <SelectItem value="field">Field</SelectItem>
                    <SelectItem value="constant">Const</SelectItem>
                  </SelectContent>
                </Select>
                <template v-if="arg.type === 'field'">
                  <ComboboxInput
                    v-model="arg.table"
                    :options="nowValidTables"
                    placeholder="Table"
                    class="flex-1"
                  />
                  <ComboboxInput
                    v-model="arg.field"
                    :options="options(arg.table)"
                    placeholder="Field"
                    class="flex-1"
                  />
                </template>
                <Input
                  v-else
                  v-model="arg.constant"
                  placeholder="value"
                  class="h-7 text-xs flex-1"
                />
                <Button
                  variant="ghost"
                  size="icon"
                  class="h-7 w-7 text-destructive shrink-0"
                  @click="emit('removeArg', i, ai)"
                  ><Trash2 class="size-3"
                /></Button>
              </div>
            </div>
          </div>
        </template>

        <Select
          :model-value="o.direction"
          @update:model-value="(val) => (o.direction = val as string)"
        >
          <SelectTrigger class="h-8 text-xs w-28"
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
          class="h-8 w-8 text-destructive shrink-0"
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
