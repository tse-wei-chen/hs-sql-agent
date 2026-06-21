<script setup>
import { Sun, Moon } from "@lucide/vue";
import { Button } from "@/components/ui/button";
const colorMode = useColorMode()

const toggleTheme = () => {
    colorMode.preference = colorMode.value === 'dark' ? 'light' : 'dark'
}
</script>

<template>
    <Button @click="toggleTheme" variant="outline" class="rounded-full w-8 h-8 bg-background mr-2" size="icon"
        aria-label="Toggle theme">
        <span>
            <Moon v-if="colorMode.value === 'dark'"
                className="absolute w-[1.2rem] h-[1.2rem] rotate-0 scale-1000 transition-transform ease-in-out duration-500 dark:-rotate-90 dark:scale-0" />
            <Sun v-else
                className="w-[1.2rem] h-[1.2rem] rotate-90 scale-0 transition-transform ease-in-out duration-500 dark:rotate-0 dark:scale-100" />
        </span>
    </Button>
</template>