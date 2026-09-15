import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// 开发期 /api 直通本机后端；生产由反向代理承担同源
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: { '/api': { target: 'http://127.0.0.1:5210', changeOrigin: false } },
  },
})
