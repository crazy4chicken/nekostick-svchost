<script setup lang="ts">
import { NButton, NCard, NInput, NText } from 'naive-ui'
import { computed } from 'vue'
import type { ReleaseProvidersSettings } from '../api/types'

const props = defineProps<{ modelValue: ReleaseProvidersSettings }>()
const emit = defineEmits<{
  'update:modelValue': [value: ReleaseProvidersSettings]
}>()

const mirrors = computed(() => props.modelValue.github?.mirrors ?? [])

function updateMirrors(next: string[]) {
  const github = props.modelValue.github ?? { mirrors: [] }
  emit('update:modelValue', {
    ...props.modelValue,
    github: { ...github, mirrors: next },
  })
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
</style>
