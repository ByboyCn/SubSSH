import { fileURLToPath, URL } from 'node:url';
import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';
// 构建产物直接输出到 C# 后端 embed 目录，SubSsh.Plugin.WebApi 无需拷贝即可托管 SPA
export default defineConfig({
    plugins: [vue()],
    resolve: {
        alias: {
            '@': fileURLToPath(new URL('./src', import.meta.url)),
        },
    },
    build: {
        outDir: '../src/components/SubSsh.Plugin.WebApi/dist',
        emptyOutDir: true,
    },
    server: {
        port: 5173,
        proxy: {
            '/api': {
                target: 'http://127.0.0.1:8080',
                changeOrigin: true,
                // 终端 WebSocket 与 SSE 走同一路径，一并代理
                ws: true,
            },
            '/.well-known': {
                target: 'http://127.0.0.1:8080',
                changeOrigin: true,
            },
        },
    },
});
