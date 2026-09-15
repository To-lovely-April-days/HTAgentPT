import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// 开发期 /api 直通本机后端；生产由反向代理承担同源。
// API_TARGET / PORT 可指到总部节点（同一份代码，换一组配置渲染为总部审核台）。
export default defineConfig({
  plugins: [react()],
  server: {
    port: Number(process.env.PORT ?? 5173),
    proxy: { '/api': { target: process.env.API_TARGET ?? 'http://127.0.0.1:5210', changeOrigin: false } },
  },
})
