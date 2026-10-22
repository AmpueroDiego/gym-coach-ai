import { textos } from './i18n'

const t = textos(
  { version: 'versión', dev: 'versión dev' },
  { version: 'version', dev: 'dev version' },
)

// Versión desplegada (sha + fecha de build), para saber si la PWA ya se actualizó.
export function textoVersion(sha?: string, fecha?: string): string {
  return sha && fecha ? `${t().version} ${sha} · ${fecha}` : t().dev
}

// Fuera de vite (vitest) las constantes no existen: typeof evita el ReferenceError.
const SHA = typeof __APP_VERSION__ === 'string' ? __APP_VERSION__ : undefined
const FECHA = typeof __BUILD_DATE__ === 'string' ? __BUILD_DATE__ : undefined

/** Función, no constante: el idioma puede cambiar con la app abierta. */
export const textoVersionApp = (): string => textoVersion(SHA, FECHA)
