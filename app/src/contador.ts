// Contadores al ejecutar la rutina. Todo sale de timestamps: sobrevive a la pestaña en segundo plano.
import type { EjercicioRutina, Serie } from './api'
import { cuentaComoTrabajo } from './records'

export const DESCANSOS = [0, 30, 45, 60, 90, 120, 180, 300] as const  // 0 = sin descanso (opcional)
/** Global del dispositivo: descanso de los ejercicios fuera del plan. */
export const claveDescanso = 'gc.descanso'

type Almacen = Pick<Storage, 'getItem' | 'setItem'> | null

export function leerDescanso(a: Almacen): number {
  try {
    const n = Number(a?.getItem(claveDescanso))
    return (DESCANSOS as readonly number[]).includes(n) ? n : 0
  } catch {
    return 0
  }
}

export function guardarDescanso(a: Almacen, s: number): void {
  try {
    a?.setItem(claveDescanso, String(s))
  } catch {
    // Sin almacenamiento: dura solo la sesión.
  }
}

/** Al guardar una serie: { inicio: ahora, duracionS }. */
export interface Descanso {
  inicio: number
  duracionS: number
}

/** Descanso tras una serie: el del ejercicio en el plan (0 = ninguno); fuera del plan, el global. */
export function descansoDe(ejercicioId: string, plan: EjercicioRutina[] | null, porDefectoS: number): number {
  return plan?.find((e) => e.id === ejercicioId)?.descansoS ?? porDefectoS
}

/** Segundos que faltan (hacia arriba, nunca negativo). */
export function restanteS(d: Descanso | null, ahora: number): number {
  if (!d) return 0
  if (!Number.isFinite(d.inicio + d.duracionS * 1000 - ahora)) return 0
  // Acotado a la duración: con un inicio futuro nunca se muestran horas.
  return Math.min(Math.max(0, d.duracionS), Math.max(0, Math.ceil((d.inicio + d.duracionS * 1000 - ahora) / 1000)))
}

/** m:ss, o h:mm:ss desde 1 h. */
export function formatoReloj(totalS: number): string {
  const t = Number.isFinite(totalS) ? Math.max(0, Math.floor(totalS)) : 0
  const h = Math.floor(t / 3600)
  const m = Math.floor((t % 3600) / 60)
  const s = String(t % 60).padStart(2, '0')
  return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${s}` : `${m}:${s}`
}

export function tiempoEntrenoS(plan: { inicio: number } | null, ahora: number): number {
  const t = plan ? Math.floor((ahora - plan.inicio) / 1000) : 0
  return Number.isFinite(t) ? Math.max(0, t) : 0
}

/**
 * Un ejercicio está hecho al llegar a sus series objetivo; seriesHechas no cuenta de más (min por ejercicio).
 * Excluye rechazadas y calentamientos. Ids repetidos en el plan cuentan una vez.
 */
export function progresoPlan(
  series: (Serie & { estado?: string })[],
  plan: EjercicioRutina[],
): { ejerciciosHechos: number; ejerciciosTotal: number; seriesHechas: number; seriesObjetivo: number } {
  const hechas = new Map<string, number>()
  for (const s of series) {
    if (s.estado !== 'rechazada' && cuentaComoTrabajo(s)) hechas.set(s.ejercicioId, (hechas.get(s.ejercicioId) ?? 0) + 1)
  }
  const unicos = new Map<string, EjercicioRutina>()
  for (const e of plan) if (!unicos.has(e.id)) unicos.set(e.id, e)
  let ejerciciosHechos = 0
  let seriesHechas = 0
  let seriesObjetivo = 0
  for (const e of unicos.values()) {
    const h = hechas.get(e.id) ?? 0
    if (h >= e.series) ejerciciosHechos++
    seriesHechas += Math.min(h, e.series)
    seriesObjetivo += e.series
  }
  return { ejerciciosHechos, ejerciciosTotal: unicos.size, seriesHechas, seriesObjetivo }
}
