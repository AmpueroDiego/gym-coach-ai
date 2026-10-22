// Cuándo despierta la asesoría con la app abierta en primer plano (en segundo plano los timers se congelan: ahí manda visibilitychange).
import { HORA_ASESORIA } from './coach'

/** ms hasta las 07:30 locales de `hoy`, o null si ya pasaron. */
export function msHastaAsesoria(ahora: Date, hoy: string): number | null {
  const [a, m, d] = hoy.split('-').map(Number)
  const [h, min] = HORA_ASESORIA.split(':').map(Number)
  const ms = new Date(a, m - 1, d, h, min).getTime() - ahora.getTime()
  return ms > 0 ? ms : null
}
