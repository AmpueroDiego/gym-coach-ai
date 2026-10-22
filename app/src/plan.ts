// Rutina arrancada hoy: vive solo en el cliente (una por cuenta), caduca al cambiar el día.
import type { EjercicioRutina, Serie } from './api'

export interface Plan {
  fecha: string
  rutinaId: string
  nombre: string
  ejercicios: EjercicioRutina[]
  /** Rutina propuesta por el coach y aún no guardada: al terminar se ofrece guardarla como nueva. */
  propuesta?: true
  /** Epoch ms al pulsar Empezar. */
  inicio: number
}

/** La API rechaza `rutina` de más de 40 caracteres: un plan así no se podría guardar con las series. */
const MAX_NOMBRE_PLAN = 40

export const clavePlan = (sub: string) => 'gc.plan:' + sub

type Almacen = Pick<Storage, 'getItem' | 'setItem'> | null

const entero = (n: unknown, min: number, max: number) => Number.isInteger(n) && (n as number) >= min && (n as number) <= max

export const esEjercicioRutina = (e: unknown): e is EjercicioRutina => {
  const x = e as Partial<EjercicioRutina> | null
  return (
    typeof x === 'object' && x !== null && typeof x.id === 'string' &&
    entero(x.series, 1, 10) && entero(x.reps, 1, 100) && entero(x.descansoS, 0, 600) &&
    (x.pesoKg === null || (typeof x.pesoKg === 'number' && x.pesoKg >= 0 && x.pesoKg <= 500))
  )
}

export function leerPlan(a: Almacen, sub: string, hoy: string): Plan | null {
  try {
    const p = JSON.parse(a?.getItem(clavePlan(sub)) ?? 'null') as Partial<Plan> | null
    if (
      !p || typeof p.fecha !== 'string' || typeof p.rutinaId !== 'string' || typeof p.nombre !== 'string' || p.nombre.length > MAX_NOMBRE_PLAN ||
      typeof p.inicio !== 'number' || !Number.isFinite(p.inicio) || !Array.isArray(p.ejercicios) || p.ejercicios.length > 15 || !p.ejercicios.every(esEjercicioRutina)
    ) return null
    if (p.fecha !== hoy) return null
    return { fecha: p.fecha, rutinaId: p.rutinaId, nombre: p.nombre, ejercicios: p.ejercicios, inicio: p.inicio, ...(p.propuesta === true ? { propuesta: true as const } : {}) }
  } catch {
    return null
  }
}

export function guardarPlan(a: Almacen, sub: string, p: Plan | null): void {
  try {
    a?.setItem(clavePlan(sub), JSON.stringify(p))
  } catch {
    // Sin almacenamiento: el plan dura solo la sesión.
  }
}

/** Sube (-1) o baja (1) la primera aparición del ejercicio; en los bordes o si no está, devuelve el mismo plan. */
export function moverEnPlan(plan: Plan, ejercicioId: string, dir: -1 | 1): Plan {
  const i = plan.ejercicios.findIndex((e) => e.id === ejercicioId)
  const j = i + dir
  if (i < 0 || j < 0 || j >= plan.ejercicios.length) return plan
  const ejercicios = [...plan.ejercicios]
  ;[ejercicios[i], ejercicios[j]] = [ejercicios[j], ejercicios[i]]
  return { ...plan, ejercicios }
}

/** Primero los ejercicios del plan en su orden (con series o []), después el resto por el ts de su primera serie. */
export function bloquesDelDia<T extends Serie>(
  series: T[],
  plan: EjercicioRutina[],
): { ejercicioId: string; series: T[]; enPlan: boolean; objetivo?: EjercicioRutina }[] {
  const grupos = new Map<string, T[]>()
  for (const s of [...series].sort((a, b) => a.ts.localeCompare(b.ts))) {
    const g = grupos.get(s.ejercicioId)
    if (g) g.push(s)
    else grupos.set(s.ejercicioId, [s])
  }
  const objetivos = new Map<string, EjercicioRutina>()
  for (const e of plan) if (!objetivos.has(e.id)) objetivos.set(e.id, e)
  const delPlan = [...objetivos].map(([id, objetivo]) => ({
    ejercicioId: id, series: grupos.get(id) ?? [], enPlan: true, objetivo,
  }))
  const fuera = [...grupos]
    .filter(([id]) => !objetivos.has(id))
    .map(([id, s]) => ({ ejercicioId: id, series: s, enPlan: false }))
  return [...delPlan, ...fuera]
}
