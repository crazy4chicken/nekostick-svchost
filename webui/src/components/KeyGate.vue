<script setup lang="ts">
import { NButton, NCard, NForm, NFormItem, NIcon, NInput, NText } from 'naive-ui'
import { KeyOutline } from '@vicons/ionicons5'
import { ref } from 'vue'

// Pending state is owned by AppRoot (it spans the verification network
// round-trip); this gate only renders it.
const props = defineProps<{ error: string; loading: boolean }>()
const emit = defineEmits<{ submit: [key: string] }>()

const key = ref('')

function submit() {
  if (!key.value || props.loading) return
  emit('submit', key.value)
}
</script>

<template>
  <div class="gate">
    <div class="gate-brand">
      <span class="brand-mark" />
      <span class="brand-name">nekostick <em>svchost</em></span>
    </div>
    <n-card class="gate-card" title="Enter API key">
      <n-text depth="3" class="hint">
        This svchost instance requires an API key. Enter the permanent key to continue.
      </n-text>
      <n-form @submit.prevent="submit">
        <n-form-item :show-label="false" :feedback="props.error" :validation-status="props.error ? 'error' : undefined">
          <n-input
            v-model:value="key"
            type="password"
            show-password-on="click"
            size="large"
            placeholder="X-Api-Key"
            :input-props="{ autocomplete: 'off' }"
          >
            <template #prefix>
              <n-icon><KeyOutline /></n-icon>
            </template>
          </n-input>
        </n-form-item>
        <n-button type="primary" attr-type="submit" :disabled="!key" :loading="props.loading" block>
          Unlock
        </n-button>
      </n-form>
    </n-card>
  </div>
</template>

<style scoped>
.gate {
  display: flex;
  flex-direction: column;
  align-items: center;
  padding-top: 88px;
}
.gate-brand {
  display: flex;
  align-items: center;
  gap: 10px;
  margin-bottom: 28px;
}
.brand-mark {
  width: 18px;
  height: 18px;
  border-radius: 6px;
  background: linear-gradient(135deg, #47cd9a, #1d9e6d);
  box-shadow: 0 0 0 4px rgba(62, 207, 142, 0.16);
}
.brand-name {
  font-size: 17px;
  font-weight: 650;
}
.brand-name em {
  font-style: normal;
  opacity: 0.55;
  font-weight: 550;
}
.gate-card {
  width: min(400px, 100%);
}
.hint {
  display: block;
  margin-bottom: 16px;
}
</style>
