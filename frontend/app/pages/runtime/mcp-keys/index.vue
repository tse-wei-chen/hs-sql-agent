<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { Button } from '@/components/ui/button'
import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
} from '@/components/ui/card'
import {
    Select,
    SelectContent,
    SelectGroup,
    SelectItem,
    SelectLabel,
    SelectTrigger,
    SelectValue,
} from '@/components/ui/select'
import { Input } from '@/components/ui/input'
import { Field, FieldGroup, FieldLabel } from '@/components/ui/field'
import { issueMcpKey, listMcpKeys, revokeMcpKey, testDbConnection } from '@/api/runtime'

definePageMeta({
	layout: 'default',
})

interface McpKeyItem {
	id: number
	name: string
	keyPrefix: string
	isActive: boolean
	lastUsedAt?: string | null
	corsAllowedOrigins?: string | null
	sqlProvider?: string | null
	hasSqlConnectionStringOverride?: boolean
}

const keys = ref<McpKeyItem[]>([])
const loading = ref(false)
const issuing = ref(false)
const testing = ref(false)
const newKeyName = ref('')
const expiresMode = ref('never')
const customExpiresAt = ref('')
const selectedTools = ref<string[]>([])
const corsAllowedOrigins = ref('')
const sqlProvider = ref('Global')
const sqlConnectionString = ref('')

const connectionTestResult = ref<{ success: boolean; errorMessage: string } | null>(null)

const issuedPlaintextKey = ref('')

const toolOptions = [
	{ label: 'Execute Query', value: 'execute_query_safe', risk: 'medium' },
	{ label: 'Get Columns', value: 'get_columns', risk: 'low' },
	{ label: 'Get Schemas', value: 'get_schemas', risk: 'low' },
	{ label: 'Get Tables', value: 'get_tables', risk: 'low' },
    { label: 'Execute DML', value: 'execute_dml_safe', risk: 'high' },
]

const providerOptions = ['Global', 'Sqlite', 'Postgres', 'MySQL', 'MsSqlServer', 'Oracle', 'Firebird']


const selectedToolLabel = computed(() => {
	if (selectedTools.value.length === 0) {
		return 'Global (no restriction)'
	}

	return `${selectedTools.value.length} tools selected`
})

const expiresAt = computed(() => {
	if (expiresMode.value === 'never') {
		return null
	}

	if (expiresMode.value === 'custom') {
		return customExpiresAt.value ? new Date(customExpiresAt.value).toISOString() : null
	}

	const now = new Date()
	const days = Number(expiresMode.value)
	if (!Number.isFinite(days) || days <= 0) {
		return null
	}

	now.setDate(now.getDate() + days)
	return now.toISOString()
})



const load = async () => {
	loading.value = true
	try {
		keys.value = await listMcpKeys()
	} finally {
		loading.value = false
	}
}

const issue = async () => {
	if (!newKeyName.value.trim()) {
		alert('Key name is required.')
		return
	}

	const normalizedSqlConnectionString = sqlConnectionString.value.trim()
	const hasSqlProviderOverride = sqlProvider.value !== 'Global'
	const hasSqlConnectionStringOverride = normalizedSqlConnectionString.length > 0

	if (hasSqlProviderOverride !== hasSqlConnectionStringOverride) {
		alert('SQL Provider and SQL Connection String must be filled together, or both left empty.')
		return
	}



	issuing.value = true
	try {
		const result = await issueMcpKey({
			name: newKeyName.value.trim(),
			expiresAt: expiresAt.value,
			allowedTools: selectedTools.value.length > 0 ? selectedTools.value.join(',') : null,
			corsAllowedOrigins: corsAllowedOrigins.value.trim() || null,
			sqlProvider: sqlProvider.value === 'Global' ? null : sqlProvider.value,
			sqlConnectionString: normalizedSqlConnectionString || null,
		})

		issuedPlaintextKey.value = result.plaintextKey || ''
		newKeyName.value = ''
		expiresMode.value = 'never'
		customExpiresAt.value = ''
		selectedTools.value = []
		corsAllowedOrigins.value = ''
		sqlProvider.value = 'Global'
		sqlConnectionString.value = ''
		await load()
	} catch (error: any) {
		alert(error?.response?.data || 'Failed to issue MCP key.')
	} finally {
		issuing.value = false
	}
}

const test = async () => {
	try {
		testing.value = true
		connectionTestResult.value = null
		const result = await testDbConnection(sqlProvider.value ?? undefined, sqlConnectionString.value ?? undefined)
		connectionTestResult.value = result
	} catch (error: any) {
		connectionTestResult.value = { success: false, errorMessage: error?.response?.data || 'Connection failed.' }
	} finally {
		testing.value = false
	}
}

const revoke = async (id: number) => {
	try {
		await revokeMcpKey(id)
		await load()
	} catch (error: any) {
		alert(error?.response?.data || 'Failed to revoke key.')
	}
}

onMounted(load)
</script>

<template>
	<div class="space-y-4">
		<Card>
			<CardHeader class="border-b bg-muted/40">
				<CardTitle>Issue MCP Access Key</CardTitle>
				<CardDescription>
					New keys are shown only once. Save the value immediately.
				</CardDescription>
			</CardHeader>
			<CardContent>
				<form class="space-y-6 pt-4">
					<FieldGroup class="grid gap-4 md:grid-cols-2">
						<Field>
							<FieldLabel for="name">Name</FieldLabel>
							<Input id="name" v-model="newKeyName" placeholder="Claude Desktop Production" />
						</Field>

						<Field>
							<FieldLabel for="expiresMode">Expires</FieldLabel>
							<Select v-model="expiresMode">
								<SelectTrigger id="expiresMode" class="w-full">
									<SelectValue placeholder="Select expiry" />
								</SelectTrigger>
								<SelectContent>
									<SelectItem value="never">Never</SelectItem>
									<SelectItem value="1">1 day</SelectItem>
									<SelectItem value="7">7 days</SelectItem>
									<SelectItem value="30">30 days</SelectItem>
									<SelectItem value="custom">Custom date/time</SelectItem>
								</SelectContent>
							</Select>
						</Field>

						<Field v-if="expiresMode === 'custom'" class="md:col-span-2">
							<FieldLabel for="customExpiresAt">Custom Expires At</FieldLabel>
							<Input id="customExpiresAt" v-model="customExpiresAt" type="datetime-local" />
						</Field>

						<Field>
							<FieldLabel>SQL Provider Override</FieldLabel>
							<Select v-model="sqlProvider">
								<SelectTrigger class="w-full">
									<SelectValue placeholder="Select provider" />
								</SelectTrigger>
								<SelectContent>
									<SelectItem v-for="provider in providerOptions" :key="provider" :value="provider">
										{{ provider === 'Global' ? 'Global default' : provider }}
									</SelectItem>
								</SelectContent>
							</Select>
							<p class="mt-1 text-xs text-muted-foreground">
								Use Global default, or select a provider together with a connection string.
							</p>
						</Field>

						<Field>
							<FieldLabel>Allowed Tools (multi-select)</FieldLabel>
							<Select v-model="selectedTools" multiple>
								<SelectTrigger class="w-full">
									<SelectValue :placeholder="selectedToolLabel" />
								</SelectTrigger>
								<SelectContent>
									<SelectGroup>
										<SelectLabel>Tools</SelectLabel>
										<SelectItem v-for="tool in toolOptions" :key="tool.value" :value="tool.value" >
											<div class="flex w-full items-center justify-between gap-2">
												<span>{{ tool.label }}</span>
												<span class="text-xs text-muted-foreground" :class="{
													'text-red-500': tool.risk === 'high',
													'text-yellow-500': tool.risk === 'medium',
													'text-emerald-500': tool.risk === 'low'
												}">{{ tool.value }}</span>
											</div>
										</SelectItem>
									</SelectGroup>
								</SelectContent>
							</Select>
						</Field>

						<Field>
							<FieldLabel for="sqlConnectionString">SQL Connection String Override</FieldLabel>
							<Input id="sqlConnectionString" v-model="sqlConnectionString" type="password" placeholder="Host=..." />
							<p class="mt-1 text-xs text-muted-foreground">
								Must be provided together with SQL Provider Override.
							</p>
						</Field>

						<Field class="md:col-span-2">
							<FieldLabel for="corsAllowedOrigins">CORS Allowed Origins</FieldLabel>
							<Input id="corsAllowedOrigins" v-model="corsAllowedOrigins"
								placeholder="https://app.example.com, https://admin.example.com" />
							<p class="mt-1 text-xs text-muted-foreground">
								Comma-separated origins. Leave empty to block browser cross-origin requests for this
								key.
							</p>
						</Field>


					</FieldGroup>
					<span class="item-center flex justify-start gap-2">
						<Button 
							type="button" 
							variant="outline" 
							:disabled="testing" 
							class="w-full md:w-auto flex items-center gap-2" 
							@click.prevent="test"
						>
							<TooltipProvider>
								<Tooltip :disabled="!connectionTestResult || connectionTestResult.success">
									<TooltipTrigger as-child>
									<Badge 
										:class="[
										'h-5 min-w-5 rounded-full px-1 font-mono tabular-nums transition-colors',
										!connectionTestResult ? 'bg-slate-100 text-slate-500' : 
										connectionTestResult.success ? 'bg-green-100 text-green-700 border-green-200' : 
										'bg-red-100 text-red-700 border-red-200'
										]"
									>
										<template v-if="connectionTestResult">
											{{ connectionTestResult.success ? '✓' : '✗' }}
										</template>
										<template v-else>?</template>
									</Badge>
									</TooltipTrigger>
									<TooltipContent class="max-w-[300px] bg-red-950 text-white border-none">
										<p class="font-mono text-xs">{{ connectionTestResult?.errorMessage }}</p>
									</TooltipContent>
								</Tooltip>
							</TooltipProvider>

							<span>{{ testing ? 'Connecting...' : 'Test DB Connection' }}</span>
						</Button>
						<Button type="submit" :disabled="issuing" class="w-full md:w-auto" @click.prevent="issue">
							{{ issuing ? 'Issuing...' : 'Issue Key' }}
						</Button>
					</span>
				</form>

				<div v-if="issuedPlaintextKey" class="mt-4 rounded border border-border bg-muted/40 p-3 text-sm">
					<div class="font-medium">One-time key value</div>
					<div class="mt-1 break-all">{{ issuedPlaintextKey }}</div>
				</div>
			</CardContent>
		</Card>

		<Card>
			<CardHeader class="border-b bg-muted/40">
				<CardTitle>Issued Keys</CardTitle>
			</CardHeader>
			<CardContent>
				<div v-if="loading" class="py-8 text-sm text-muted-foreground">Loading keys...</div>
				<div v-else-if="keys.length === 0" class="py-8 text-sm text-muted-foreground">
					No issued keys yet.
				</div>
				<div v-else class="space-y-2 pt-4">
					<div v-for="key in keys" :key="key.id"
						class="flex flex-col gap-3 rounded-lg border bg-card p-4 shadow-sm md:flex-row md:items-center md:justify-between">
						<div class="text-sm">
							<div class="font-medium">{{ key.name }}</div>
							<div class="text-muted-foreground">Prefix: {{ key.keyPrefix }} | Active: {{ key.isActive ?
								'yes' : 'no' }}</div>
							<div class="text-muted-foreground">Last used: {{ key.lastUsedAt || 'never' }}</div>
							<div class="text-muted-foreground">CORS: {{ key.corsAllowedOrigins || 'none' }}</div>
							<div class="text-muted-foreground">SQL: {{ key.sqlProvider || 'Global' }} / connection: {{
								key.hasSqlConnectionStringOverride ? 'override' : 'Global' }}</div>
						</div>
						<Button variant="destructive" :disabled="!key.isActive" @click="revoke(key.id)">Revoke</Button>
					</div>
				</div>
			</CardContent>
		</Card>
	</div>
</template>
