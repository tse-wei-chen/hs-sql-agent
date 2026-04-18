<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import {
	listMcpKeys,
	getRuntimeAudit,
	getRuntimeAuditDailySummary,
} from '@/api/runtime'
import type { AuditDailySummaryItem } from '@/api/runtime'
import type { ChartConfig } from '@/components/ui/chart'
import {
	Card,
	CardContent,
	CardDescription,
	CardHeader,
	CardTitle,
} from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import {
	ChartContainer,
	ChartCrosshair,
	ChartLegendContent,
	ChartTooltip,
	ChartTooltipContent,
	componentToString,
} from '@/components/ui/chart'
import { VisAxis, VisGroupedBar, VisXYContainer } from '@unovis/vue'
import { VisDonut, VisSingleContainer } from "@unovis/vue"
import { Donut } from '@unovis/ts'

definePageMeta({
	layout: 'default',
})

interface McpKeyItem {
	id: number
	name: string
	keyPrefix: string
	isActive: boolean
	createdAt?: string | null
	lastUsedAt?: string | null
	sqlProvider?: string | null
	hasSqlConnectionStringOverride?: boolean
	permitLimitOverride?: number | null
	windowSecondsOverride?: number | null
	queueLimitOverride?: number | null
}

interface AuditItem {
	id: number
	actorType: string
	actorId?: string | null
	action: string
	target: string
	detail?: string | null
	result: string
	createdAt: string
}

const loading = ref(false)
const keys = ref<McpKeyItem[]>([])
const recentAudits = ref<AuditItem[]>([])
const dailySummary = ref<AuditDailySummaryItem[]>([])
type AuditDailySummaryData = typeof dailySummary.value[number]
const activeKeyCount = computed(() => keys.value.filter((key) => key.isActive).length)
const revokedKeyCount = computed(() => keys.value.length - activeKeyCount.value)
const failAuditCount = computed(() => recentAudits.value.filter((item) => item.result.toLowerCase() !== 'success').length)
const successAuditCount = computed(() => recentAudits.value.length - failAuditCount.value)
const latestEventAt = computed(() => recentAudits.value[0]?.createdAt || null)
const keyStatusChartData = computed(() => [
	{
		status: 'active',
		active: activeKeyCount.value,
	},
	{
		status: 'revoked',
		revoked: revokedKeyCount.value,
	},
])

const keyStatusChartConfig = {
	active: {
		label: 'Active',
		color: 'var(--chart-5)',
	},
	revoked: {
		label: 'Revoked',
		color: 'var(--chart-3)',
	},
} satisfies ChartConfig

const auditTrendChartConfig = {
	views: {
		label: "Page Views",
		color: undefined,
	},
	success: {
		label: 'Success',
		color: "var(--chart-5)",
	},
	failed: {
		label: 'Failed',
		color: "var(--chart-3)",
	},
} satisfies ChartConfig

const formatTime = (value?: string | null) => {
	if (!value) return 'N/A'
	return new Date(value).toLocaleString()
}

const loadDashboard = async () => {
	loading.value = true
	try {
		const [keyResult, latestAuditResult, dailySummaryResult] = await Promise.all([
			listMcpKeys(),
			getRuntimeAudit(1, 8),
			getRuntimeAuditDailySummary(7),
		])
		keys.value = keyResult || []
		recentAudits.value = latestAuditResult?.items || []
		dailySummary.value = dailySummaryResult?.items.map((item: any) => ({
			day: new Date(item.day).getTime(),
			success: item.successCount || 0,
			failed: item.failedCount || 0,
		})) || []
	} finally {
		loading.value = false
	}
}

onMounted(loadDashboard)
</script>

<template>
	<div class="space-y-4">
		<div class="rounded-xl border bg-gradient-to-r from-sky-100 via-cyan-50 to-emerald-100 p-4">
			<div class="flex flex-col gap-4 md:flex-row md:items-center md:justify-between">
				<div>
					<p class="text-xs font-semibold uppercase tracking-widest text-slate-600">Runtime Dashboard</p>
					<h1 class="text-2xl font-semibold text-slate-900">Operational Overview</h1>
					<p class="text-sm text-slate-700">
						Last event: {{ latestEventAt ? formatTime(latestEventAt) : 'No recent events' }}
					</p>
				</div>
				<div class="flex items-center gap-2">
					<Button variant="outline" @click="loadDashboard" :disabled="loading">
						{{ loading ? 'Refreshing...' : 'Refresh' }}
					</Button>
					<Button as-child>
						<NuxtLink to="/runtime/mcp-keys">Manage Keys</NuxtLink>
					</Button>
				</div>
			</div>
		</div>

		<div class="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
			<Card>
				<CardHeader class="pb-2">
					<CardDescription>Total Keys</CardDescription>
					<CardTitle class="text-3xl">{{ keys.length }}</CardTitle>
				</CardHeader>
				<CardContent class="text-xs text-muted-foreground">Issued server access keys in current environment
				</CardContent>
			</Card>
			<Card>
				<CardHeader class="pb-2">
					<CardDescription>Active Keys</CardDescription>
					<CardTitle class="text-3xl text-emerald-700">{{ activeKeyCount }}</CardTitle>
				</CardHeader>
				<CardContent class="text-xs text-muted-foreground">Ready for MCP runtime access</CardContent>
			</Card>
			<Card>
				<CardHeader class="pb-2">
					<CardDescription>Revoked Keys</CardDescription>
					<CardTitle class="text-3xl text-amber-700">{{ revokedKeyCount }}</CardTitle>
				</CardHeader>
				<CardContent class="text-xs text-muted-foreground">Disabled keys kept for auditability</CardContent>
			</Card>
			<Card>
				<CardHeader class="pb-2">
					<CardDescription>Recent Audit Failures</CardDescription>
					<CardTitle class="text-3xl text-rose-700">{{ failAuditCount }}</CardTitle>
				</CardHeader>
				<CardContent class="text-xs text-muted-foreground">Out of {{ recentAudits.length }} latest events
				</CardContent>
			</Card>
		</div>

		<div class="grid gap-4 xl:grid-cols-3">
			<Card class="xl:col-span-2">
				<CardHeader>
					<CardTitle>Security Signals</CardTitle>
					<CardDescription>Real-time status view from MCP keys and recent audit records</CardDescription>
				</CardHeader>
				<CardContent class="grid gap-4 md:grid-cols-2">
					<div class="rounded-lg border p-3">
						<div class="mb-2 text-xs font-medium uppercase tracking-wide text-muted-foreground">Key Status
						</div>
						<ChartContainer :config="keyStatusChartConfig" class="h-[220px] w-full">
							<VisSingleContainer :data="keyStatusChartData">
								<VisDonut :value="(d: any) => d[d.status]" :arc-width="40"
									:color="(d: any) => keyStatusChartConfig[d.status as keyof typeof keyStatusChartConfig].color"
									:cornerRadius="5" :padAngle="0.05" />

								<ChartTooltip :triggers="{
									[Donut.selectors.segment]: componentToString(keyStatusChartConfig, ChartTooltipContent, { hideLabel: true })!,
								}" />
							</VisSingleContainer>
						</ChartContainer>
					</div>

					<div class="rounded-lg border p-3">
						<div class="mb-2 text-xs font-medium uppercase tracking-wide text-muted-foreground">
							7-Day Daily Outcomes
						</div>

						<div class="relative h-[220px] w-full">
							<div v-if="loading"
								class="absolute inset-0 z-10 flex items-center justify-center bg-background/50 text-sm">
								Loading...
							</div>

							<ChartContainer :config="auditTrendChartConfig" class="aspect-auto h-[250px] w-full" cursor>
								<VisXYContainer :data="dailySummary" :margin="{ left: -24 }" :y-domain="[0, undefined]">

									<VisGroupedBar :x="(d: AuditDailySummaryData) => d.day" :y="[
										(d: AuditDailySummaryData) => d.success,
										(d: AuditDailySummaryData) => d.failed
									]" :color="[
										auditTrendChartConfig.success.color,
										auditTrendChartConfig.failed.color
									]" :group-padding="0.2" :bar-padding="0.1" :rounded-corners="4" />

									<VisAxis type="x" :x="(d: AuditDailySummaryData) => d.day" :tick-line="false"
										:domain-line="false" :grid-line="false"
										:tick-format="(d: number) => new Date(d).toLocaleDateString('en-US', { month: 'short', day: 'numeric' })" />

									<VisAxis type="y" :num-ticks="3" :tick-line="false" :domain-line="false" />

									<ChartTooltip />

									<ChartCrosshair :x="(d: AuditDailySummaryData) => d.day"
										:y="[(d: AuditDailySummaryData) => d.success, (d: AuditDailySummaryData) => d.failed]"
										:template="componentToString(auditTrendChartConfig, ChartTooltipContent, {
											labelFormatter(d) {
												return new Date(d).toLocaleDateString('en-US', {
													month: 'short',
													day: 'numeric',
													year: 'numeric',
												})
											},
										})" :color="auditTrendChartConfig.success.color" />
								</VisXYContainer>
							</ChartContainer>
						</div>
					</div>
				</CardContent>
			</Card>

			<Card>
				<CardHeader class="flex flex-row items-start justify-between gap-3">
					<div>
						<CardTitle>Recent Audit Events</CardTitle>
						<CardDescription>Latest runtime operations and security activities</CardDescription>
					</div>
					<Button variant="outline" size="sm" as-child>
						<NuxtLink to="/runtime/audit">View all</NuxtLink>
					</Button>
				</CardHeader>
				<CardContent>
					<div v-if="loading" class="py-6 text-sm text-muted-foreground">Loading dashboard data...</div>
					<div v-else-if="recentAudits.length === 0" class="py-6 text-sm text-muted-foreground">No audit
						events yet.</div>
					<div v-else class="max-h-[420px] space-y-2 overflow-y-auto pr-1">
						<div v-for="item in recentAudits" :key="item.id" class="rounded-lg border p-3">
							<div class="flex flex-wrap items-center gap-2">
								<span class="text-sm font-medium">{{ item.action }}</span>
								<span class="rounded-full px-2 py-0.5 text-xs"
									:class="item.result.toLowerCase() === 'success' ? 'bg-emerald-100 text-emerald-700' : 'bg-rose-100 text-rose-700'">
									{{ item.result }}
								</span>
							</div>
							<div class="text-xs text-muted-foreground">
								target: {{ item.target }} | actor: {{ item.actorType }} {{ item.actorId || '' }}
							</div>
							<div class="text-xs text-muted-foreground">{{ formatTime(item.createdAt) }}</div>
						</div>
					</div>
				</CardContent>
			</Card>
		</div>

		<div class="grid gap-4 xl:grid-cols-3">
			<Card class="xl:col-span-1 xl:col-start-3">
				<CardHeader>
					<CardTitle>Quick Actions</CardTitle>
					<CardDescription>Jump to runtime management pages</CardDescription>
				</CardHeader>
				<CardContent class="space-y-3">
					<NuxtLink
						class="block rounded-lg border border-sky-200 bg-sky-50 p-3 text-sm transition hover:bg-sky-100"
						to="/runtime/mcp-keys">
						<div class="font-medium text-sky-900">MCP Key Management</div>
						<div class="text-xs text-sky-700">Issue, revoke, and configure tool restrictions</div>
					</NuxtLink>
					<NuxtLink
						class="block rounded-lg border border-emerald-200 bg-emerald-50 p-3 text-sm transition hover:bg-emerald-100"
						to="/runtime/audit">
						<div class="font-medium text-emerald-900">Audit Logs</div>
						<div class="text-xs text-emerald-700">Inspect operation history and security events</div>
					</NuxtLink>

					<div class="rounded-lg border bg-muted/40 p-3 text-xs text-muted-foreground">
						Success events in latest batch: {{ successAuditCount }} / {{ recentAudits.length }}
					</div>
				</CardContent>
			</Card>
		</div>
	</div>
</template>