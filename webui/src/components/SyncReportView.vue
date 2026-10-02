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
    <n-text
      v-if="report.succeeded === false && report.error"
      class="reason run-error"
      :style="{ color: vars.errorColor }"
    >
      {{ report.errorKind ? `${report.errorKind}: ` : '' }}{{ report.error }}
    </n-text>
    <n-list v-if="syncReportEntries(report).length" size="small" bordered>
      <n-list-item v-for="result in syncReportEntries(report)" :key="result.name">
        <div class="service-result">
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
          <div v-if="result.warnings.length" class="warnings">
            <n-text
              v-for="(warning, index) in result.warnings"
              :key="`${index}-${warning}`"
              type="warning"
              class="warning"
            >
              {{ warning }}
            </n-text>
          </div>
        </div>
      </n-list-item>
    </n-list>
    <n-text v-else-if="report.succeeded || !report.error" depth="3">No services in this configuration.</n-text>
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
.run-error {
  display: block;
  margin-bottom: 8px;
}
.service-result {
  width: 100%;
}
.warnings {
  display: flex;
  flex-direction: column;
  gap: 4px;
  margin: 6px 0 0 26px;
}
.warning {
  font-size: 12px;
}
</style>
