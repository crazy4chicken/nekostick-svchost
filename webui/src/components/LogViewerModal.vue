<script setup lang="ts">
import { NButton, NModal, NSelect, NText, useThemeVars } from 'naive-ui'
import { computed, defineComponent, h, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import type { PropType, VNodeChild } from 'vue'
import { ApiError, api } from '../api/client'
import type { ConfigSummary, LogPage } from '../api/types'

const props = defineProps<{ config: ConfigSummary | null }>()
const emit = defineEmits<{
  'update:config': [config: ConfigSummary | null]
}>()

const vars = useThemeVars()
const serviceOptions = computed(
  () => props.config?.services.map((service) => ({ label: service.name, value: service.name })) ?? [],
)
const selectedService = ref<string | null>(null)

type LogEntry = { id: number; text: string }

const lines = ref<LogEntry[]>([])
const fileCount = ref(0)
const oldestFile = ref(0)
const loadingInitial = ref(false)
const loadingOlder = ref(false)
const historyError = ref('')
const showBackToBottom = ref(false)
const stickToBottom = ref(true)
const logViewport = ref<HTMLElement | null>(null)

let nextLogId = 0
let sessionVersion = 0
let sessionController: AbortController | null = null

function entriesFrom(rawLines: string[]): LogEntry[] {
  return rawLines.map((text) => ({ id: nextLogId++, text }))
}

function linkify(text: string): VNodeChild[] {
  const children: VNodeChild[] = []
  const pattern = /https?:\/\/[^\s"'<>)]+/g
  let lastIndex = 0
  let found = false
  let match: RegExpExecArray | null

  while ((match = pattern.exec(text)) !== null) {
    found = true
    if (match.index > lastIndex) children.push(text.slice(lastIndex, match.index))
    const url = match[0]
    children.push(
      h(
        'a',
        {
          href: url,
          target: '_blank',
          rel: 'noopener noreferrer',
          class: 'log-link',
          style: { color: vars.value.infoColor },
        },
        url,
      ),
    )
    lastIndex = pattern.lastIndex
  }

  if (!found) return [text]
  if (lastIndex < text.length) children.push(text.slice(lastIndex))
  return children
}

function renderLogLine(text: string, id: number) {
  const match = /^(\S+) \[(stdout|stderr|svchost)\] (.*)$/.exec(text)
  if (!match) {
    return h(
      'div',
      { class: 'log-line', key: id },
      [h('span', { class: 'message', style: { color: vars.value.textColor1 } }, linkify(text))],
    )
  }

  const [, timestamp, stream, message] = match
  const streamColor =
    stream === 'stdout'
      ? vars.value.successColor
      : stream === 'stderr'
        ? vars.value.errorColor
        : vars.value.warningColor
  return h('div', { class: 'log-line', key: id }, [
    h('span', { class: 'timestamp', style: { color: vars.value.infoColor } }, `${timestamp} `),
    h('span', { class: 'stream', style: { color: streamColor } }, `[${stream}] `),
    h('span', { class: 'message', style: { color: vars.value.textColor1 } }, linkify(message)),
  ])
}

const LogLines = defineComponent({
  props: {
    entries: { type: Array as PropType<LogEntry[]>, required: true },
  },
  setup(componentProps) {
    return () =>
      h(
        'div',
        { class: 'log-lines' },
        componentProps.entries.map((entry) => renderLogLine(entry.text, entry.id)),
      )
  },
})

function isCurrentSession(controller: AbortController, version: number): boolean {
  return (
    sessionController === controller &&
    !controller.signal.aborted &&
    sessionVersion === version
  )
}

function cancelSession() {
  sessionVersion += 1
  sessionController?.abort()
  sessionController = null
}

function resetLogState() {
  lines.value = []
  fileCount.value = 0
  oldestFile.value = 0
  loadingInitial.value = false
  loadingOlder.value = false
  historyError.value = ''
  showBackToBottom.value = false
  stickToBottom.value = true
}

function startSession() {
  cancelSession()
  resetLogState()

  const config = props.config
  const service = selectedService.value
  if (!config || !service) return

  const controller = new AbortController()
  sessionController = controller
  const version = sessionVersion
  loadingInitial.value = true
  void loadSession(config.name, service, controller, version)
}

async function loadSession(
  config: string,
  service: string,
  controller: AbortController,
  version: number,
) {
  let fromLine = 0
  try {
    const page: LogPage = await api.listLogPage(config, service, 0)
    if (!isCurrentSession(controller, version)) return
    lines.value = entriesFrom(page.lines)
    fileCount.value = page.fileCount
    oldestFile.value = 0
    fromLine = page.lineCount
  } catch (err) {
    if (!isCurrentSession(controller, version)) return
    if (err instanceof ApiError && err.status === 404) {
      lines.value = []
      fileCount.value = 0
      oldestFile.value = 0
    } else {
      historyError.value = err instanceof Error ? err.message : 'Failed to load logs.'
    }
  }

  if (!isCurrentSession(controller, version)) return
  loadingInitial.value = false
  await nextTick()
  if (!isCurrentSession(controller, version)) return
  scrollToBottom()
  void streamLive(config, service, fromLine, controller, version)
}

function nearBottom(element: HTMLElement): boolean {
  return element.scrollHeight - element.scrollTop - element.clientHeight <= 40
}

function scrollToBottom() {
  const element = logViewport.value
  if (element) element.scrollTop = element.scrollHeight
  stickToBottom.value = true
  showBackToBottom.value = false
}

function appendLiveLine(line: string) {
  const element = logViewport.value
  const keepAtBottom = !element || nearBottom(element)
  lines.value.push({ id: nextLogId++, text: line })

  if (keepAtBottom) {
    stickToBottom.value = true
    showBackToBottom.value = false
    void nextTick(() => {
      if (stickToBottom.value) scrollToBottom()
    })
  } else {
    stickToBottom.value = false
    showBackToBottom.value = true
  }
}

function onLogScroll() {
  const element = logViewport.value
  if (!element) return

  const atBottom = nearBottom(element)
  stickToBottom.value = atBottom
  showBackToBottom.value = !atBottom && lines.value.length > 0
  if (element.scrollTop <= 40) void loadOlder()
}

async function loadOlder() {
  const config = props.config
  const service = selectedService.value
  const element = logViewport.value
  const controller = sessionController
  const version = sessionVersion
  const file = oldestFile.value + 1
  if (
    !config ||
    !service ||
    !element ||
    !controller ||
    loadingOlder.value ||
    file >= fileCount.value ||
    !isCurrentSession(controller, version)
  ) {
    return
  }

  loadingOlder.value = true
  try {
    const page = await api.listLogPage(config.name, service, file)
    if (!isCurrentSession(controller, version)) return
    const previousScrollHeight = element.scrollHeight
    const previousScrollTop = element.scrollTop
    lines.value = [...entriesFrom(page.lines), ...lines.value]
    oldestFile.value = file
    fileCount.value = page.fileCount
    historyError.value = ''
    await nextTick()
    if (!isCurrentSession(controller, version)) return
    const viewport = logViewport.value
    if (viewport) {
      viewport.scrollTop = previousScrollTop + viewport.scrollHeight - previousScrollHeight
    }
  } catch (err) {
    if (!isCurrentSession(controller, version)) return
    if (err instanceof ApiError && err.status === 404) {
      fileCount.value = file
    } else {
      historyError.value = err instanceof Error ? err.message : 'Failed to load older logs.'
    }
  } finally {
    if (isCurrentSession(controller, version)) loadingOlder.value = false
  }
}

function waitForRetry(milliseconds: number, signal: AbortSignal): Promise<void> {
  return new Promise<void>((resolve) => {
    if (signal.aborted) {
      resolve()
      return
    }

    let timeout: ReturnType<typeof setTimeout>
    const finish = () => {
      clearTimeout(timeout)
      signal.removeEventListener('abort', finish)
      resolve()
    }
    timeout = setTimeout(finish, milliseconds)
    signal.addEventListener('abort', finish, { once: true })
  })
}

async function streamLive(
  config: string,
  service: string,
  initialLine: number,
  controller: AbortController,
  version: number,
) {
  const retryBackoff = [1000, 2000, 4000, 8000, 10000]
  let fromLine = initialLine
  let retry = 0

  while (isCurrentSession(controller, version)) {
    try {
      await api.streamLogTail(
        config,
        service,
        fromLine,
        (line) => {
          if (!isCurrentSession(controller, version)) return
          fromLine += 1
          retry = 0
          appendLiveLine(line)
        },
        controller.signal,
      )
    } catch {
      if (!isCurrentSession(controller, version)) return
    }

    if (!isCurrentSession(controller, version)) return
    await waitForRetry(retryBackoff[Math.min(retry, retryBackoff.length - 1)], controller.signal)
    retry = Math.min(retry + 1, retryBackoff.length - 1)
  }
}

watch(
  () => props.config,
  (config) => {
    selectedService.value = config?.services[0]?.name ?? null
  },
  { immediate: true },
)

watch(
  [() => props.config, selectedService],
  () => {
    const config = props.config
    const serviceNames = config?.services.map((service) => service.name) ?? []
    if (config && serviceNames.length > 0 && !serviceNames.includes(selectedService.value ?? '')) {
      selectedService.value = serviceNames[0] ?? null
      return
    }
    startSession()
  },
  { immediate: true },
)

onBeforeUnmount(cancelSession)
</script>

<template>
  <n-modal
    :show="props.config !== null"
    preset="card"
    :title="props.config ? `Logs for ${props.config.name}` : 'Logs'"
    :style="{ width: '720px', maxWidth: 'calc(100vw - 32px)' }"
    closable
    @update:show="(show: boolean) => !show && emit('update:config', null)"
  >
    <div class="log-viewer-content">
      <div v-if="props.config" class="service-picker">
        <n-text depth="3">Service</n-text>
        <n-select
          v-model:value="selectedService"
          :options="serviceOptions"
          :disabled="serviceOptions.length === 0"
          size="small"
        />
      </div>
      <n-text v-if="historyError" type="error" class="history-error">{{ historyError }}</n-text>
      <n-text v-if="loadingOlder" depth="3" class="history-loading">Loading older logs…</n-text>
      <div class="log-container">
        <div
          ref="logViewport"
          class="log-viewport"
          :style="{ backgroundColor: vars.codeColor, color: vars.textColor1 }"
          @scroll="onLogScroll"
        >
          <div v-if="props.config && serviceOptions.length === 0" class="log-status">
            No services in this configuration.
          </div>
          <div v-else-if="loadingInitial" class="log-status">Loading logs…</div>
          <div v-else-if="lines.length === 0" class="log-status">No logs yet.</div>
          <LogLines v-else :entries="lines" />
        </div>
        <n-button
          v-if="showBackToBottom"
          class="back-to-bottom"
          size="small"
          quaternary
          @click="scrollToBottom"
        >
          Back to bottom
        </n-button>
      </div>
    </div>
  </n-modal>
</template>

<style scoped>
.service-picker {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-bottom: 12px;
}
.service-picker :deep(.n-select) {
  flex: 1;
}
.history-error,
.history-loading {
  display: block;
  margin-bottom: 8px;
}
.log-container {
  position: relative;
}
.log-viewport {
  box-sizing: border-box;
  height: 420px;
  overflow-y: auto;
  padding: 10px 12px;
  border-radius: 4px;
}
.log-lines {
  display: flex;
  min-height: 100%;
  flex-direction: column;
  justify-content: flex-end;
}
.log-line {
  white-space: pre-wrap;
  word-break: break-all;
  line-height: 1.5;
  font-family: 'SF Mono', Menlo, Consolas, monospace;
}
.log-link {
  text-decoration: underline;
}
.log-status {
  display: flex;
  min-height: 100%;
  align-items: center;
  justify-content: center;
  font-family: 'SF Mono', Menlo, Consolas, monospace;
}
.back-to-bottom {
  position: absolute;
  right: 12px;
  bottom: 12px;
  z-index: 1;
}
</style>
