<script setup lang="ts">
import { Command, LifeBuoy, Send } from "@lucide/vue";
import NavSecondary from "@/components/NavSecondary.vue";
import NavUser from "@/components/NavUser.vue";
import {
  Sidebar,
  SidebarContent,
  SidebarFooter,
  SidebarHeader,
  SidebarMenu,
  SidebarMenuButton,
  SidebarMenuItem,
  SidebarGroup,
  SidebarGroupContent,
  SidebarGroupLabel,
} from "@/components/ui/sidebar";

const route = useRoute();
const { $can } = useNuxtApp();

const navMain = computed(() =>
  data.value.navGroups
    .map((group) => ({
      ...group,
      items: group.items.filter((item) => $can(`${item.url}.view`)),
    }))
    .filter((group) => group.items.length > 0),
);

const props = defineProps<{
  side?: "left" | "right";
  variant?: "sidebar" | "floating" | "inset";
  collapsible?: "offcanvas" | "icon" | "none";
  class?: any;
}>();

onMounted(() => {
  data.value.user.name = localStorage.getItem("userName") || "User";
  data.value.user.email =
    localStorage.getItem("userEmail") || "user@example.com";
});

const data = ref({
  user: {
    name: "",
    email: "",
    avatar: "",
  },
  navSecondary: [
    {
      title: "Support",
      url: "#",
      icon: LifeBuoy,
    },
    {
      title: "Feedback",
      url: "#",
      icon: Send,
    },
  ],
  navGroups: [
    {
      title: "Runtime Management",
      url: "#",
      items: [
        { title: "Overview", url: "/home" },
        { title: "MCP Keys", url: "/runtime/mcp-keys" },
        { title: "Custom Tools", url: "/runtime/custom-tools" },
        { title: "DB Management", url: "/runtime/db-management" },
        { title: "Audit", url: "/runtime/audit" },
      ],
    },
    {
      title: "Auth Management",
      url: "#",
      items: [
        { title: "Role Management", url: "/auth/role", isActive: true },
        { title: "User Management", url: "/auth/user", isActive: true },
      ],
    },
  ],
});
</script>

<template>
  <Sidebar v-bind="props">
    <SidebarHeader>
      <SidebarMenu>
        <SidebarMenuItem>
          <SidebarMenuButton size="lg" as-child>
            <a href="#">
              <div
                class="flex aspect-square size-8 items-center justify-center rounded-lg bg-sidebar-primary text-sidebar-primary-foreground"
              >
                <Command class="size-4" />
              </div>
              <div class="grid flex-1 text-left text-sm leading-tight">
                <span class="truncate font-medium">HS Admin Panel</span>
                <span class="truncate text-xs">Dashboard</span>
              </div>
            </a>
          </SidebarMenuButton>
        </SidebarMenuItem>
        <SearchForm />
      </SidebarMenu>
    </SidebarHeader>
    <SidebarContent>
      <SidebarGroup v-for="item in navMain" :key="item.title">
        <SidebarGroupLabel>{{ item.title }}</SidebarGroupLabel>
        <SidebarGroupContent>
          <SidebarMenu>
            <SidebarMenuItem
              v-for="childItem in item.items"
              :key="childItem.title"
            >
              <SidebarMenuButton
                as-child
                :is-active="route.path === childItem.url"
              >
                <a @click="navigateTo(childItem.url)">
                  {{ childItem.title }}
                </a>
              </SidebarMenuButton>
            </SidebarMenuItem>
          </SidebarMenu>
        </SidebarGroupContent>
      </SidebarGroup>
      <NavSecondary :items="data.navSecondary" class="mt-auto" />
    </SidebarContent>
    <SidebarFooter>
      <NavUser :user="data.user" />
    </SidebarFooter>
  </Sidebar>
</template>
