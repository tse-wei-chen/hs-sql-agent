<script
  setup
  lang="ts"
  generic="T extends { value: string; label: string; [key: string]: any }"
>
import { computed, ref } from "vue";
import { CheckIcon, ChevronDownIcon, XIcon } from "lucide-vue-next";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import {
  Command,
  CommandEmpty,
  CommandGroup,
  CommandInput,
  CommandItem,
  CommandList,
} from "@/components/ui/command";
import {
  Popover,
  PopoverContent,
  PopoverTrigger,
} from "@/components/ui/popover";

interface Props {
  modelValue: string[];
  options: T[];
  placeholder?: string;
  searchPlaceholder?: string;
  emptyMessage?: string;
  class?: string;
}

const props = withDefaults(defineProps<Props>(), {
  placeholder: "Select...",
  searchPlaceholder: "Search...",
  emptyMessage: "No results found.",
});

const emit = defineEmits<{
  "update:modelValue": [value: string[]];
}>();

const open = ref(false);

const selectedItems = computed(() =>
  props.options.filter((opt) => props.modelValue.includes(opt.value)),
);

function toggleOption(val: string) {
  const newValues = props.modelValue.includes(val)
    ? props.modelValue.filter((v) => v !== val)
    : [...props.modelValue, val];

  emit("update:modelValue", newValues);
}
</script>

<template>
  <Popover v-model:open="open">
    <PopoverTrigger as-child>
      <Button
        variant="outline"
        role="combobox"
        :aria-expanded="open"
        :class="
          cn(
            'w-full justify-between h-auto min-h-10 px-3 py-2 text-left',
            props.class,
          )
        "
      >
        <div class="flex flex-wrap gap-1.5 flex-1 overflow-hidden text-left">
          <template v-if="modelValue.length > 0">
            <Badge
              v-for="item in selectedItems"
              :key="item.value"
              variant="outline"
              class="flex items-center gap-1 max-w-full"
            >
              <span class="truncate">{{ item.label }}</span>
              <button @mousedown.prevent @click.stop="toggleOption(item.value)">
                <XIcon
                  class="h-3 w-3 text-muted-foreground hover:text-foreground"
                />
              </button>
            </Badge>
          </template>
          <span v-else class="text-muted-foreground text-sm">{{
            placeholder
          }}</span>
        </div>
        <ChevronDownIcon class="ml-2 h-4 w-4 shrink-0 opacity-50" />
      </Button>
    </PopoverTrigger>

    <PopoverContent
      class="p-0 border shadow-md"
      align="start"
      :side-offset="4"
      :portal-config="{ disabled: true }"
      :to="null"
      :style="{
        width: 'var(--radix-popover-trigger-width)',
        minWidth: 'var(--radix-popover-trigger-width)',
      }"
    >
      <Command
        :filter-function="
          (list: any[], search: string) =>
            list.filter((i) => i.toLowerCase().includes(search.toLowerCase()))
        "
      >
        <CommandInput :placeholder="searchPlaceholder" />
        <CommandList class="max-h-64 overflow-y-auto w-full">
          <CommandEmpty>{{ emptyMessage }}</CommandEmpty>
          <CommandGroup>
            <CommandItem
              v-for="option in options"
              :key="option.value"
              :value="option.value"
              class="cursor-pointer w-full"
              @select="toggleOption(option.value)"
            >
              <div class="flex items-center w-full min-w-0">
                <CheckIcon
                  :class="
                    cn(
                      'mr-2 h-4 w-4 shrink-0',
                      modelValue.includes(option.value)
                        ? 'opacity-100'
                        : 'opacity-0',
                    )
                  "
                />
                <div class="flex-1 w-full min-w-0">
                  <slot name="option" :option="option">
                    {{ option.label }}
                  </slot>
                </div>
              </div>
            </CommandItem>
          </CommandGroup>
        </CommandList>
      </Command>
    </PopoverContent>
  </Popover>
</template>
