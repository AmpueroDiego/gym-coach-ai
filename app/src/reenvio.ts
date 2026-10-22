// Temporizadores de Principal: cuándo volver a subir lo pendiente y cuándo cambia el día. Lee la forma persistida del almacén (gc.sinSubir:<sub>),
// que es donde queda el Retry-After de cada serie tras un 429.

/** Backoff fijo para las series en 'error' (red caída, 5xx). */
export const BACKOFF_ERROR_MS = 30_000

/**
 * Epoch ms del próximo reenvío: el menor `reintentarDesde` de las pendientes por 429,
 * o `ahora + backoffMs` si hay alguna en 'error'. null = nada que reprogramar.
 */
export function proximoReenvio(crudo: string | null, ahora: number, backoffMs = BACKOFF_ERROR_MS): number | null {
  let lista: unknown
  try {
    lista = JSON.parse(crudo ?? '[]')
  } catch {
    return null
  }
  if (!Array.isArray(lista)) return null
  let proximo: number | null = null
  const candidato = (t: number) => {
    if (proximo === null || t < proximo) proximo = t
  }
  for (const x of lista as unknown[]) {
    if (typeof x !== 'object' || x === null) continue
    const { estado, reintentarDesde } = x as { estado?: unknown; reintentarDesde?: unknown }
    if (typeof reintentarDesde === 'number' && Number.isFinite(reintentarDesde) && (estado === 'pendiente' || estado === 'error')) {
      candidato(Math.max(reintentarDesde, ahora))
    } else if (estado === 'error') {
      candidato(ahora + backoffMs)
    }
  }
  return proximo
}

/** Ms hasta la próxima medianoche local (para recalcular "hoy" con la app abierta). */
export function msHastaMedianoche(ahora: Date): number {
  const manana = new Date(ahora.getFullYear(), ahora.getMonth(), ahora.getDate() + 1)
  return manana.getTime() - ahora.getTime()
}
