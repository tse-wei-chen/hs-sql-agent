<script setup lang="ts">
import { Button } from "@/components/ui/button";
import { Plus } from "lucide-vue-next";

defineProps<{
  title: string;
  description?: string;
  actionLabel?: string;
  showAction?: boolean;
}>();

defineEmits<{
  action: [];
}>();
</script>

<template>
  <div class="space-y-4">
    <div class="flex items-center justify-between">
      <div class="flex items-center gap-4">
        <p class="text-sm font-medium">{{ title }}</p>
        <slot name="header-suffix" />
      </div>
      <div v-if="showAction" class="flex gap-2">
        <slot name="actions" />
        <Button
          v-if="actionLabel"
          variant="outline"
          size="sm"
          class="h-7 text-xs"
          @click="$emit('action')"
        >
          <Plus class="size-3 mr-1" /> {{ actionLabel }}
        </Button>
      </div>
    </div>

    <div class="space-y-3">
      <slot />
    </div>
  </div>
</template>
