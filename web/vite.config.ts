import { fileURLToPath, URL } from 'node:url'
import tailwindcss from '@tailwindcss/vite'
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// The service serves the built SPA from its wwwroot. In development, Vite serves the UI and
// proxies /api to the locally running service (self-signed certificate, hence secure: false).
export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) },
  },
  build: {
    outDir: '../src/Stowaway.Service/wwwroot',
    emptyOutDir: true,
    // Served from localhost, so a single bundle is fine; revisit with route-level splitting as pages grow.
    chunkSizeWarningLimit: 1000,
  },
  server: {
    // Help pages are bundled from ../docs/help.
    fs: { allow: ['..'] },
    port: 5173,
    strictPort: true,
    proxy: {
      '/api': { target: 'https://localhost:8484', secure: false, changeOrigin: true },
      '/hubs': { target: 'https://localhost:8484', secure: false, changeOrigin: true, ws: true },
    },
  },
})
