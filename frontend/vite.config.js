import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// Dev: /api is proxied to the .NET API. Build: output goes to the API's wwwroot so it can serve the UI too.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: { '/api': 'http://localhost:5080' },
  },
  build: {
    outDir: '../backend/LendingApp.Api/wwwroot',
    emptyOutDir: true,
  },
})
