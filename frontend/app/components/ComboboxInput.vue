<script setup lang="ts">
import { ref, watch } from "vue";
import { Check, ChevronsUpDown } from "@lucide/vue";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
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

const props = withDefaults(
  defineProps<{
    modelValue: string;
    options: string[];
    placeholder?: string;
    searchPlaceholder?: string;
    emptyText?: string;
    allowCustom?: boolean;
    disabled?: boolean;
    class?: any;
  }>(),
  {
    placeholder: "Select...",
    searchPlaceholder: "Search or type custom value...",
    emptyText: "No results found.",
    allowCustom: true,
    disabled: false,
  },
);

const emit = defineEmits<{
  "update:modelValue": [value: string];
}>();

const open = ref(false);
const searchVal = ref("");

const onSelect = (value: string) => {
  emit("update:modelValue", value);
  open.value = false;
};

watch(open, (isOpen) => {
  if (isOpen) searchVal.value = "";
});
</script>

<template>
  <Popover v-model:open="open">
    <PopoverTrigger as-child>
      <Button
        variant="outline"
        role="combobox"
        :aria-expanded="open"
        :disabled="disabled"
        :class="cn('justify-between px-3 font-normal', props.class)"
      >
        <span class="truncate">{{ modelValue || placeholder }}</span>
        <ChevronsUpDown class="ml-2 h-4 w-4 shrink-0 opacity-50" />
      </Button>
    </PopoverTrigger>
    <PopoverContent class="min-w-50 p-0" align="start">
      <Command>
        <CommandInput
          :placeholder="searchPlaceholder"
          @input="(event: any) => (searchVal = event.target.value)"
        />
        <CommandList>
          <CommandGroup
            v-if="allowCustom && searchVal && !options.includes(searchVal)"
          >
            <CommandItem :value="searchVal" @select="onSelect(searchVal)">
              <span class="font-medium text-primary">
                Use custom: "{{ searchVal }}"
              </span>
            </CommandItem>
          </CommandGroup>

          <CommandEmpty>{{ emptyText }}</CommandEmpty>

          <CommandGroup>
            <CommandItem
              v-for="option in options"
              :key="option"
              :value="option"
              @select="onSelect(option)"
            >
              <Check
                :class="
                  cn(
                    'mr-2 h-4 w-4',
                    modelValue === option ? 'opacity-100' : 'opacity-0',
                  )
                "
              />
              {{ option }}
            </CommandItem>
          </CommandGroup>
        </CommandList>
      </Command>
    </PopoverContent>
  </Popover>
</template>
