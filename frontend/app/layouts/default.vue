<script lang="ts">
export const description = "An inset sidebar with secondary navigation.";
export const iframeHeight = "800px";
</script>

<script setup lang="ts">
import AppSidebar from "@/components/AppSidebar.vue";
import {
  Breadcrumb,
  BreadcrumbItem,
  BreadcrumbLink,
  BreadcrumbList,
  BreadcrumbPage,
  BreadcrumbSeparator,
} from "@/components/ui/breadcrumb";
import { Separator } from "@/components/ui/separator";
import {
  SidebarInset,
  SidebarProvider,
  SidebarTrigger,
} from "@/components/ui/sidebar";
import ToggleTheme from "@/components/ToggleTheme.vue";
const route = useRoute();
const router = useRouter();

const breadcrumbs = computed(() => {
  const path = route.path;
  if (path === "/" || path === "/home") {
    return [{ title: "Overview", url: "/home", isClickable: true }];
  }

  const segments = path.split("/").filter(Boolean);
  const crumbs = [];

  let currentPath = "";

  for (const segment of segments) {
    currentPath += `/${segment}`;

    // Skip numeric IDs in breadcrumbs
    if (/^\d+$/.test(segment)) {
      continue;
    }

    const mapping: Record<string, string> = {
      runtime: "Runtime Admin",
      "db-management": "DB Management",
      "mcp-keys": "MCP Keys",
      "custom-tools": "Custom Tools",
      audit: "Audit",
      home: "Overview",
      semantic: "Semantic Layer",
    };

    const resolved = router.resolve(currentPath);
    const isClickable =
      resolved.matched.length > 0 && resolved.name !== undefined;

    crumbs.push({
      title:
        mapping[segment] ||
        segment.charAt(0).toUpperCase() + segment.slice(1).replace(/-/g, " "),
      url: currentPath,
      isClickable,
    });
  }

  return crumbs;
});
</script>

<template>
  <SidebarProvider>
    <AppSidebar />
    <SidebarInset>
      <header class="flex h-16 shrink-0 items-center gap-2">
        <div class="flex items-center gap-2 px-4">
          <SidebarTrigger class="-ml-1" />
          <Separator
            orientation="vertical"
            class="mr-2 data-[orientation=vertical]:h-4"
          />
          <Breadcrumb>
            <BreadcrumbList>
              <template v-for="(crumb, index) in breadcrumbs" :key="crumb.url">
                <BreadcrumbItem
                  :class="{ 'hidden md:block': index < breadcrumbs.length - 1 }"
                >
                  <template v-if="index < breadcrumbs.length - 1">
                    <BreadcrumbLink v-if="crumb.isClickable" as-child>
                      <NuxtLink :to="crumb.url">
                        {{ crumb.title }}
                      </NuxtLink>
                    </BreadcrumbLink>
                    <span v-else>{{ crumb.title }}</span>
                  </template>
                  <template v-else>
                    <BreadcrumbPage>{{ crumb.title }}</BreadcrumbPage>
                  </template>
                </BreadcrumbItem>
                <BreadcrumbSeparator
                  v-if="index < breadcrumbs.length - 1"
                  class="hidden md:block"
                />
              </template>
            </BreadcrumbList>
          </Breadcrumb>
        </div>
        <div className="ml-auto flex items-center gap-2 mr-2">
          <ToggleTheme />
        </div>
      </header>
      <div class="flex flex-1 flex-col gap-4 p-4 pt-0">
        <slot />
      </div>
    </SidebarInset>
  </SidebarProvider>
</template>
