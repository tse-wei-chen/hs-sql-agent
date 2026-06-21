<script setup lang="ts">
import { Field as UIField, FieldLabel } from "@/components/ui/field";
import {
  Tooltip,
  TooltipContent,
  TooltipProvider,
  TooltipTrigger,
} from "@/components/ui/tooltip";
import { CircleAlert, CircleCheck } from "@lucide/vue";
import { useFormContext } from "vee-validate";

defineOptions({
  inheritAttrs: false,
});

const props = defineProps<{
  name: string;
  rules?: string;
  label?: string;
  class?: string;
  helpText?: string;
}>();

const formContext = useFormContext();
const submitCount = computed(() => formContext?.submitCount ?? 0);

const showError = (
  errorMessage: string | undefined,
  fieldMeta: { touched: boolean },
) => errorMessage && (fieldMeta.touched || submitCount.value.value > 0);
</script>

<template>
  <VeeField
    v-slot="{ field, errorMessage, meta: fieldMeta }"
    :name="name"
    :rules="rules"
  >
    <UIField :class="props.class">
      <FieldLabel v-if="label" :for="name"
        >{{ label }}<RequiredStar v-if="rules?.includes('required')"
      /></FieldLabel>
      <div class="relative">
        <slot :field="field" :errorMessage="errorMessage" :meta="fieldMeta" />
        <TooltipProvider v-if="showError(errorMessage, fieldMeta)">
          <Tooltip>
            <TooltipTrigger as-child>
              <div class="absolute right-0 top-1/2 -translate-y-1/2 pr-3">
                <CircleAlert class="size-4 text-destructive" />
              </div>
            </TooltipTrigger>
            <TooltipContent side="top" align="end">
              {{ errorMessage }}
            </TooltipContent>
          </Tooltip>
        </TooltipProvider>
        <div
          v-else-if="fieldMeta.touched && fieldMeta.valid"
          class="absolute right-0 top-1/2 -translate-y-1/2 pr-3"
        >
          <CircleCheck class="size-4 text-green-500" />
        </div>
      </div>
    </UIField>
  </VeeField>
</template>
