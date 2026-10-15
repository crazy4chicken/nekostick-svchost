<script setup lang="ts">
import { NButton, NCard, NInput, NInputNumber, NText } from 'naive-ui'
import { computed } from 'vue'
import type { GitHubAppSettings, ReleaseProvidersSettings } from '../api/types'

const REDACTED_PRIVATE_KEY = '********'

const props = defineProps<{ modelValue: ReleaseProvidersSettings }>()
const emit = defineEmits<{
  'update:modelValue': [value: ReleaseProvidersSettings]
}>()

const mirrors = computed(() => props.modelValue.github?.mirrors ?? [])
const app = computed(() => props.modelValue.github?.app)

function updateGitHub(patch: Partial<{ mirrors: string[]; app: GitHubAppSettings | undefined }>) {
  const github = props.modelValue.github ?? { mirrors: [] }
  emit('update:modelValue', {
    ...props.modelValue,
    github: { ...github, ...patch },
  })
}

function updateMirrors(next: string[]) {
  updateGitHub({ mirrors: next })
}

function updateMirror(index: number, value: string) {
  const next = [...mirrors.value]
  next[index] = value
  updateMirrors(next)
}

function addMirror() {
  updateMirrors([...mirrors.value, ''])
}

function removeMirror(index: number) {
  updateMirrors(mirrors.value.filter((_, mirrorIndex) => mirrorIndex !== index))
}

function updateApp(patch: Partial<GitHubAppSettings>) {
  const current = app.value ?? { appId: 0 }
  updateGitHub({ app: { ...current, ...patch } })
}

function removeApp() {
  updateGitHub({ app: undefined })
}

const appConfigured = computed(() => app.value !== undefined)
const privateKeyRedacted = computed(() => app.value?.privateKey === REDACTED_PRIVATE_KEY)
</script>

<template>
  <n-card size="small" title="GitHub">
    <n-text depth="3" class="helper">
      Mirrors are URL prefixes tried in order before the official address.
    </n-text>
    <div class="mirror-list">
      <div v-for="(mirror, index) in mirrors" :key="index" class="mirror-row">
        <n-input
          :value="mirror"
          type="text"
          placeholder="https://ghproxy.net/"
          @update:value="updateMirror(index, $event)"
        />
        <n-button
          size="small"
          quaternary
          type="error"
          :aria-label="`Remove mirror ${index + 1}`"
          @click="removeMirror(index)"
        >
          Remove
        </n-button>
      </div>
      <n-text v-if="!mirrors.length" depth="3">No mirrors configured.</n-text>
      <n-button size="small" secondary @click="addMirror">Add mirror</n-button>
    </div>

    <n-text depth="3" class="helper app-helper">
      GitHub App credentials enable downloads from private repositories. The App needs the
      'Contents: read' permission and must be installed on the target repositories.
    </n-text>
    <div v-if="appConfigured" class="app-form">
      <div class="app-row">
        <n-text class="app-label">App ID</n-text>
        <n-input-number
          :value="app!.appId || null"
          :min="1"
          :show-button="false"
          placeholder="123456"
          @update:value="updateApp({ appId: $event ?? 0 })"
        />
      </div>
      <div class="app-row">
        <n-text class="app-label">Installation ID</n-text>
        <n-input-number
          :value="app!.installationId ?? null"
          :min="1"
          :show-button="false"
          clearable
          placeholder="Auto-resolve per repository"
          @update:value="updateApp({ installationId: $event ?? undefined })"
        />
      </div>
      <div class="app-row">
        <n-text class="app-label">Private key (PEM)</n-text>
        <n-input
          :value="privateKeyRedacted ? '' : (app!.privateKey ?? '')"
          type="textarea"
          :autosize="{ minRows: 3, maxRows: 8 }"
          :placeholder="
            privateKeyRedacted
              ? 'A key is stored. Paste a new PEM to replace it, or leave empty to keep it.'
              : '-----BEGIN PRIVATE KEY-----'
          "
          @update:value="updateApp({ privateKey: $event === '' && privateKeyRedacted ? REDACTED_PRIVATE_KEY : $event })"
        />
      </div>
      <n-button size="small" quaternary type="error" @click="removeApp">
        Remove GitHub App credentials
      </n-button>
    </div>
    <n-button v-else size="small" secondary class="app-helper" @click="updateApp({ appId: 0, privateKey: '' })">
      Configure GitHub App
    </n-button>
  </n-card>
</template>

<style scoped>
.helper {
  display: block;
  margin-bottom: 12px;
}
.mirror-list {
  display: flex;
  flex-direction: column;
  gap: 8px;
}
.mirror-row {
  display: flex;
  align-items: center;
  gap: 8px;
}
.mirror-row :deep(.n-input) {
  flex: 1;
}
.app-helper {
  margin-top: 16px;
}
.app-form {
  display: flex;
  flex-direction: column;
  gap: 8px;
}
.app-row {
  display: flex;
  align-items: flex-start;
  gap: 8px;
}
.app-label {
  width: 140px;
  line-height: 34px;
  flex-shrink: 0;
}
.app-row :deep(.n-input),
.app-row :deep(.n-input-number) {
  flex: 1;
}
</style>
