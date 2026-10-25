import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// In development the API runs on its own port; in production the same container serves both.
export default defineConfig({
  plugins: [react()],
  server: {
    port: Number(process.env.WEB_PORT ?? 5173),
    proxy: {
      '/api': `http://localhost:${process.env.API_PORT ?? 5130}`,
      '/health': `http://localhost:${process.env.API_PORT ?? 5130}`,
    },
  },
  build: { outDir: 'dist', sourcemap: false },
  test: { environment: 'node', exclude: ['e2e/**', 'node_modules/**'] },
})
