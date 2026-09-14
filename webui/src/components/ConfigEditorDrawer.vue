<script setup lang="ts">
import {
  NAlert,
  NButton,
  NDrawer,
  NDrawerContent,
  NForm,
  NFormItem,
  NInput,
  NSpin,
  NText,
  useMessage,
} from 'naive-ui'
import { inject, nextTick, onBeforeUnmount, ref, toRef, watch } from 'vue'
import { ApiError, api } from '../api/client'
import type { ConfigSummary, SyncReport } from '../api/types'
import { loadMonaco } from '../monaco/loader'
import type { MonacoEditor } from '../monaco/loader'
import { attachComposeSupport } from '../monaco/composeYaml'
import SyncReportView from './SyncReportView.vue'

type Editing = ConfigSummary | 'new' | null

const props = defineProps<{
  editing: Editing
  onSaved: () => Promise<void>
}>()

const emit = defineEmits<{
  'update:editing': [editing: Editing]
}>()

const message = useMessage()
const handleApiError = inject<(err: unknown, fallback: string) => void>('handleApiError')!
const editing = toRef(props, 'editing')

const editorName = ref('')
const editorYaml = ref('')
const editorLoading = ref(false)
const editorSaving = ref(false)
const editorError = ref('')
const saveReport = ref<SyncReport | null>(null)

const editorEl = ref<HTMLElement | null>(null)
const editorFailed = ref(false)
let monacoEditor: MonacoEditor | null = null
let initToken = 0

function disposeEditor() {
  initToken += 1
  monacoEditor?.dispose()
  monacoEditor = null
}

async function initEditor() {
  const token = ++initToken
  await nextTick()
  const host = editorEl.value
  if (!host || editorFailed.value) {
    return
  }
  try {
    const monaco = await loadMonaco()
    if (token !== initToken || !editorEl.value) {
      return
    }
    monacoEditor?.dispose()
    const editor = monaco.editor.create(host, {
      value: editorYaml.value,
      language: 'yaml',
      theme: 'vs-dark',
      fontSize: 13,
      minimap: { enabled: false },
      automaticLayout: true,
      scrollBeyondLastLine: false,
      fixedOverflowWidgets: true,
      tabSize: 2,
    })
    monacoEditor = editor
    const model = editor.getModel()
    if (model) {
      await attachComposeSupport(monaco, model, (listener) =>
        editor.onDidChangeModelContent(() => {
          editorYaml.value = editor.getValue()
          listener()
        }),
      )
    }
  } catch (err) {
    console.error('svchost: monaco editor init failed', err)
    editorFailed.value = true
    monacoEditor?.dispose()
    monacoEditor = null
  }
}

onBeforeUnmount(disposeEditor)

watch(
  editing,
  async (target) => {
    if (target === null) {
      disposeEditor()
      editorLoading.value = false
      return
    }

    editorError.value = ''
    saveReport.value = null
    disposeEditor()

    if (target === 'new') {
      editorName.value = ''
      editorYaml.value = 'services:\n'
      editorLoading.value = false
      await initEditor()
      return
    }

    editorName.value = target.name
    editorLoading.value = true
    try {
      const detail = await api.getConfig(target.name)
      editorYaml.value = detail.yaml
    } catch (err) {
      handleApiError(err, 'Failed to load config')
      message.error(err instanceof ApiError ? err.message : 'Failed to load config')
      emit('update:editing', null)
      return
    } finally {
      editorLoading.value = false
    }
    await initEditor()
  },
  { immediate: true },
)

async function save() {
  editorSaving.value = true
  editorError.value = ''
  try {
    // Reconcile-level failures arrive as HTTP 200 with succeeded:false.
    const report = await api.putConfig(editorName.value, editorYaml.value)
    saveReport.value = report
    if (report.succeeded) {
      message.success(`Config "${editorName.value}" saved and reconciled.`)
    } else {
      message.error(report.error ?? `Config "${editorName.value}" saved, but the sync failed for some services.`)
    }
    await props.onSaved()
  } catch (err) {
    if (err instanceof ApiError && err.status === 422) {
      // YAML validation envelope: keep the editor open and show errors inline.
      editorError.value = err.message
    } else {
      handleApiError(err, 'Failed to save config')
      message.error(err instanceof ApiError ? err.message : 'Failed to save config')
    }
  } finally {
    editorSaving.value = false
  }
}
</script>

<template>
  <n-drawer
    :show="editing !== null"
    :width="640"
    placement="right"
    @update:show="(v: boolean) => !v && emit('update:editing', null)"
  >
    <n-drawer-content
      :title="editing === 'new' ? 'New configuration' : `Edit ${editorName}`"
      closable
    >
      <n-spin :show="editorLoading">
        <n-form label-placement="top">
          <n-form-item
            label="Config name"
            :validation-status="editing === 'new' && editorName && !/^[a-z0-9][a-z0-9-]{0,62}$/.test(editorName) ? 'error' : undefined"
            :feedback="editing === 'new' && editorName && !/^[a-z0-9][a-z0-9-]{0,62}$/.test(editorName) ? 'Lowercase letters, digits and dashes; must start with a letter or digit.' : undefined"
          >
            <n-input
              v-model:value="editorName"
              :disabled="editing !== 'new'"
              placeholder="my-config"
            />
          </n-form-item>
          <n-form-item label="YAML">
            <div class="yaml-editor-host">
              <div v-show="!editorFailed" ref="editorEl" class="monaco-editor-container"></div>
              <n-input
                v-if="editorFailed"
                v-model:value="editorYaml"
                type="textarea"
                :rows="18"
                class="yaml-editor"
                placeholder="services:"
                :input-props="{ spellcheck: 'false' }"
              />
            </div>
          </n-form-item>
          <n-alert v-if="editorFailed" type="warning" :bordered="false" class="editor-error">
            Editor CDN unavailable; using the plain text editor.
          </n-alert>
        </n-form>

        <n-alert v-if="editorError" type="error" :bordered="false" title="Validation failed" class="editor-error">
          {{ editorError }}
        </n-alert>

        <template v-if="saveReport">
          <n-text strong class="report-title">Sync report</n-text>
          <SyncReportView :report="saveReport" />
        </template>
      </n-spin>

      <template #footer>
        <n-button :disabled="!editorName || !editorYaml.trim()" :loading="editorSaving" type="primary" @click="save">
          Save &amp; reconcile
        </n-button>
      </template>
    </n-drawer-content>
  </n-drawer>
</template>

<style scoped>
.yaml-editor-host {
  width: 100%;
}
.monaco-editor-container {
  width: 100%;
  height: 430px;
  border: 1px solid rgba(255, 255, 255, 0.09);
  border-radius: 3px;
}
.yaml-editor {
  font-family: 'SF Mono', Menlo, Consolas, monospace;
  font-size: 13px;
}
.editor-error {
  margin-bottom: 12px;
}
.report-title {
  display: block;
  margin: 4px 0 8px;
}
</style>
