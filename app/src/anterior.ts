// Memoria por ejercicio al ejecutar una rutina: lo último que hizo el usuario, fila por fila.
import type { EjercicioRutina, Serie, TipoSerie } from './api'
import { tipoHeredado } from './records'

const MAX_FILAS = 10

/** Todas las series del día más reciente antes de hoy con este ejercicio (por s.fecha, nunca por ts), en orden de ts. */
export function sesionAnterior(series: (Serie & { estado?: string })[], ejercicioId: string, hoy: string): Serie[] {
  const previas = series.filter((s) => s.ejercicioId === ejercicioId && s.fecha < hoy && s.estado !== 'rechazada')
  if (previas.length === 0) return []
  const fecha = previas.reduce((m, s) => (s.fecha > m ? s.fecha : m), '')
  return previas.filter((s) => s.fecha === fecha).sort((a, b) => a.ts.localeCompare(b.ts))
}

export interface FilaSugerida {
  /** Serie del mismo número en la sesión anterior (referencia gris); null = «—». */
  ref: Serie | null
  pesoKg: number | null
  reps: number | null
  /** Solo el calentamiento de la serie del mismo número; fallo y drop no se heredan. */
  tipoSerie?: TipoSerie
}

/**
 * Una fila por serie de la sesión anterior (tope 10); sin historial, objetivo.series filas.
 * Sugerido: la serie anterior del mismo número, si no la última anterior, si no el objetivo.
 */
export function filasSugeridas(anterior: Serie[], objetivo: EjercicioRutina | null): FilaSugerida[] {
  const n = anterior.length > 0 ? Math.min(MAX_FILAS, anterior.length) : Math.min(MAX_FILAS, Math.max(1, objetivo?.series ?? 1))
  return Array.from({ length: n }, (_, i) => sugerida(anterior, objetivo, i))
}

/** En carga corporal, sin peso objetivo se sugiere 0 (sin lastre): así el ✓ funciona sin escribir nada. */
export function objetivoParaSugerir(o: EjercicioRutina, corporal: boolean): EjercicioRutina {
  return corporal && o.pesoKg === null ? { ...o, pesoKg: 0 } : o
}

/** Fila i (también para las agregadas con '+ Serie' más allá de las sugeridas). */
export function sugerida(anterior: Serie[], objetivo: EjercicioRutina | null, i: number): FilaSugerida {
  const ref = anterior[i] ?? null
  const base = ref ?? anterior[anterior.length - 1] ?? null
  const tipoSerie = tipoHeredado(ref?.tipoSerie)
  if (base) return { ref, pesoKg: base.pesoKg, reps: base.reps, ...(tipoSerie ? { tipoSerie } : {}) }
  return { ref: null, pesoKg: objetivo?.pesoKg ?? null, reps: objetivo?.reps ?? null }
}
