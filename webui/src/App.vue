<script setup lang="ts">
import { NConfigProvider, NDialogProvider, NMessageProvider } from 'naive-ui'
import AppRoot from './components/AppRoot.vue'
import { naiveTheme, themeOverrides } from './theme'
</script>

<template>
  <n-config-provider :theme="naiveTheme" :theme-overrides="themeOverrides">
    <n-message-provider placement="top-right">
      <n-dialog-provider>
        <AppRoot />
      </n-dialog-provider>
    </n-message-provider>
  </n-config-provider>
</template>

<style>
html,
body,
#app {
  height: 100%;
  margin: 0;
  /* naive-ui only colors its own components; raw elements must not fall back to
     the browser default (black on dark background). Actual values are applied
     by src/theme.ts per active color scheme. */
  transition: background-color 0.2s ease;
}
</style>
