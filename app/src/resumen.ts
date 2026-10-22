// Historial agrupado en sesiones (un día + una rutina), para la pantalla Historial.
import type { Serie } from './api'
import { cuentaComoTrabajo } from './records'

export interface EjercicioResumen {
  ejercicioId: string
  /** En orden de ts, calentamientos incluidos (se listan pero no suman). */
  series: Serie[]
  /** Σ pesoKg×reps; 0 en carga corporal (ahí se muestra `reps`). */
  volumenKg: number
  /** Σ reps. */
  reps: number
}

export interface Sesion {
  fecha: string
  rutina: string | null
  ejercicios: EjercicioResumen[]
  /** En kg canónico; la UI convierte por ejercicio al mostrar. */
  volumenKg: number
  /** Sin calentamientos. */
  series: number
}

/**
 * Agrupa por (s.fecha, rutina) — nunca por ts — sin las 'rechazada'. Las series sin rutina de un día forman una sola
 * sesión. Ejercicios en orden de su primera serie; sesiones de la más reciente a la más antigua.
 */
/** Quita el ruido de flotantes (45.36*10 = 453.59999999999997). */
const redondear = (n: number) => Math.round(n * 100) / 100

export function sesionesDe(series: (Serie & { estado?: string })[], corporales: Set<string> = new Set()): Sesion[] {
  const grupos = new Map<string, { fecha: string; rutina: string | null; series: Serie[] }>()
  for (const s of series) {
    if (s.estado === 'rechazada') continue
    const rutina = s.rutina ?? null
    const clave = JSON.stringify([s.fecha, rutina])
    const g = grupos.get(clave)
    if (g) g.series.push(s)
    else grupos.set(clave, { fecha: s.fecha, rutina, series: [s] })
  }
  const maxTs = (g: { series: Serie[] }) => g.series.reduce((m, s) => (s.ts > m ? s.ts : m), '')
  return [...grupos.values()]
    .sort((a, b) => b.fecha.localeCompare(a.fecha) || maxTs(b).localeCompare(maxTs(a)))
    .map((g) => {
      const porEjercicio = new Map<string, Serie[]>()
      for (const s of [...g.series].sort((a, b) => a.ts.localeCompare(b.ts))) {
        const l = porEjercicio.get(s.ejercicioId)
        if (l) l.push(s)
        else porEjercicio.set(s.ejercicioId, [s])
      }
      const ejercicios = [...porEjercicio].map(([ejercicioId, ss]): EjercicioResumen => {
        const trabajo = ss.filter(cuentaComoTrabajo)
        return {
          ejercicioId,
          series: ss,
          volumenKg: corporales.has(ejercicioId) ? 0 : redondear(trabajo.reduce((t, s) => t + s.pesoKg * s.reps, 0)),
          reps: trabajo.reduce((t, s) => t + s.reps, 0),
        }
      })
      return {
        fecha: g.fecha,
        rutina: g.rutina,
        ejercicios,
        volumenKg: redondear(ejercicios.reduce((t, e) => t + e.volumenKg, 0)),
        series: g.series.filter(cuentaComoTrabajo).length,
      }
    })
}

/** La más reciente con rutina. */
export const ultimaSesionConRutina = (sesiones: Sesion[]): Sesion | null => sesiones.find((s) => s.rutina !== null) ?? null
