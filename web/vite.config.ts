import { defineConfig } from 'vite';

// 静态托管目标：产物全部为相对路径，可直接放到 GitHub Pages / Netlify / 任何对象存储。
export default defineConfig({
  base: './',
  build: {
    target: 'es2022',
    sourcemap: true,
    outDir: 'dist',
    // ffmpeg.wasm 核心与 worker 由 @ffmpeg/* 在运行时按 URL 加载，
    // 这里不做内联，保持产物体积可预期。
    chunkSizeWarningLimit: 4096,
  },
  // 多线程 ffmpeg.wasm 需要 SharedArrayBuffer，即需要 COOP/COEP。
  // 生产由托管方的 _headers 提供（见 web/public/_headers），
  // 开发服务器在这里补齐同样的响应头，保证行为一致。
  server: {
    headers: {
      'Cross-Origin-Opener-Policy': 'same-origin',
      'Cross-Origin-Embedder-Policy': 'require-corp',
    },
  },
  preview: {
    headers: {
      'Cross-Origin-Opener-Policy': 'same-origin',
      'Cross-Origin-Embedder-Policy': 'require-corp',
    },
  },
  optimizeDeps: {
    exclude: ['@ffmpeg/ffmpeg', '@ffmpeg/util'],
  },
});
