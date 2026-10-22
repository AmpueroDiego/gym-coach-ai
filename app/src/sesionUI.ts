import type { Sesion } from './auth'

export type Verificada = 'pendiente' | 'ok' | 'sin_red' | 'ausente'

export interface SesionUI {
  /** La sesión de la cookie es de la cuenta que se muestra. */
  delaCuenta: boolean
  /** Se puede hablar con la API (en 'sin_red' es optimista: se confía en la cuenta recordada). */
  conSesion: boolean
  /** Banda Reconectar. Nunca con 'pendiente': aún no se sabe y parpadearía en cada arranque. */
  mostrarReconectar: boolean
  /** Aún sin saber si hay sesión: la UI dice "Cargando…" en vez de pedir reconectar. */
  esperando: boolean
}

export function derivarSesionUI(verificada: Verificada, sesion: Sesion | null, cuenta: { sub: string } | null): SesionUI {
  if (!cuenta) return { delaCuenta: false, conSesion: false, mostrarReconectar: false, esperando: false }
  const delaCuenta = sesion?.sub === cuenta.sub
  const conSesion = verificada === 'sin_red' || (verificada === 'ok' && delaCuenta)
  const mostrarReconectar = verificada === 'ausente' || (verificada === 'ok' && !delaCuenta)
  return { delaCuenta, conSesion, mostrarReconectar, esperando: verificada === 'pendiente' }
}
