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
import { issueMcpKey, listMcpKeys, revokeMcpKey } from '@/api/runtime'

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
  permitLimitOverride?: number | null
  windowSecondsOverride?: number | null
  queueLimitOverride?: number | null
}

const keys = ref<McpKeyItem[]>([])
const loading = ref(false)
const issuing = ref(false)
const newKeyName = ref('')
const expiresMode = ref('never')
const customExpiresAt = ref('')
const selectedTools = ref<string[]>([])
const corsAllowedOrigins = ref('')
const sqlProvider = ref('global')
const sqlConnectionString = ref('')
const permitLimitOverride = ref('global')
const windowSecondsOverride = ref('global')
const queueLimitOverride = ref('global')
const issuedPlaintextKey = ref('')

const toolOptions = [
  { label: 'Execute Query', value: 'execute_query_safe' },
  { label: 'Get Columns', value: 'get_columns' },
  { label: 'Get Schemas', value: 'get_schemas' },
  { label: 'Get Tables', value: 'get_tables' },
  { label: 'Get Table Reference', value: 'get_table_reference' },
]

const providerOptions = ['global', 'Sqlite', 'Postgres', 'MySQL']

const permitOptions = ['global', '20', '60', '120', '300', '0']
const windowOptions = ['global', '10', '30', '60', '120', '300', '0']
const queueOptions = ['global', '0', '5', '10', '20', '50']

const selectedToolLabel = computed(() => {
  if (selectedTools.value.length === 0) {
    return 'Global (no restriction)'
  }

  return `${selectedTools.value.length} tools selected`
})

const canSubmitRateOverride = computed(() => {
  const values = [permitLimitOverride.value, windowSecondsOverride.value, queueLimitOverride.value]
  const globals = values.filter((x) => x === 'global').length
  return globals === 0 || globals === 3
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

const mapNumericOverride = (value: string) => {
  return value === 'global' ? null : Number(value)
}

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

  if (!canSubmitRateOverride.value) {
    alert('Rate override must be all Global or all concrete values.')
    return
  }

  issuing.value = true
  try {
    const result = await issueMcpKey({
      name: newKeyName.value.trim(),
      expiresAt: expiresAt.value,
      allowedTools: selectedTools.value.length > 0 ? selectedTools.value.join(',') : null,
      corsAllowedOrigins: corsAllowedOrigins.value.trim() || null,
      sqlProvider: sqlProvider.value === 'global' ? null : sqlProvider.value,
      sqlConnectionString: sqlConnectionString.value.trim() || null,
      permitLimitOverride: mapNumericOverride(permitLimitOverride.value),
      windowSecondsOverride: mapNumericOverride(windowSecondsOverride.value),
      queueLimitOverride: mapNumericOverride(queueLimitOverride.value),
    })

    issuedPlaintextKey.value = result.plaintextKey || ''
    newKeyName.value = ''
    expiresMode.value = 'never'
    customExpiresAt.value = ''
    selectedTools.value = []
    corsAllowedOrigins.value = ''
    sqlProvider.value = 'global'
    sqlConnectionString.value = ''
    permitLimitOverride.value = 'global'
    windowSecondsOverride.value = 'global'
    queueLimitOverride.value = 'global'
    await load()
  } catch (error: any) {
    alert(error?.response?.data || 'Failed to issue MCP key.')
  } finally {
    issuing.value = false
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
                    {{ provider === 'global' ? 'Global default' : provider }}
                  </SelectItem>
                </SelectContent>
              </Select>
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
                    <SelectItem v-for="tool in toolOptions" :key="tool.value" :value="tool.value">
                      <div class="flex w-full items-center justify-between gap-2">
                        <span>{{ tool.label }}</span>
                        <span class="text-xs text-muted-foreground">{{ tool.value }}</span>
                      </div>
                    </SelectItem>
                  </SelectGroup>
                </SelectContent>
              </Select>
            </Field>

            <Field>
              <FieldLabel for="sqlConnectionString">SQL Connection String Override</FieldLabel>
              <Input id="sqlConnectionString" v-model="sqlConnectionString" type="password" placeholder="Host=..." />
            </Field>

            <Field class="md:col-span-2">
              <FieldLabel for="corsAllowedOrigins">CORS Allowed Origins</FieldLabel>
              <Input
                id="corsAllowedOrigins"
                v-model="corsAllowedOrigins"
                placeholder="https://app.example.com, https://admin.example.com"
              />
              <p class="mt-1 text-xs text-muted-foreground">
                Comma-separated origins. Leave empty to block browser cross-origin requests for this key.
              </p>
            </Field>

            <Field>
              <FieldLabel>Permit Limit Override</FieldLabel>
              <Select v-model="permitLimitOverride">
                <SelectTrigger class="w-full">
                  <SelectValue placeholder="Permit override" />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem v-for="opt in permitOptions" :key="`permit-${opt}`" :value="opt">
                    {{ opt === 'global' ? 'Global default' : opt }}
                  </SelectItem>
                </SelectContent>
              </Select>
            </Field>

            <Field>
              <FieldLabel>Window Seconds Override</FieldLabel>
              <Select v-model="windowSecondsOverride">
                <SelectTrigger class="w-full">
                  <SelectValue placeholder="Window override" />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem v-for="opt in windowOptions" :key="`window-${opt}`" :value="opt">
                    {{ opt === 'global' ? 'Global default' : opt }}
                  </SelectItem>
                </SelectContent>
              </Select>
            </Field>

            <Field>
              <FieldLabel>Queue Limit Override</FieldLabel>
              <Select v-model="queueLimitOverride">
                <SelectTrigger class="w-full">
                  <SelectValue placeholder="Queue override" />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem v-for="opt in queueOptions" :key="`queue-${opt}`" :value="opt">
                    {{ opt === 'global' ? 'Global default' : opt }}
                  </SelectItem>
                </SelectContent>
              </Select>
            </Field>
          </FieldGroup>

          <p v-if="!canSubmitRateOverride" class="rounded-md border border-border bg-muted/50 p-2 text-xs text-muted-foreground">
            Rate overrides must be either all Global, or all concrete values.
          </p>

          <Button type="submit" :disabled="issuing || !canSubmitRateOverride" class="w-full md:w-auto" @click.prevent="issue">
            {{ issuing ? 'Issuing...' : 'Issue Key' }}
          </Button>
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
          <div
            v-for="key in keys"
            :key="key.id"
            class="flex flex-col gap-3 rounded-lg border bg-card p-4 shadow-sm md:flex-row md:items-center md:justify-between"
          >
            <div class="text-sm">
              <div class="font-medium">{{ key.name }}</div>
              <div class="text-muted-foreground">Prefix: {{ key.keyPrefix }} | Active: {{ key.isActive ? 'yes' : 'no' }}</div>
              <div class="text-muted-foreground">Last used: {{ key.lastUsedAt || 'never' }}</div>
              <div class="text-muted-foreground">CORS: {{ key.corsAllowedOrigins || 'none' }}</div>
              <div class="text-muted-foreground">SQL: {{ key.sqlProvider || 'global' }} / connection: {{ key.hasSqlConnectionStringOverride ? 'override' : 'global' }}</div>
              <div class="text-muted-foreground">Rate: {{ key.permitLimitOverride ?? 'global' }}/{{ key.windowSecondsOverride ?? 'global' }}/{{ key.queueLimitOverride ?? 'global' }}</div>
            </div>
            <Button variant="destructive" :disabled="!key.isActive" @click="revoke(key.id)">Revoke</Button>
          </div>
        </div>
      </CardContent>
    </Card>
  </div>
</template>
