import tailwindcss from '@tailwindcss/vite'
import react from '@vitejs/plugin-react'
import path from 'node:path'
import { defineConfig } from 'vite'

// The design system is consumed from source. Its files import '@/...' for their own modules, so '@'
// points at the design system and the app uses '~' for its own source.
export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: {
      '@': path.resolve(import.meta.dirname, '../../packages/ui/src'),
      '~': path.resolve(import.meta.dirname, 'src'),
    },
  },
})
