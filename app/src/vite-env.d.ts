/// <reference types="vite/client" />

// Los inyecta `define` en vite.config.ts (en vitest no existen).
declare const __APP_VERSION__: string
declare const __BUILD_DATE__: string

interface ImportMetaEnv {
  readonly VITE_GOOGLE_CLIENT_ID?: string
}
