import type { Serie } from './api'
import { pesoEnTexto, type Unidad } from './unidades'

export interface Prefill {
  pesoKg: number
  reps: number
  /** Último día distinto de hoy con series de este ejercicio, en orden cronológico. */
  anterior: { fecha: string; series: Serie[] } | null
}

const porTsDesc = (a: Serie, b: Serie) => b.ts.localeCompare(a.ts)
const porTsAsc = (a: Serie, b: Serie) => a.ts.localeCompare(b.ts)

/**
 * Valores iniciales de la Hoja: copia la última serie del ejercicio (hoy incluido),
 * para que lo normal sea solo tocar "Guardar". Sin historial: 0 kg × 10.
 */
export function derivarPrefill(historial: Serie[], hoy: string): Prefill {
  const orden = [...historial].sort(porTsDesc)
  const ultima = orden[0]
  const fechaAnterior = orden.find((s) => s.fecha !== hoy)?.fecha
  return {
    pesoKg: ultima?.pesoKg ?? 0,
    reps: ultima?.reps ?? 10,
    anterior: fechaAnterior
      ? { fecha: fechaAnterior, series: orden.filter((s) => s.fecha === fechaAnterior).sort(porTsAsc) }
      : null,
  }
}

/** Agrupa por ejercicio respetando el orden en que se empezó cada uno. */
export function agruparPorEjercicio<T extends Serie>(series: T[]): { ejercicioId: string; series: T[] }[] {
  const grupos = new Map<string, T[]>()
  for (const s of [...series].sort(porTsAsc)) {
    const g = grupos.get(s.ejercicioId)
    if (g) g.push(s)
    else grupos.set(s.ejercicioId, [s])
  }
  return [...grupos].map(([ejercicioId, series]) => ({ ejercicioId, series }))
}

/** Fecha local del dispositivo en YYYY-MM-DD (la API guarda el día del usuario, no el UTC). */
export function fechaLocal(d = new Date()): string {
  const p = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`
}

/** Suma días a una fecha YYYY-MM-DD sin pasar por zonas horarias. */
export function sumarDias(fecha: string, dias: number): string {
  const [a, m, d] = fecha.split('-').map(Number)
  return fechaLocal(new Date(a, m - 1, d + dias))
}

/** 62.5 × 8 (kg) o 137.8 lb × 8 */
export const formatoSerie = (s: Pick<Serie, 'pesoKg' | 'reps'>, unidad: Unidad = 'kg') =>
  unidad === 'lb' ? `${pesoEnTexto(s.pesoKg, 'lb')} lb × ${s.reps}` : `${s.pesoKg} × ${s.reps}`
