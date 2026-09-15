import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// 客户门户是与内网完全独立的构建产物——不共享内网的路由表、角色名与接口路径，
// 隔离区资源文件里不能打包进任何内网信息。
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5174,
    proxy: { '/api': { target: 'http://127.0.0.1:5210', changeOrigin: true } },
  },
})
