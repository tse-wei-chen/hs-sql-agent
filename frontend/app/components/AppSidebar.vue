<script setup lang="ts">
import { Command } from "@lucide/vue";
import NavUser from "@/components/NavUser.vue";
import { Permissions } from "@/lib/permissions";
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
      items: group.items.filter((item) => $can(item.permission)),
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
  navGroups: [
    {
      title: "Runtime Management",
      url: "#",
      items: [
        { title: "Overview", url: "/home", permission: Permissions.Home.View },
        { title: "MCP Keys", url: "/runtime/mcp-keys", permission: Permissions.Runtime.McpKeys.View },
        { title: "Custom Tools", url: "/runtime/custom-tools", permission: Permissions.Runtime.CustomTools.View },
        { title: "DB Management", url: "/runtime/db-management", permission: Permissions.Runtime.DbManagement.View },
        { title: "Audit", url: "/runtime/audit", permission: Permissions.Runtime.Audit.View },
        { title: "Security", url: "/runtime/security", permission: Permissions.Runtime.Security.View },
        { title: "Operability", url: "/runtime/operability", permission: Permissions.Runtime.Operability.View },
      ],
    },
    {
      title: "Auth Management",
      url: "#",
      items: [
        { title: "Role Management", url: "/auth/role", permission: Permissions.Auth.Role.View, isActive: true },
        { title: "User Management", url: "/auth/user", permission: Permissions.Auth.User.View, isActive: true },
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
            <NuxtLink to="/home">
              <div
                class="flex aspect-square size-8 items-center justify-center rounded-lg bg-sidebar-primary text-sidebar-primary-foreground"
              >
                <Command class="size-4" />
              </div>
              <div class="grid flex-1 text-left text-sm leading-tight">
                <span class="truncate font-medium">hs-sql-agent</span>
                <span class="truncate text-xs">Admin Console</span>
              </div>
            </NuxtLink>
          </SidebarMenuButton>
        </SidebarMenuItem>
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
    </SidebarContent>
    <SidebarFooter>
      <NavUser :user="data.user" />
    </SidebarFooter>
  </Sidebar>
</template>
