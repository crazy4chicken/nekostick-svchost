<script setup lang="ts">
import { NIcon, NList, NListItem, NTag, NText, useThemeVars } from 'naive-ui'
import { CheckmarkCircleOutline, CloseCircleOutline } from '@vicons/ionicons5'
import { syncReportEntries } from '../api/client'
import type { SyncReport } from '../api/types'

defineProps<{ report: SyncReport | null }>()

const vars = useThemeVars()
</script>

<template>
  <div v-if="report">
    <n-list v-if="syncReportEntries(report).length" size="small" bordered>
      <n-list-item v-for="result in syncReportEntries(report)" :key="result.name">
        <div class="row">
          <n-icon
            size="18"
            :style="{ color: result.succeeded ? vars.successColor : vars.errorColor }"
          >
            <CheckmarkCircleOutline v-if="result.succeeded" />
            <CloseCircleOutline v-else />
          </n-icon>
          <span class="name">{{ result.name }}</span>
          <n-tag v-if="result.succeeded" size="tiny" type="success" :bordered="false">synced</n-tag>
          <n-tag v-else size="tiny" type="error" :bordered="false">failed</n-tag>
          <n-text v-if="!result.succeeded" depth="2" class="reason">
            {{ result.errorKind ? `${result.errorKind}: ` : '' }}{{ result.error ?? 'unknown error' }}
          </n-text>
        </div>
      </n-list-item>
    </n-list>
    <n-text v-else depth="3">No services in this configuration.</n-text>
  </div>
  <n-text v-else depth="3">No sync has run yet.</n-text>
</template>

<style scoped>
.row {
  display: flex;
  align-items: center;
  gap: 8px;
}
.name {
  font-family: 'SF Mono', Menlo, Consolas, monospace;
}
.reason {
  font-size: 12px;
}
</style>
