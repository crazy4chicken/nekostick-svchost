<script setup lang="ts">
import { NButton, NIcon, NResult, NSpin, NTabPane, NTabs, NTag } from 'naive-ui'
import { KeyOutline, RefreshOutline } from '@vicons/ionicons5'
import { onMounted, provide, ref } from 'vue'
import { ApiError, api, clearStoredKey, getStoredKey, setStoredKey } from '../api/client'
import type { StatusResponse } from '../api/types'
import BootstrapSetup from './BootstrapSetup.vue'
import ConfigList from './ConfigList.vue'
import KeyGate from './KeyGate.vue'
import ServicesDashboard from './ServicesDashboard.vue'
import SettingsView from './SettingsView.vue'

type Phase = 'loading' | 'bootstrap' | 'gate' | 'main' | 'unreachable'

const phase = ref<Phase>('loading')
const status = ref<StatusResponse | null>(null)
const gateError = ref('')
const activeTab = ref('services')

// Children surface API failures through this handler; a 401 sends the user
// back to the key gate.
provide('handleApiError', (err: unknown, fallback: string) => {
  if (err instanceof ApiError && err.status === 401) {
    clearStoredKey()
    gateError.value = 'The stored API key was rejected. Enter a valid key.'
    phase.value = 'gate'
    return
  }
  gateError.value = ''
  console.error(fallback, err)
})

async function boot() {
  phase.value = 'loading'
  let st: StatusResponse
  try {
    st = await api.getStatus()
  } catch {
    phase.value = 'unreachable'
    return
  }
  status.value = st
  if (st.bootstrap) {
    phase.value = 'bootstrap'
  } else if (!getStoredKey()) {
    phase.value = 'gate'
  } else {
    phase.value = 'main'
  }
}

const gatePending = ref(false)

async function enterKey(key: string) {
  gateError.value = ''
  gatePending.value = true
  setStoredKey(key)
  try {
    // Verify against an authenticated endpoint: /status bypasses auth, so it
    // would accept any key and only fail later inside the main view.
    await api.listConfigs()
    status.value = await api.getStatus()
    phase.value = 'main'
  } catch (err) {
    clearStoredKey()
    if (err instanceof ApiError && err.status === 401) {
      gateError.value = 'That key was rejected (401). Check the key and try again.'
    } else {
      gateError.value = err instanceof Error ? err.message : 'Failed to contact the API.'
    }
  } finally {
    gatePending.value = false
  }
}

onMounted(boot)
</script>

<template>
  <div class="shell">
    <header class="shell-header">
      <div class="shell-title">
        <n-icon size="20"><KeyOutline /></n-icon>
        <span>nekostick svchost</span>
        <n-tag v-if="status" size="tiny" :bordered="false" type="info">
          {{ status.version }}
        </n-tag>
      </div>
      <div class="header-actions">
        <n-button v-if="phase === 'main'" size="small" quaternary @click="boot">
          <template #icon><n-icon><RefreshOutline /></n-icon></template>
          Re-check status
        </n-button>
      </div>
    </header>

    <main class="shell-body">
      <n-spin v-if="phase === 'loading'" size="large" class="centered" />

      <n-result
        v-else-if="phase === 'unreachable'"
        status="error"
        title="Management API unreachable"
        description="Could not reach /svchost/api/status. Is the svchost extension loaded on this host?"
      >
        <template #footer>
          <n-button @click="boot">Retry</n-button>
        </template>
      </n-result>

      <BootstrapSetup v-else-if="phase === 'bootstrap'" @done="boot" />

      <KeyGate v-else-if="phase === 'gate'" :error="gateError" :loading="gatePending" @submit="enterKey" />

      <template v-else>
        <n-tabs v-model:value="activeTab" type="segment" class="tabs">
            <n-tab-pane name="services" tab="Services">
            <ServicesDashboard />
          </n-tab-pane>
          <n-tab-pane name="configs" tab="Configs">
            <ConfigList />
          </n-tab-pane>
          <n-tab-pane name="settings" tab="Settings">
            <SettingsView />
          </n-tab-pane>
        </n-tabs>
      </template>
    </main>
  </div>
</template>

<style scoped>
.shell {
  max-width: 1080px;
  margin: 0 auto;
  padding: 0 20px 48px;
  min-height: 100%;
}
.shell-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 18px 0;
}
.shell-title {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 17px;
  font-weight: 600;
}
.header-actions {
  display: flex;
  align-items: center;
  gap: 8px;
}
.shell-body {
  position: relative;
}
.centered {
  display: block;
  margin: 120px auto;
}
.tabs {
  margin-top: 4px;
}
</style>
