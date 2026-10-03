import { darkTheme, type GlobalThemeOverrides } from 'naive-ui'
import { computed, ref, watchEffect } from 'vue'

export type ThemeMode = 'light' | 'dark' | 'system'

const STORAGE_KEY = 'svchost-theme-mode'
const systemQuery = window.matchMedia('(prefers-color-scheme: dark)')

function loadMode(): ThemeMode {
  const stored = localStorage.getItem(STORAGE_KEY)
  return stored === 'light' || stored === 'dark' || stored === 'system' ? stored : 'system'
}

const mode = ref<ThemeMode>(loadMode())
const systemDark = ref(systemQuery.matches)
systemQuery.addEventListener('change', (event) => {
  systemDark.value = event.matches
})

export const themeMode = computed(() => mode.value)
export const isDark = computed(() => (mode.value === 'system' ? systemDark.value : mode.value === 'dark'))
export const naiveTheme = computed(() => (isDark.value ? darkTheme : null))

export function setThemeMode(next: ThemeMode) {
  mode.value = next
  localStorage.setItem(STORAGE_KEY, next)
}

// Emerald accent shared by both schemes; neutrals diverge per scheme.
const accent = {
  primaryColor: '#2ebd85',
  primaryColorHover: '#47cd9a',
  primaryColorPressed: '#23a873',
  primaryColorSuppl: '#2ebd85',
  successColor: '#2ebd85',
  successColorHover: '#47cd9a',
  successColorPressed: '#23a873',
  infoColor: '#4d9de0',
  infoColorHover: '#71b3e8',
  infoColorPressed: '#3c8ad4',
  warningColor: '#e0a83e',
  warningColorHover: '#e8bc65',
  warningColorPressed: '#cf9530',
  errorColor: '#e05d6f',
  errorColorHover: '#e87e8c',
  errorColorPressed: '#cf4a5d',
}

const lightOverrides: GlobalThemeOverrides = {
  common: {
    ...accent,
    borderRadius: '8px',
    borderRadiusSmall: '5px',
    fontFamily:
      "ui-sans-serif, system-ui, -apple-system, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, sans-serif",
    fontFamilyMono: "ui-monospace, 'SF Mono', Menlo, Consolas, monospace",
    bodyColor: '#f4f5f7',
    cardColor: '#ffffff',
    modalColor: '#ffffff',
    popoverColor: '#ffffff',
    tableColor: '#ffffff',
    inputColor: '#ffffff',
    borderColor: 'rgba(15, 23, 42, 0.1)',
    dividerColor: 'rgba(15, 23, 42, 0.08)',
    textColorBase: 'rgba(15, 23, 42, 0.88)',
    textColor1: 'rgba(15, 23, 42, 0.9)',
    textColor2: 'rgba(15, 23, 42, 0.66)',
    textColor3: 'rgba(15, 23, 42, 0.42)',
    boxShadow1: '0 1px 2px rgba(15, 23, 42, 0.04), 0 4px 16px rgba(15, 23, 42, 0.06)',
    boxShadow2: '0 2px 4px rgba(15, 23, 42, 0.05), 0 8px 28px rgba(15, 23, 42, 0.1)',
  },
  Table: {
    thColor: 'rgba(15, 23, 42, 0.025)',
    thTextColor: 'rgba(15, 23, 42, 0.45)',
    thFontWeight: '600',
    tdColor: 'transparent',
    borderColor: 'rgba(15, 23, 42, 0.07)',
  },
  Card: {
    borderColor: 'rgba(15, 23, 42, 0.08)',
  },
}

const darkOverrides: GlobalThemeOverrides = {
  common: {
    ...accent,
    primaryColor: '#3ecf8e',
    primaryColorHover: '#5cd9a4',
    primaryColorPressed: '#2eb87d',
    primaryColorSuppl: '#3ecf8e',
    successColor: '#3ecf8e',
    successColorHover: '#5cd9a4',
    successColorPressed: '#2eb87d',
    borderRadius: '8px',
    borderRadiusSmall: '5px',
    fontFamily:
      "ui-sans-serif, system-ui, -apple-system, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, sans-serif",
    fontFamilyMono: "ui-monospace, 'SF Mono', Menlo, Consolas, monospace",
    bodyColor: '#0f1013',
    cardColor: '#17181d',
    modalColor: '#1b1c22',
    popoverColor: '#1f2026',
    tableColor: '#17181d',
    inputColor: 'rgba(255, 255, 255, 0.045)',
    borderColor: 'rgba(255, 255, 255, 0.09)',
    dividerColor: 'rgba(255, 255, 255, 0.08)',
    textColorBase: 'rgba(255, 255, 255, 0.88)',
    textColor1: 'rgba(255, 255, 255, 0.88)',
    textColor2: 'rgba(255, 255, 255, 0.68)',
    textColor3: 'rgba(255, 255, 255, 0.42)',
    boxShadow1: '0 1px 2px rgba(0, 0, 0, 0.3), 0 4px 16px rgba(0, 0, 0, 0.35)',
    boxShadow2: '0 2px 4px rgba(0, 0, 0, 0.35), 0 8px 28px rgba(0, 0, 0, 0.45)',
  },
  Table: {
    thColor: 'rgba(255, 255, 255, 0.03)',
    thTextColor: 'rgba(255, 255, 255, 0.45)',
    thFontWeight: '600',
    tdColor: 'transparent',
    borderColor: 'rgba(255, 255, 255, 0.07)',
  },
  Card: {
    borderColor: 'rgba(255, 255, 255, 0.08)',
  },
}

export const themeOverrides = computed(() => (isDark.value ? darkOverrides : lightOverrides))

// naive-ui only colors its own components; the page chrome follows the mode.
watchEffect(() => {
  const dark = isDark.value
  document.documentElement.style.colorScheme = dark ? 'dark' : 'light'
  document.body.style.backgroundColor = dark ? '#0f1013' : '#f4f5f7'
  document.body.style.color = dark ? 'rgba(255, 255, 255, 0.88)' : 'rgba(15, 23, 42, 0.88)'
})
