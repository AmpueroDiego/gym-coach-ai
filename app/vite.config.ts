/// <reference types="vitest/config" />
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'
import { VitePWA } from 'vite-plugin-pwa'
import { execSync } from 'node:child_process'

// SHA corto para mostrar la versión: CI lo pasa en APP_SHA; en local sale de git.
function gitCorto(): string | undefined {
  try {
    return execSync('git rev-parse --short HEAD', { stdio: ['ignore', 'pipe', 'ignore'] }).toString().trim() || undefined
  } catch {
    return undefined
  }
}
const sha7 = (process.env.APP_SHA ?? process.env.GITHUB_SHA ?? gitCorto() ?? 'dev').slice(0, 7)
const fecha = new Date().toISOString().slice(0, 10)

export default defineConfig({
  define: {
    __APP_VERSION__: JSON.stringify(sha7),
    __BUILD_DATE__: JSON.stringify(fecha),
  },
  plugins: [
    react(),
    VitePWA({
      registerType: 'autoUpdate',
      // Solo el app shell: /api nunca se cachea (los datos siempre vienen del servidor).
      workbox: {
        globPatterns: ['**/*.{js,css,html,svg,png,webmanifest}'],
        navigateFallbackDenylist: [/^\/api\//],
        // Fotos de la ficha: con SHA fijo no cambian; tras verlas una vez sirven sin conexión.
        // opaque (status 0) no se acepta: las <img> usan crossOrigin y CORS es *.
        runtimeCaching: [
          {
            urlPattern: /^https:\/\/raw\.githubusercontent\.com\/yuhonas\/free-exercise-db\//,
            handler: 'CacheFirst',
            options: {
              cacheName: 'fotos-ejercicios',
              expiration: { maxEntries: 120, maxAgeSeconds: 60 * 60 * 24 * 365 },
              cacheableResponse: { statuses: [200] },
            },
          },
        ],
      },
      includeAssets: ['favicon.svg'],
      manifest: {
        name: 'gym-coach',
        short_name: 'gym-coach',
        description: 'Registra tus series en el gym con una mano.',
        lang: 'es',
        id: '/',
        scope: '/',
        start_url: '/',
        display: 'standalone',
        orientation: 'portrait',
        background_color: '#111418',
        theme_color: '#111418',
        icons: [
          { src: '/icons/icon-192.png', sizes: '192x192', type: 'image/png' },
          { src: '/icons/icon-512.png', sizes: '512x512', type: 'image/png' },
          { src: '/icons/icon-512.png', sizes: '512x512', type: 'image/png', purpose: 'maskable' },
        ],
      },
    }),
  ],
  // `npm run dev` sin SWA CLI: manda /api al host de Functions local.
  server: { proxy: { '/api': 'http://localhost:7071' } },
  test: { environment: 'node' },
})
