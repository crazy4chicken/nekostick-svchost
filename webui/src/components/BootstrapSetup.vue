<script setup lang="ts">
import {
  NAlert,
  NButton,
  NCard,
  NForm,
  NFormItem,
  NInput,
  NSteps,
  NStep,
  NText,
  useMessage,
} from 'naive-ui'
import { computed, ref } from 'vue'
import { ApiError, api, setStoredKey } from '../api/client'

const emit = defineEmits<{ done: [] }>()
const message = useMessage()

const bootstrapKey = ref('')
const newKey = ref('')
const confirmKey = ref('')
const submitting = ref(false)
const formError = ref('')

const newKeyValid = computed(() => newKey.value.length >= 16)
const confirmMatches = computed(() => confirmKey.value === newKey.value)
const canSubmit = computed(
  () => bootstrapKey.value.length > 0 && newKeyValid.value && confirmMatches.value && !submitting.value,
)

async function submit() {
  if (!canSubmit.value) return
  submitting.value = true
  formError.value = ''
  try {
    await api.setBootstrapKey(bootstrapKey.value, newKey.value)
    setStoredKey(newKey.value)
    message.success('Permanent API key stored. Bootstrap mode exited.')
    emit('done')
  } catch (err) {
    formError.value =
      err instanceof ApiError ? err.message : 'Failed to set the permanent key.'
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <div class="bootstrap">
    <n-alert type="warning" :bordered="false" class="alert" title="Bootstrap mode">
      No permanent API key is configured for this svchost instance. A one-time bootstrap
      key was printed to the host log — use it now to set your permanent key. The
      one-time key is invalidated once bootstrap completes or the extension reloads.
    </n-alert>

    <n-card class="card" title="Set up your API key">
      <n-steps :current="2" size="small" class="steps">
        <n-step title="Copy the one-time key from the host log" />
        <n-step title="Choose a permanent key (at least 16 characters)" />
        <n-step title="Save" />
      </n-steps>

      <n-form label-placement="top" @submit.prevent="submit">
        <n-form-item label="One-time bootstrap key (from host log)">
          <n-input
            v-model:value="bootstrapKey"
            type="password"
            show-password-on="click"
            placeholder="One-time key"
            :input-props="{ autocomplete: 'off' }"
          />
        </n-form-item>
        <n-form-item
          label="New permanent key"
          :validation-status="newKey && !newKeyValid ? 'error' : undefined"
          :feedback="newKey && !newKeyValid ? 'Must be at least 16 characters.' : undefined"
        >
          <n-input
            v-model:value="newKey"
            type="password"
            show-password-on="click"
            placeholder="At least 16 characters"
            :input-props="{ autocomplete: 'new-password' }"
          />
        </n-form-item>
        <n-form-item
          label="Repeat new key"
          :validation-status="confirmKey && !confirmMatches ? 'error' : undefined"
          :feedback="confirmKey && !confirmMatches ? 'Keys do not match.' : undefined"
        >
          <n-input
            v-model:value="confirmKey"
            type="password"
            show-password-on="click"
            placeholder="Repeat the new key"
            :input-props="{ autocomplete: 'new-password' }"
          />
        </n-form-item>

        <n-text v-if="formError" type="error">{{ formError }}</n-text>

        <n-button
          type="primary"
          attr-type="submit"
          :disabled="!canSubmit"
          :loading="submitting"
          block
        >
          Save permanent key
        </n-button>
      </n-form>
    </n-card>
  </div>
</template>

<style scoped>
.bootstrap {
  max-width: 560px;
  margin: 0 auto;
}
.alert {
  margin-bottom: 16px;
}
.card {
  margin-bottom: 16px;
}
.steps {
  margin-bottom: 24px;
}
</style>
