<script setup lang="ts">
import { NButton, NButtonGroup, NIcon, NResult, NSpin, NTabPane, NTabs, NTag, NTooltip } from 'naive-ui'
import { DesktopOutline, MoonOutline, RefreshOutline, SunnyOutline } from '@vicons/ionicons5'
import { onMounted, provide, ref } from 'vue'
import { ApiError, api, clearStoredKey, getStoredKey, setStoredKey } from '../api/client'
import type { StatusResponse } from '../api/types'
import { setThemeMode, themeMode, type ThemeMode } from '../theme'
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

const themeOptions: { mode: ThemeMode; label: string; icon: typeof SunnyOutline }[] = [
  { mode: 'light', label: 'Light theme', icon: SunnyOutline },
  { mode: 'dark', label: 'Dark theme', icon: MoonOutline },
  { mode: 'system', label: 'Follow system theme', icon: DesktopOutline },
]

onMounted(boot)
</script>

<template>
  <div class="shell">
    <header class="shell-header">
      <div class="shell-header-inner">
        <div class="shell-title">
          <span class="brand-mark" />
          <span class="brand-name">nekostick <em>svchost</em></span>
          <n-tag v-if="status" size="tiny" :bordered="false" class="version-tag">
            v{{ status.version }}
          </n-tag>
        </div>
        <div class="header-actions">
          <n-button-group size="small" class="theme-switch">
            <n-tooltip v-for="option in themeOptions" :key="option.mode" :delay="300">
              <template #trigger>
                <n-button
                  quaternary
                  :type="themeMode === option.mode ? 'primary' : 'default'"
                  @click="setThemeMode(option.mode)"
                >
                  <template #icon><n-icon><component :is="option.icon" /></n-icon></template>
                </n-button>
              </template>
              {{ option.label }}
            </n-tooltip>
          </n-button-group>
          <n-button v-if="phase === 'main'" size="small" quaternary @click="boot">
            <template #icon><n-icon><RefreshOutline /></n-icon></template>
            Re-check status
          </n-button>
        </div>
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
        <n-tabs v-model:value="activeTab" type="line" class="tabs" animated>
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
  min-height: 100%;
}
.shell-header {
  position: sticky;
  top: 0;
  z-index: 10;
  border-bottom: 1px solid rgba(128, 128, 128, 0.18);
  background: rgba(128, 128, 128, 0.06);
  backdrop-filter: blur(12px);
  -webkit-backdrop-filter: blur(12px);
}
.shell-header-inner {
  max-width: 1120px;
  margin: 0 auto;
  padding: 0 24px;
  height: 56px;
  display: flex;
  align-items: center;
  justify-content: space-between;
}
.shell-title {
  display: flex;
  align-items: center;
  gap: 10px;
}
.brand-mark {
  width: 14px;
  height: 14px;
  border-radius: 5px;
  background: linear-gradient(135deg, #47cd9a, #1d9e6d);
  box-shadow: 0 0 0 3px rgba(62, 207, 142, 0.18);
}
.brand-name {
  font-size: 15px;
  font-weight: 650;
  letter-spacing: 0.01em;
}
.brand-name em {
  font-style: normal;
  opacity: 0.55;
  font-weight: 550;
}
.version-tag {
  margin-left: 2px;
  opacity: 0.75;
}
.header-actions {
  display: flex;
  align-items: center;
  gap: 12px;
}
.theme-switch {
  opacity: 0.9;
}
.shell-body {
  position: relative;
  max-width: 1120px;
  margin: 0 auto;
  padding: 8px 24px 64px;
}
.centered {
  display: block;
  margin: 120px auto;
}
.tabs {
  margin-top: 8px;
}
</style>
