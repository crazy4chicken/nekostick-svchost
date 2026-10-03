<script setup lang="ts">
import {
  NAlert,
  NButton,
  NIcon,
  NPopconfirm,
  NSpin,
  NTable,
  NTooltip,
  NText,
  useMessage,
} from 'naive-ui'
import { PlayOutline, StopOutline, SyncOutline, WarningOutline } from '@vicons/ionicons5'
import { computed, inject, onMounted, ref } from 'vue'
import { ApiError, api } from '../api/client'
import type { ManagedService } from '../api/types'
import { absoluteTime, relativeTime } from '../format'

const message = useMessage()
const handleApiError = inject<(err: unknown, fallback: string) => void>('handleApiError')!

const loading = ref(true)
const services = ref<ManagedService[]>([])
const pendingAction = ref('')
const syncingName = ref('')

const grouped = computed(() => {
  const byConfig = new Map<string, ManagedService[]>()
  for (const svc of services.value) {
    const list = byConfig.get(svc.config) ?? []
    list.push(svc)
    byConfig.set(svc.config, list)
  }
  return [...byConfig.entries()]
})

async function load() {
  loading.value = true
  try {
    services.value = await api.listServices()
  } catch (err) {
    handleApiError(err, 'Failed to load services')
    message.error(err instanceof ApiError ? err.message : 'Failed to load services')
  } finally {
    loading.value = false
  }
}

// The host may report a literal 'null'/empty value; normalize before display.
function clean(value?: string | null): string {
  const normalized = (value ?? '').trim()
  return normalized === '' || normalized.toLowerCase() === 'null' ? '' : normalized
}

function stateLabel(state?: string): string {
  return clean(state).toLowerCase() || 'unknown'
}

type DotTone = 'ok' | 'warn' | 'err' | 'idle'

function stateTone(state?: string): DotTone {
  switch (stateLabel(state)) {
    case 'running':
    case 'healthy':
    case 'started':
      return 'ok'
    case 'starting':
    case 'stopping':
    case 'pending':
    case 'waiting':
      return 'warn'
    case 'failed':
    case 'unhealthy':
    case 'crashed':
      return 'err'
    default:
      return 'idle'
  }
}

function reconcileTip(svc: ManagedService): string {
  const report = svc.lastReconcile
  if (!report) return ''
  const parts = [
    absoluteTime(report.completedAt),
    report.trigger ? `trigger: ${report.trigger}` : '',
    report.decision ? `decision: ${report.decision}` : '',
  ].filter(Boolean)
  if (report.error) parts.push(`error: ${report.error}`)
  return parts.join('\n')
}

async function action(svc: ManagedService, kind: 'start' | 'stop' | 'restart') {
  pendingAction.value = `${svc.config}/${svc.service}/${kind}`
  try {
    // Reconcile-level failures arrive as HTTP 200 with succeeded:false and the
    // backend's message (e.g. 'disable phase failed').
    const res = await api.serviceAction(svc.config, svc.service, kind)
    const suffix = res.asynchronous ? ' The supervisor applies it asynchronously.' : ''
    if (!res.succeeded) {
      message.error(res.message || `${kind} of "${svc.service}" failed.`)
    } else if (kind === 'restart') {
      message.info(
        `${res.message || `Restart of "${svc.service}" requested.`} The platform has no global restart, so this is a disable + reconcile + enable + reconcile; it completes asynchronously.`,
      )
    } else {
      message.success(`${res.message || `${kind} of "${svc.service}" requested.`}${suffix}`)
    }
    await load()
  } catch (err) {
    handleApiError(err, `Failed to ${kind} service`)
    message.error(err instanceof ApiError ? err.message : `Failed to ${kind} service`)
  } finally {
    pendingAction.value = ''
  }
}

async function syncConfig(config: string) {
  syncingName.value = config
  try {
    await api.syncConfig(config)
    message.success(`Config "${config}" synced.`)
    await load()
  } catch (err) {
    handleApiError(err, 'Failed to sync config')
    message.error(err instanceof ApiError ? err.message : 'Failed to sync config')
  } finally {
    syncingName.value = ''
  }
}

onMounted(load)
</script>

<template>
  <div class="services">
    <div class="toolbar">
      <n-text depth="3">{{ services.length }} managed service(s)</n-text>
      <n-button size="small" quaternary :loading="loading" @click="load">Refresh</n-button>
    </div>

    <n-spin v-if="loading" size="large" class="centered" />

    <n-alert
      v-else-if="!services.length"
      type="info"
      :bordered="false"
      title="No managed services"
    >
      Services appear here once a configuration declaring them is saved and synced.
    </n-alert>

    <template v-else>
      <section v-for="[config, svcs] in grouped" :key="config" class="config-group">
        <header class="group-header">
          <div class="group-title">
            <span class="mono">{{ config }}</span>
            <n-text depth="3" class="group-count">{{ svcs.length }} service(s)</n-text>
          </div>
          <n-button
            size="tiny"
            quaternary
            :loading="syncingName === config"
            :disabled="syncingName !== ''"
            @click="syncConfig(config)"
          >
            <template #icon><n-icon><SyncOutline /></n-icon></template>
            Sync
          </n-button>
        </header>
        <div class="group-card">
          <n-table size="small" :bordered="false">
            <thead>
              <tr>
                <th>Service</th>
                <th>State</th>
                <th>Failures</th>
                <th>Last reconcile</th>
                <th>Desired</th>
                <th class="actions-col">Actions</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="svc in svcs" :key="`${svc.config}/${svc.service}`">
                <td class="mono service-name">{{ svc.service }}</td>
                <td>
                  <span class="state-cell">
                    <span class="state-dot" :data-tone="stateTone(svc.state)" />
                    <span>{{ stateLabel(svc.state) }}</span>
                    <n-text v-if="clean(svc.detail)" depth="3" class="detail">{{ clean(svc.detail) }}</n-text>
                  </span>
                </td>
                <td>
                  <n-tag
                    v-if="(svc.consecutiveFailures ?? 0) > 0"
                    size="tiny"
                    type="error"
                    :bordered="false"
                  >
                    ×{{ svc.consecutiveFailures }}
                  </n-tag>
                  <n-text v-else depth="3">—</n-text>
                </td>
                <td>
                  <template v-if="svc.lastReconcile">
                    <n-tooltip :delay="300">
                      <template #trigger>
                        <span
                          class="reconcile-cell"
                          :class="{ failed: !svc.lastReconcile.succeeded }"
                        >
                          {{ relativeTime(svc.lastReconcile.completedAt) }}
                          <n-icon v-if="!svc.lastReconcile.succeeded" class="cell-icon">
                            <WarningOutline />
                          </n-icon>
                        </span>
                      </template>
                      <span style="white-space: pre-line">{{ reconcileTip(svc) }}</span>
                    </n-tooltip>
                    <n-tooltip v-if="svc.driftCorrected" :delay="300">
                      <template #trigger>
                        <n-tag size="tiny" type="warning" :bordered="false" class="drift-tag">
                          drift
                        </n-tag>
                      </template>
                      External drift was corrected by the last reconcile.
                    </n-tooltip>
                  </template>
                  <n-text v-else depth="3">—</n-text>
                </td>
                <td>
                  <n-tag v-if="svc.enabled" size="tiny" type="success" :bordered="false">
                    running
                  </n-tag>
                  <n-tag v-else size="tiny" :bordered="false">stopped</n-tag>
                </td>
                <td class="actions-col">
                  <n-popconfirm
                    v-if="svc.enabled === false"
                    positive-text="Start"
                    negative-text="Cancel"
                    @positive-click="action(svc, 'start')"
                  >
                    <template #trigger>
                      <n-button size="tiny" quaternary type="success">
                        <template #icon><n-icon><PlayOutline /></n-icon></template>
                        Start
                      </n-button>
                    </template>
                    Start "{{ svc.service }}"? The supervisor starts and health-checks a new
                    instance before switching traffic.
                  </n-popconfirm>
                  <n-popconfirm
                    v-else
                    positive-text="Stop"
                    negative-text="Cancel"
                    @positive-click="action(svc, 'stop')"
                  >
                    <template #trigger>
                      <n-button size="tiny" quaternary type="warning">
                        <template #icon><n-icon><StopOutline /></n-icon></template>
                        Stop
                      </n-button>
                    </template>
                    Stop "{{ svc.service }}"? Its routes will stop receiving traffic.
                  </n-popconfirm>
                  <n-popconfirm
                    positive-text="Restart"
                    negative-text="Cancel"
                    @positive-click="action(svc, 'restart')"
                  >
                    <template #trigger>
                      <n-button
                        size="tiny"
                        quaternary
                        :loading="pendingAction === `${svc.config}/${svc.service}/restart`"
                        :disabled="pendingAction !== ''"
                      >
                        Restart
                      </n-button>
                    </template>
                    Restart "{{ svc.service }}"? Traffic stops for a few seconds while the
                    service is disabled, reconciled, re-enabled, and reconciled again.
                  </n-popconfirm>
                </td>
              </tr>
            </tbody>
          </n-table>
        </div>
      </section>
    </template>
  </div>
</template>

<style scoped>
.toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin: 16px 0 20px;
}
.centered {
  display: block;
  margin: 64px auto;
}
.config-group {
  margin-bottom: 28px;
}
.group-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 8px;
  padding: 0 2px;
}
.group-title {
  display: flex;
  align-items: baseline;
  gap: 10px;
  font-weight: 600;
}
.group-count {
  font-size: 12px;
  font-weight: 400;
}
.group-card {
  border: 1px solid rgba(128, 128, 128, 0.18);
  border-radius: 10px;
  overflow: hidden;
}
.mono {
  font-family: ui-monospace, 'SF Mono', Menlo, Consolas, monospace;
}
.service-name {
  font-weight: 550;
}
.state-cell {
  display: inline-flex;
  align-items: center;
  gap: 7px;
  text-transform: capitalize;
}
.state-dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  flex-shrink: 0;
}
.state-dot[data-tone='ok'] {
  background: #3ecf8e;
  box-shadow: 0 0 6px rgba(62, 207, 142, 0.55);
}
.state-dot[data-tone='warn'] {
  background: #e0a83e;
}
.state-dot[data-tone='err'] {
  background: #e05d6f;
  box-shadow: 0 0 6px rgba(224, 93, 111, 0.5);
}
.state-dot[data-tone='idle'] {
  background: rgba(128, 128, 128, 0.55);
}
.detail {
  font-size: 12px;
  text-transform: none;
}
.reconcile-cell {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  cursor: default;
}
.reconcile-cell.failed {
  color: #e05d6f;
}
.cell-icon {
  vertical-align: -2px;
}
.drift-tag {
  margin-left: 6px;
}
.actions-col {
  white-space: nowrap;
  text-align: right;
}
</style>
