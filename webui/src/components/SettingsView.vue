<script setup lang="ts">
import { NAlert, NButton, NCard, NSpin, NText, useMessage } from 'naive-ui'
import type { Component } from 'vue'
import { inject, onMounted, ref } from 'vue'
import { ApiError, api } from '../api/client'
import type { SettingsResponse, SettingsUpdate } from '../api/types'
import ReleaseProvidersEditor from './ReleaseProvidersEditor.vue'

interface SettingsGroupDescriptor {
  key: string
  title: string
  description: string
  editor: Component
}

const settingsGroups: SettingsGroupDescriptor[] = [
  {
    key: 'releaseProviders',
    title: 'Release Providers',
    description: 'Configure release download options by provider.',
    editor: ReleaseProvidersEditor,
  },
]

const message = useMessage()
const handleApiError = inject<(err: unknown, fallback: string) => void>('handleApiError')!
const settings = ref<SettingsResponse>({ releaseProviders: {} })
const loading = ref(true)
const loadError = ref('')
const savingGroup = ref<string | null>(null)

async function load() {
  loading.value = true
  loadError.value = ''
  try {
    settings.value = await api.getSettings()
  } catch (err) {
    loadError.value = err instanceof ApiError ? err.message : 'Failed to load settings'
    handleApiError(err, 'Failed to load settings')
    message.error(loadError.value)
  } finally {
    loading.value = false
  }
}

function updateGroup(key: string, value: unknown) {
  settings.value[key] = value
}

async function saveGroup(group: SettingsGroupDescriptor) {
  savingGroup.value = group.key
  try {
    const payload = { [group.key]: settings.value[group.key] } as SettingsUpdate
    const saved = await api.putSettings(payload)
    Object.assign(settings.value, { [group.key]: saved[group.key] })
    message.success(`${group.title} settings saved.`)
  } catch (err) {
    const fallback = `Failed to save ${group.title.toLowerCase()} settings`
    handleApiError(err, fallback)
    message.error(err instanceof ApiError ? err.message : fallback)
  } finally {
    savingGroup.value = null
  }
}

onMounted(load)
</script>

<template>
  <div class="settings-view">
    <n-spin v-if="loading" size="large" class="centered" />
    <n-alert v-else-if="loadError" type="error" :bordered="false" title="Settings could not be loaded">
      {{ loadError }}
      <template #action>
        <n-button size="small" @click="load">Retry</n-button>
      </template>
    </n-alert>
    <div v-else class="settings-groups">
      <n-card v-for="group in settingsGroups" :key="group.key" :title="group.title">
        <n-text depth="3" class="group-description">{{ group.description }}</n-text>
        <component
          :is="group.editor"
          :model-value="settings[group.key]"
          @update:model-value="updateGroup(group.key, $event)"
        />
        <div class="actions">
          <n-button
            type="primary"
            :loading="savingGroup === group.key"
            :disabled="savingGroup !== null"
            @click="saveGroup(group)"
          >
            Save
          </n-button>
        </div>
      </n-card>
    </div>
  </div>
</template>

<style scoped>
.settings-groups {
  display: flex;
  flex-direction: column;
  gap: 16px;
  margin-top: 12px;
}
.group-description {
  display: block;
  margin-bottom: 12px;
}
.actions {
  display: flex;
  justify-content: flex-end;
  margin-top: 16px;
}
.centered {
  display: block;
  margin: 64px auto;
}
</style>
