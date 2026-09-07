<script setup lang="ts">
import {
  NAlert,
  NButton,
  NIcon,
  NPopconfirm,
  NSpin,
  NTable,
  NTag,
  NText,
  useMessage,
} from 'naive-ui'
import { PlayOutline, StopOutline, SyncOutline } from '@vicons/ionicons5'
import { computed, inject, onMounted, ref } from 'vue'
import { ApiError, api } from '../api/client'
import type { ManagedService } from '../api/types'

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

function stateType(state?: string): 'success' | 'warning' | 'error' | 'default' {
  switch ((state ?? '').toLowerCase()) {
    case 'running':
    case 'healthy':
    case 'started':
      return 'success'
    case 'starting':
    case 'stopping':
    case 'pending':
      return 'warning'
    case 'failed':
    case 'unhealthy':
    case 'crashed':
      return 'error'
    default:
      return 'default'
  }
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
      <div class="group-header">
        <n-text strong class="mono">{{ config }}</n-text>
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
      </div>
      <n-table size="small" :bordered="false">
        <thead>
          <tr>
            <th>Service</th>
            <th>State</th>
            <th>Enabled</th>
            <th class="actions-col">Actions</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="svc in svcs" :key="`${svc.config}/${svc.service}`">
            <td class="mono">{{ svc.service }}</td>
            <td>
              <n-tag size="tiny" :type="stateType(svc.state)" :bordered="false">
                {{ svc.state ?? 'unknown' }}
              </n-tag>
              <n-text v-if="svc.detail" depth="3" class="detail"> {{ svc.detail }}</n-text>
            </td>
            <td>
              <n-tag v-if="svc.enabled" size="tiny" type="info" :bordered="false">yes</n-tag>
              <n-tag v-else size="tiny" :bordered="false">no</n-tag>
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
      </section>
    </template>
  </div>
</template>

<style scoped>
.toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin: 12px 0;
}
.centered {
  display: block;
  margin: 64px auto;
}
.config-group {
  margin-bottom: 24px;
}
.group-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 8px;
}
.mono {
  font-family: 'SF Mono', Menlo, Consolas, monospace;
}
.actions-col {
  white-space: nowrap;
  text-align: right;
}
.detail {
  font-size: 12px;
}
</style>
