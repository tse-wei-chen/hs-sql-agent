<script setup lang="ts">
import { ref, watch } from "vue";
import { Check, ChevronsUpDown } from "lucide-vue-next";
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

const props = defineProps<{
  modelValue: string;
  options: string[];
  placeholder?: string;
  class?: any;
}>();

const emit = defineEmits<{
  "update:modelValue": [value: string];
}>();

const open = ref(false);

const onSelect = (val: string) => {
  emit("update:modelValue", val);
  open.value = false;
};

// We need a wrapper component or just access to filterState if we were inside Command.
// Since Command is down in the template, we can't easily use `useCommand()` here in script setup.
// Wait! `Command` provides the context. We can't access it here.
// Let's rely on `@input` event on CommandInput or just trust that v-model works on ListboxFilter.
// Actually, `ListboxFilter` is a normal input, so `@input="(e) => searchVal = e.target.value"` will work perfectly!

const searchVal = ref("");

// Watch for changes in the popover state to reset search
watch(open, (isOpen) => {
  if (isOpen) {
    searchVal.value = "";
  }
});
</script>

<template>
  <Popover v-model:open="open">
    <PopoverTrigger as-child>
      <Button
        variant="outline"
        role="combobox"
        :aria-expanded="open"
        :class="cn('justify-between font-normal px-3', props.class)"
      >
        <span class="truncate">{{
          modelValue || placeholder || "Select..."
        }}</span>
        <ChevronsUpDown class="ml-2 h-4 w-4 shrink-0 opacity-50" />
      </Button>
    </PopoverTrigger>
    <!-- We use SameWidth as trigger for PopoverContent if possible, but w-[200px] or dynamic -->
    <PopoverContent class="p-0 min-w-50" align="start">
      <Command>
        <CommandInput
          placeholder="Search or type custom value..."
          @input="(e: any) => (searchVal = e.target.value)"
        />
        <CommandList>
          <!-- Show the typed custom value as an option if it's not empty and not perfectly matching an existing option -->
          <CommandGroup v-if="searchVal && !options.includes(searchVal)">
            <CommandItem :value="searchVal" @select="onSelect(searchVal)">
              <span class="text-primary font-medium"
                >Use custom: "{{ searchVal }}"</span
              >
            </CommandItem>
          </CommandGroup>

          <CommandEmpty v-if="!searchVal"> No results found. </CommandEmpty>

          <CommandGroup>
            <!-- Standard Options -->
            <CommandItem
              v-for="opt in options"
              :key="opt"
              :value="opt"
              @select="onSelect(opt)"
            >
              <Check
                :class="
                  cn(
                    'mr-2 h-4 w-4',
                    modelValue === opt ? 'opacity-100' : 'opacity-0',
                  )
                "
              />
              {{ opt }}
            </CommandItem>
          </CommandGroup>
        </CommandList>
      </Command>
    </PopoverContent>
  </Popover>
</template>
