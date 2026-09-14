<script setup lang="ts">
import { darkTheme, NConfigProvider, NDialogProvider, NMessageProvider } from 'naive-ui'
import AppRoot from './components/AppRoot.vue'
</script>

<template>
  <n-config-provider :theme="darkTheme">
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
  background-color: #101014;
  /* naive-ui only colors its own components; raw elements must not fall back to
     the browser default (black on dark background). */
  color: rgba(255, 255, 255, 0.82);
}
</style>
