/// <reference types="vitest" />
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// 开发时把 /api 代理到 Go 后端，这样浏览器里只有 http://localhost:5173 一个来源，
// 与“生产环境由 Go 直接托管 dist”的行为保持一致（都不需要关心跨域）。
const BACKEND = process.env.VITE_BACKEND ?? 'http://localhost:8080'

export default defineConfig({
  plugins: [react()],
  build: {
    outDir: 'dist',
    sourcemap: true,
  },
  server: {
    port: 5173,
    proxy: {
      '/api': { target: BACKEND, changeOrigin: true },
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
    include: ['src/**/*.test.{ts,tsx}'],
  },
})
