<script setup lang="ts">
import { ref, computed } from 'vue'
import { EyeIcon, EyeOffIcon } from 'lucide-vue-next'
import { cn } from '@/lib/utils'
import { InputGroup, InputGroupAddon, InputGroupButton, InputGroupInput } from '@/components/ui/input-group'

interface Props {
    modelValue?: string | number
    class?: any
}

const props = defineProps<Props>()

const emit = defineEmits(['update:modelValue'])

const show = ref(false)
const handleToggle = () => (show.value = !show.value)

const value = computed({
    get: () => props.modelValue,
    set: (val) => emit('update:modelValue', val)
})
</script>

<template>
    <InputGroup>
        <InputGroupInput v-model="value" :type="show ? 'text' : 'password'" :class="cn(props.class)" v-bind="$attrs" />
        <InputGroupAddon align="inline-end">
            <InputGroupButton type="button" size="icon-xs" variant="ghost" @click="handleToggle">
                <EyeOffIcon v-if="show" class="h-4 w-4" />
                <EyeIcon v-else class="h-4 w-4" />
            </InputGroupButton>
        </InputGroupAddon>
    </InputGroup>
</template>