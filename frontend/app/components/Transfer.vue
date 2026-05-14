<script
  setup
  lang="ts"
  generic="T extends { value: string; label: string; [key: string]: any }"
>
import { computed, ref } from "vue";
import {
  ChevronRightIcon,
  ChevronLeftIcon,
  ChevronsRightIcon,
  ChevronsLeftIcon,
  SearchIcon,
  XIcon,
} from "lucide-vue-next";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { cn } from "@/lib/utils";
import { ScrollArea } from "@/components/ui/scroll-area";

interface Props {
  modelValue: string[];
  options: T[];
  disabled?: boolean;
  leftTitle?: string;
  rightTitle?: string;
}

const props = withDefaults(defineProps<Props>(), {
  disabled: false,
  leftTitle: "Available",
  rightTitle: "Selected",
});

const emit = defineEmits<{
  "update:modelValue": [value: string[]];
}>();

const leftSearch = ref("");
const rightSearch = ref("");

// Tables in the current schema that are NOT in the whitelist
const availableItems = computed(() => {
  return props.options.filter((opt) => !props.modelValue.includes(opt.value));
});

const filteredAvailableItems = computed(() => {
  if (!leftSearch.value) return availableItems.value;
  const search = leftSearch.value.toLowerCase();
  return availableItems.value.filter((item) =>
    item.label.toLowerCase().includes(search),
  );
});

// All selected tables (can be from multiple schemas)
// Since props.options only contains tables for the CURRENT schema,
// we might want to show all selected tables, but we need labels for them.
// If we don't have the label for an item in modelValue (because it's from another schema),
// we just use the value as the label.
const selectedItems = computed(() => {
  return props.modelValue.map((val) => {
    const opt = props.options.find((o) => o.value === val);
    return opt || { value: val, label: val };
  });
});

const filteredSelectedItems = computed(() => {
  if (!rightSearch.value) return selectedItems.value;
  const search = rightSearch.value.toLowerCase();
  return selectedItems.value.filter((item) =>
    item.label.toLowerCase().includes(search),
  );
});

function moveToRight(val: string) {
  if (props.disabled) return;
  if (!props.modelValue.includes(val)) {
    emit("update:modelValue", [...props.modelValue, val]);
  }
}

function moveToLeft(val: string) {
  if (props.disabled) return;
  emit(
    "update:modelValue",
    props.modelValue.filter((v) => v !== val),
  );
}

function moveAllToRight() {
  if (props.disabled) return;
  const newItems = availableItems.value.map((i) => i.value);
  emit("update:modelValue", [...new Set([...props.modelValue, ...newItems])]);
}

function clearAll() {
  if (props.disabled) return;
  emit("update:modelValue", []);
}
</script>

<template>
  <div class="grid grid-cols-[1fr_auto_1fr] gap-4 items-center">
    <!-- Left Panel -->
    <div
      class="flex flex-col border rounded-lg overflow-hidden h-[300px] bg-card shadow-sm"
    >
      <div
        class="px-3 py-2 border-b bg-muted/30 flex items-center justify-between"
      >
        <span class="text-sm font-medium"
          >{{ leftTitle }} ({{ availableItems.length }})</span
        >
      </div>
      <div class="p-2 border-b">
        <div class="relative">
          <SearchIcon
            class="absolute left-2 top-2.5 h-4 w-4 text-muted-foreground"
          />
          <Input
            v-model="leftSearch"
            placeholder="Search..."
            class="pl-8 h-9 text-xs"
            :disabled="disabled"
          />
        </div>
      </div>
      <div class="flex-1 min-h-0">
        <ScrollArea class="h-full p-1">
          <div class="space-y-1">
            <button
              v-for="item in filteredAvailableItems"
              :key="item.value"
              type="button"
              class="w-full text-left px-2 py-1.5 text-xs rounded-md hover:bg-accent hover:text-accent-foreground transition-colors group flex items-center justify-between"
              :disabled="disabled"
              @click="moveToRight(item.value)"
            >
              <span class="truncate">{{ item.label }}</span>
              <ChevronRightIcon
                class="h-3.5 w-3.5 opacity-0 group-hover:opacity-100"
              />
            </button>
            <div
              v-if="filteredAvailableItems.length === 0"
              class="p-4 text-center text-muted-foreground text-xs italic"
            >
              {{ leftSearch ? "No matches" : "Empty" }}
            </div>
          </div>
        </ScrollArea>
      </div>
    </div>

    <!-- Middle Controls -->
    <div class="flex flex-col gap-2">
      <Button
        type="button"
        variant="outline"
        size="icon"
        class="h-8 w-8"
        :disabled="disabled || availableItems.length === 0"
        @click="moveAllToRight"
        title="Add All"
      >
        <ChevronsRightIcon class="h-4 w-4" />
      </Button>
      <Button
        type="button"
        variant="outline"
        size="icon"
        class="h-8 w-8"
        :disabled="disabled || modelValue.length === 0"
        @click="clearAll"
        title="Clear All"
      >
        <ChevronsLeftIcon class="h-4 w-4" />
      </Button>
    </div>

    <!-- Right Panel -->
    <div
      class="flex flex-col border rounded-lg overflow-hidden h-[300px] bg-card shadow-sm"
    >
      <div
        class="px-3 py-2 border-b bg-muted/30 flex items-center justify-between"
      >
        <span class="text-sm font-medium"
          >{{ rightTitle }} ({{ modelValue.length }})</span
        >
      </div>
      <div class="p-2 border-b">
        <div class="relative">
          <SearchIcon
            class="absolute left-2 top-2.5 h-4 w-4 text-muted-foreground"
          />
          <Input
            v-model="rightSearch"
            placeholder="Search..."
            class="pl-8 h-9 text-xs"
            :disabled="disabled"
          />
        </div>
      </div>
      <div class="flex-1 min-h-0">
        <ScrollArea class="h-full p-1">
          <div class="space-y-1">
            <button
              v-for="item in filteredSelectedItems"
              :key="item.value"
              type="button"
              class="w-full text-left px-2 py-1.5 text-xs rounded-md hover:bg-accent hover:text-accent-foreground transition-colors group flex items-center justify-between bg-accent/30"
              :disabled="disabled"
              @click="moveToLeft(item.value)"
            >
              <span class="truncate">{{ item.label }}</span>
              <XIcon
                class="h-3.5 w-3.5 text-muted-foreground hover:text-destructive transition-colors"
              />
            </button>
            <div
              v-if="filteredSelectedItems.length === 0"
              class="p-4 text-center text-muted-foreground text-xs italic"
            >
              {{ rightSearch ? "No matches" : "Nothing selected" }}
            </div>
          </div>
        </ScrollArea>
      </div>
    </div>
  </div>
</template>
