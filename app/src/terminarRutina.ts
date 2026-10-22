// Ejercicios agregados sobre la marcha a la rutina de hoy, y cómo pasarlos a la rutina guardada al terminar.
import type { EjercicioRutina, Rutina, Serie } from './api'
import type { Plan } from './plan'

export const MAX_EJERCICIOS_PLAN = 15
const MAX_SERIES = 10

/** Error como datos: quien lo pinta lo traduce (el texto ya armado no sigue al idioma). */
export type ErrorTope = { error: 'demasiados'; n: number; max: number }

type Almacen = Pick<Storage, 'getItem' | 'setItem'> | null

// Aparte del plan: leerPlan rearma el Plan campo a campo y perdería un campo extra.
export const claveAgregados = (sub: string) => 'gc.planAgregados:' + sub

/** Ids agregados hoy; caducan con la fecha (como el plan). */
export function leerAgregados(a: Almacen, sub: string, hoy: string): string[] {
  try {
    const v = JSON.parse(a?.getItem(claveAgregados(sub)) ?? 'null') as { fecha?: unknown; ids?: unknown } | null
    if (!v || v.fecha !== hoy || !Array.isArray(v.ids)) return []
    return [...new Set(v.ids.filter((x): x is string => typeof x === 'string'))]
  } catch {
    return []
  }
}

export function guardarAgregados(a: Almacen, sub: string, v: { fecha: string; ids: string[] } | null): void {
  try {
    a?.setItem(claveAgregados(sub), JSON.stringify(v))
  } catch {
    // Sin almacenamiento: dura solo la sesión.
  }
}

/** Añade al final con objetivo por defecto. Sin cambios si ya está o si el plan llegó al tope. */
export function agregarAlPlan(plan: Plan, id: string, descansoS: number): Plan {
  if (plan.ejercicios.some((e) => e.id === id) || plan.ejercicios.length >= MAX_EJERCICIOS_PLAN) return plan
  return { ...plan, ejercicios: [...plan.ejercicios, { id, series: 3, reps: 10, pesoKg: null, descansoS }] }
}

/**
 * Agregados con al menos una serie hoy (sin rechazadas), listos para la rutina: series = nº hechas (máx 10),
 * reps y pesoKg de la última de hoy, descanso el del plan. Los que ya estén en la rutina original no se repiten.
 */
export function nuevosParaRutina(
  series: (Serie & { estado?: string })[],
  plan: Plan,
  agregados: string[],
  enRutina: string[] = [],
): EjercicioRutina[] {
  const ya = new Set(enRutina)
  const salida: EjercicioRutina[] = []
  for (const id of new Set(agregados)) {
    if (ya.has(id)) continue
    const hechas = series
      .filter((s) => s.ejercicioId === id && s.fecha === plan.fecha && s.estado !== 'rechazada')
      .sort((a, b) => a.ts.localeCompare(b.ts))
    if (hechas.length === 0) continue
    const ultima = hechas[hechas.length - 1]
    const descansoS = plan.ejercicios.find((e) => e.id === id)?.descansoS ?? 90
    // Topes del servidor (series 1–10, reps 1–100): un dato raro en local no debe tumbar el PUT., pesoKg 0–500 (si no, null).
    const reps = Math.min(100, Math.max(1, Math.round(ultima.reps)))
    const pesoKg = Number.isFinite(ultima.pesoKg) && ultima.pesoKg >= 0 && ultima.pesoKg <= 500 ? ultima.pesoKg : null
    salida.push({ id, series: Math.min(MAX_SERIES, hechas.length), reps, pesoKg, descansoS })
    ya.add(id)
  }
  return salida
}

/** La rutina original con los nuevos al final; error si pasaría el tope de ejercicios. */
export function rutinaConNuevos(
  r: Rutina,
  nuevos: EjercicioRutina[],
): ErrorTope | Pick<Rutina, 'id' | 'nombre' | 'ejercicios'> {
  const ids = new Set(r.ejercicios.map((e) => e.id))
  const ejercicios = [...r.ejercicios, ...nuevos.filter((n) => !ids.has(n.id))]
  if (ejercicios.length > MAX_EJERCICIOS_PLAN)
    return { error: 'demasiados', n: ejercicios.length, max: MAX_EJERCICIOS_PLAN }
  return { id: r.id, nombre: r.nombre, ejercicios }
}

// Orden con el que arrancó la rutina de hoy: para saber al terminar si el usuario la reordenó. Caduca con la fecha.
export const claveOrden = (sub: string) => 'gc.planOrden:' + sub

export function leerOrdenInicial(a: Almacen, sub: string, hoy: string): string[] | null {
  try {
    const v = JSON.parse(a?.getItem(claveOrden(sub)) ?? 'null') as { fecha?: unknown; ids?: unknown } | null
    if (!v || v.fecha !== hoy || !Array.isArray(v.ids)) return null
    return v.ids.filter((x): x is string => typeof x === 'string')
  } catch {
    return null
  }
}

export function guardarOrdenInicial(a: Almacen, sub: string, v: { fecha: string; ids: string[] } | null): void {
  try {
    a?.setItem(claveOrden(sub), JSON.stringify(v))
  } catch {
    // Sin almacenamiento: no se ofrecerá guardar el orden.
  }
}

/** ¿El orden de los ejercicios de la rutina original cambió en el plan? Los agregados no cuentan; sin orden inicial, no. */
export function ordenCambiado(inicial: string[] | null, plan: Plan): boolean {
  if (!inicial) return false
  const enInicial = new Set(inicial)
  const actual = [...new Set(plan.ejercicios.map((e) => e.id))].filter((id) => enInicial.has(id))
  const enActual = new Set(actual)
  const base = [...new Set(inicial)].filter((id) => enActual.has(id))
  return actual.some((id, i) => id !== base[i])
}

/**
 * La rutina con los cambios que el usuario aceptó: `nuevos` al final (como rutinaConNuevos) y, con `orden`,
 * los ejercicios en el orden del plan (los que el plan no tiene quedan al final, en su orden).
 */
export function rutinaConCambios(
  r: Rutina,
  nuevos: EjercicioRutina[],
  orden: string[] | null,
): ErrorTope | Pick<Rutina, 'id' | 'nombre' | 'ejercicios'> {
  const base = rutinaConNuevos(r, nuevos)
  if ('error' in base || !orden) return base
  const pos = new Map<string, number>()
  orden.forEach((id, i) => pos.has(id) || pos.set(id, i))
  const rank = (id: string) => pos.get(id) ?? orden.length
  // sort es estable: lo que el plan no conoce conserva su orden relativo.
  return { ...base, ejercicios: [...base.ejercicios].sort((a, b) => rank(a.id) - rank(b.id)) }
}

/** Rutina nueva con lo que quedó del plan de hoy (agregados y orden incluidos), para una propuesta del coach. */
export const rutinaNuevaDePlan = (plan: Plan, id: string): Pick<Rutina, 'id' | 'nombre' | 'ejercicios'> => ({
  id,
  nombre: plan.nombre,
  ejercicios: plan.ejercicios,
})
