import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// https://vite.dev/config/
export default defineConfig({
  base: './',
  plugins: [vue()],
  server: {
    port: 5173,
    host: '0.0.0.0',
    proxy: {
      // 核心 MES API (8076 端口)
      '/mes-api': {
        target: 'http://172.25.57.144:8076',
        changeOrigin: true,
        rewrite: (path) => path.replace(/^\/mes-api/, ''),
        configure: (proxy) => {
          proxy.on('error', (err) => {
            console.log('[MES代理错误]', err.message)
          })
          proxy.on('proxyReq', (_, req) => {
            console.log('[MES代理请求]', req.method, req.url)
          })
        }
      },
      // MES 报工数据上传 (8072 端口)
      '/mes-push': {
        target: 'http://172.25.57.144:8072',
        changeOrigin: true,
        rewrite: (path) => path.replace(/^\/mes-push/, ''),
      },
      // 本地 C# 后端 (5246 端口)
      '/api': {
        target: 'http://127.0.0.1:5246',
        changeOrigin: true,
        ws: true,
        rewrite: (path) => path.replace(/^\/api/, '')
      }
    }
  },
  build: {
    target: 'es2015',
    minify: 'esbuild'
  }
})
