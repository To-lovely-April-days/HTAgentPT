import { defineConfig } from 'vite'
import type { Plugin } from 'vite'
import react from '@vitejs/plugin-react'
import { createRequire } from 'node:module'
import { readFileSync, mkdirSync, copyFileSync } from 'node:fs'
import { dirname, join } from 'node:path'

// 预览面板里跑的网页（模型写的 HTML）常引用 React、Vue、ECharts、Tailwind 的 CDN 地址。
// 内网用户上不了外网，所以把这些库的浏览器版放到我们自己的 /vendor/ 下，预览时把 CDN 地址换成这里。
const require = createRequire(import.meta.url)
const VENDOR: Record<string, string> = {
  'react.js': 'react18/umd/react.production.min.js',
  'react-dom.js': 'react-dom18/umd/react-dom.production.min.js',
  'babel.js': '@babel/standalone/babel.min.js',
  'vue.js': 'vue/dist/vue.global.prod.js',
  'echarts.js': 'echarts/dist/echarts.min.js',
  'tailwind.js': '@tailwindcss/browser',
}
// 有的包用 exports 限制了子路径（React 18 的 umd），这时按包目录拼路径
const resolveVendor = (name: string) => {
  const spec = VENDOR[name]
  try { return require.resolve(spec) } catch {
    const parts = spec.split('/')
    const pkg = spec.startsWith('@') ? parts.slice(0, 2).join('/') : parts[0]
    return join(dirname(require.resolve(`${pkg}/package.json`)), spec.slice(pkg.length + 1))
  }
}

function vendor(): Plugin {
  let outDir = 'dist'
  return {
    name: 'ht-vendor',
    configResolved(c) { outDir = c.build.outDir },
    configureServer(server) {
      server.middlewares.use('/vendor/', (req, res, next) => {
        const name = (req.url ?? '').replace(/^\//, '').split('?')[0]
        if (!VENDOR[name]) return next()
        res.setHeader('Content-Type', 'application/javascript; charset=utf-8')
        res.end(readFileSync(resolveVendor(name)))
      })
    },
    closeBundle() {
      const dir = join(outDir, 'vendor')
      mkdirSync(dir, { recursive: true })
      for (const name of Object.keys(VENDOR)) copyFileSync(resolveVendor(name), join(dir, name))
    },
  }
}

// 开发期 /api 直通本机后端；生产由反向代理承担同源。
// API_TARGET / PORT 可指到总部节点（同一份代码，换一组配置渲染为总部审核台）。
export default defineConfig({
  plugins: [react(), vendor()],
  server: {
    port: Number(process.env.PORT ?? 5173),
    proxy: { '/api': { target: process.env.API_TARGET ?? 'http://127.0.0.1:5210', changeOrigin: false } },
  },
})
