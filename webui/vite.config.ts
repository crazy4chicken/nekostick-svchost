import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import { viteSingleFile } from 'vite-plugin-singlefile'

// The host serves dist/index.html verbatim at /svchost, so the build must be a
// single self-contained file with no external asset references.
export default defineConfig({
  plugins: [vue(), viteSingleFile()],
  build: {
    assetsInlineLimit: 100_000_000,
    chunkSizeWarningLimit: 100_000_000,
    cssCodeSplit: false,
    minify: 'esbuild',
  },
})
