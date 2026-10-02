<script setup lang="ts">
import {
  NAlert,
  NButton,
  NIcon,
  NModal,
  NPopconfirm,
  NSpin,
  NTable,
  NTag,
  NTooltip,
  NText,
  useDialog,
  useMessage,
} from 'naive-ui'
import { AddOutline, CreateOutline, DocumentTextOutline, SyncOutline, TrashOutline } from '@vicons/ionicons5'
import { inject, onMounted, ref } from 'vue'
import { ApiError, api, syncReportEntries } from '../api/client'
import type { ConfigSummary } from '../api/types'
import ConfigEditorDrawer from './ConfigEditorDrawer.vue'
import LogViewerModal from './LogViewerModal.vue'
import SyncReportView from './SyncReportView.vue'

const message = useMessage()
const dialog = useDialog()
const handleApiError = inject<(err: unknown, fallback: string) => void>('handleApiError')!

const loading = ref(true)
const configs = ref<ConfigSummary[]>([])
const editing = ref<ConfigSummary | 'new' | null>(null)
const syncingName = ref('')
const viewingReport = ref<ConfigSummary | null>(null)
const viewingLogs = ref<ConfigSummary | null>(null)

async function load() {
  loading.value = true
  try {
    configs.value = await api.listConfigs()
  } catch (err) {
    handleApiError(err, 'Failed to load configs')
    message.error(err instanceof ApiError ? err.message : 'Failed to load configs')
  } finally {
    loading.value = false
  }
}

function openNew() {
  editing.value = 'new'
}

function openEdit(config: ConfigSummary) {
  editing.value = config
}

async function remove(config: ConfigSummary) {
  try {
    // DELETE always returns 200 once deleted; a failed post-delete reconcile
    // is reported inside the payload, not via an error status.
    const result = await api.deleteConfig(config.name)
    if (result.report.succeeded) {
      message.success(`Config "${config.name}" deleted.`)
    } else {
      dialog.warning({
        title: `Config "${config.name}" was deleted, but reconcile reported failures`,
        content: () =>
          [result.report.error, ...syncReportEntries(result.report).map((r) => `${r.name}: ${r.error ?? 'unknown error'}`)]
            .filter((line): line is string => Boolean(line))
            .join('\n'),
      })
    }
    await load()
  } catch (err) {
    handleApiError(err, 'Failed to delete config')
    message.error(err instanceof ApiError ? err.message : 'Failed to delete config')
  }
}

async function sync(config: ConfigSummary) {
  syncingName.value = config.name
  try {
    const report = await api.syncConfig(config.name)
    if (report.succeeded) {
      message.success(`Config "${config.name}" synced.`)
    } else {
      const failures = syncReportEntries(report).filter((r) => !r.succeeded)
      dialog.warning({
        title: `Sync of "${config.name}" finished with failures`,
        content: () =>
          [report.error, ...failures.map((r) => `${r.name}: ${r.error ?? 'unknown error'}`)]
            .filter((line): line is string => Boolean(line))
            .join('\n'),
      })
    }
    await load()
  } catch (err) {
    handleApiError(err, 'Failed to sync config')
    message.error(err instanceof ApiError ? err.message : 'Failed to sync config')
  } finally {
    syncingName.value = ''
  }
}

function lockCount(config: ConfigSummary): number {
  return config.lock?.services ? Object.keys(config.lock.services).length : 0
}

function lastSyncOk(config: ConfigSummary): boolean | null {
  return config.lastSync ? config.lastSync.succeeded : null
}

function openReport(config: ConfigSummary) {
  if (config.lastSync) viewingReport.value = config
}

function openLogs(config: ConfigSummary) {
  viewingLogs.value = config
}

onMounted(load)
</script>

<template>
  <div class="config-list">
    <div class="toolbar">
      <n-text depth="3">{{ configs.length }} configuration(s)</n-text>
      <n-button size="small" type="primary" @click="openNew">
        <template #icon><n-icon><AddOutline /></n-icon></template>
        New config
      </n-button>
    </div>

    <n-spin v-if="loading" size="large" class="centered" />

    <n-alert
      v-else-if="!configs.length"
      type="info"
      :bordered="false"
      title="No configurations yet"
    >
      Create a configuration to declare services declaratively. Each config is a YAML
      document listing services, their sources, health checks, and routes.
    </n-alert>

    <n-table v-else size="small" :bordered="false">
      <thead>
        <tr>
          <th>Name</th>
          <th>Services</th>
          <th>Locked</th>
          <th>Last sync</th>
          <th class="actions-col">Actions</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="config in configs" :key="config.name">
          <td>
            <div class="config-title">
              <span class="mono">{{ config.name }}</span>
              <div class="config-badges">
                <n-tooltip v-if="config.serviceScope" placement="top">
                  <template #trigger>
                    <n-tag
                      size="tiny"
                      :type="config.serviceScope === 'global' ? 'info' : 'success'"
                      :bordered="false"
                    >
                      {{ config.serviceScope }}
                    </n-tag>
                  </template>
                  {{
                    config.serviceScope === 'global'
                      ? 'Shared service namespace across config files.'
                      : 'Services are local to this config file.'
                  }}
                </n-tooltip>
                <n-tooltip v-if="config.strictSources === true" placement="top">
                  <template #trigger>
                    <n-tag size="tiny" type="warning" :bordered="false">strict sources</n-tag>
                  </template>
                  Sources must declare a SHA-256 digest.
                </n-tooltip>
                <n-tag size="tiny" :bordered="false">
                  {{ config.services.length }} {{ config.services.length === 1 ? 'service' : 'services' }}
                </n-tag>
              </div>
            </div>
          </td>
          <td>{{ config.services.map((s) => s.name).join(', ') || '—' }}</td>
          <td>{{ lockCount(config) }}</td>
          <td>
            <n-button
              v-if="lastSyncOk(config) !== null"
              quaternary
              size="tiny"
              @click="openReport(config)"
            >
              <n-tag
                size="tiny"
                :type="lastSyncOk(config) ? 'success' : 'error'"
                :bordered="false"
              >
                {{ lastSyncOk(config) ? 'ok' : 'failed' }}
              </n-tag>
            </n-button>
            <n-tag v-else size="tiny" :bordered="false">never</n-tag>
          </td>
          <td class="actions-col">
            <n-button size="tiny" quaternary @click="openLogs(config)">
              <template #icon><n-icon><DocumentTextOutline /></n-icon></template>
              Logs
            </n-button>
            <n-button size="tiny" quaternary @click="openEdit(config)">
              <template #icon><n-icon><CreateOutline /></n-icon></template>
              Edit
            </n-button>
            <n-button
              size="tiny"
              quaternary
              :loading="syncingName === config.name"
              :disabled="syncingName !== ''"
              @click="sync(config)"
            >
              <template #icon><n-icon><SyncOutline /></n-icon></template>
              Sync
            </n-button>
            <n-popconfirm
              positive-text="Delete"
              negative-text="Cancel"
              @positive-click="remove(config)"
            >
              <template #trigger>
                <n-button size="tiny" quaternary type="error">
                  <template #icon><n-icon><TrashOutline /></n-icon></template>
                  Delete
                </n-button>
              </template>
              Delete "{{ config.name }}"? Its services and routes will be removed and the
              data directory cleaned up.
            </n-popconfirm>
          </td>
        </tr>
      </tbody>
    </n-table>

    <ConfigEditorDrawer v-model:editing="editing" :on-saved="load" />
    <LogViewerModal v-model:config="viewingLogs" />
    <n-modal
      :show="viewingReport !== null"
      preset="card"
      :title="viewingReport ? `Sync report for ${viewingReport.name}` : 'Sync report'"
      :style="{ width: '640px', maxWidth: 'calc(100vw - 32px)' }"
      closable
      @update:show="(show: boolean) => !show && (viewingReport = null)"
    >
      <SyncReportView v-if="viewingReport" :report="viewingReport.lastSync" />
    </n-modal>
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
.config-title {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 8px;
}
.config-badges {
  display: inline-flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 4px;
}
.mono {
  font-family: 'SF Mono', Menlo, Consolas, monospace;
}
.actions-col {
  white-space: nowrap;
  text-align: right;
}
</style>
