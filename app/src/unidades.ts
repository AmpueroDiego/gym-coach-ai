// Kg/lb en el cliente: la API siempre guarda kg; lb es solo presentación (preferencia por ejercicio).
export type Unidad = 'kg' | 'lb'
export const KG_POR_LB = 0.45359237
/** Salto de los botones +/-. */
export const PASO: Record<Unidad, number> = { kg: 2.5, lb: 5 }

const MAX_KG = 500
const NUMERO = /^(\d+\.?\d*|\.\d+)$/
const redondear = (n: number, dec: number) => Math.round(n * 10 ** dec) / 10 ** dec

export const lbAKg = (lb: number): number => redondear(lb * KG_POR_LB, 2)
export const kgALb = (kg: number): number => redondear(kg / KG_POR_LB, 1)

/** Sin ceros sobrantes: 45 -> '45', 62.5 -> '62.5'. */
export const pesoEnTexto = (kg: number, u: Unidad): string => String(u === 'kg' ? redondear(kg, 2) : kgALb(kg))

const maxEn = (u: Unidad) => (u === 'kg' ? MAX_KG : kgALb(MAX_KG))
const normalizar = (t: string) => t.trim().replace(',', '.')

/**
 * Texto del campo -> kg. NaN si es inválido (vacío, >2 decimales en kg o >1 en lb, fuera de 0-500 kg).
 * Si el texto es el mismo que muestra baseKg, devuelve baseKg sin reconvertir (evita deriva kg<->lb).
 */
export function resolverPesoKg(texto: string, u: Unidad, baseKg: number | null): number {
  const t = normalizar(texto)
  if (!NUMERO.test(t)) return NaN
  if (baseKg !== null && t === pesoEnTexto(baseKg, u)) return baseKg
  const decimales = t.split('.')[1]?.length ?? 0
  if (decimales > (u === 'kg' ? 2 : 1)) return NaN
  const n = Number(t)
  if (n > maxEn(u)) return NaN
  if (u === 'kg') return n
  // El tope en lb (1102.3) redondea a 499.99 kg: se trata como el máximo exacto.
  return n === maxEn('lb') ? MAX_KG : lbAKg(n)
}

/** Sube o baja un paso en la unidad visible; texto inválido cuenta como 0. Limita a 0-500 kg. */
export function sumarPaso(texto: string, u: Unidad, dir: 1 | -1): string {
  const t = normalizar(texto)
  const actual = NUMERO.test(t) ? Number(t) : 0
  const nuevo = Math.min(maxEn(u), Math.max(0, actual + dir * PASO[u]))
  return String(redondear(nuevo, u === 'kg' ? 2 : 1))
}

export const claveUnidades = (sub: string) => 'gc.unidades:' + sub

type Almacen = Pick<Storage, 'getItem' | 'setItem'> | null

/** ejercicioId -> unidad; solo se guarda lb (kg es el valor por defecto). */
export function leerUnidades(a: Almacen, sub: string): Record<string, Unidad> {
  try {
    const crudo: unknown = JSON.parse(a?.getItem(claveUnidades(sub)) ?? '{}')
    if (typeof crudo !== 'object' || crudo === null || Array.isArray(crudo)) return {}
    const salida: Record<string, Unidad> = {}
    for (const [id, v] of Object.entries(crudo)) if (v === 'lb') salida[id] = 'lb'
    return salida
  } catch {
    return {}
  }
}

export function guardarUnidades(a: Almacen, sub: string, m: Record<string, Unidad>): void {
  const soloLb: Record<string, Unidad> = {}
  for (const [id, u] of Object.entries(m)) if (u === 'lb') soloLb[id] = 'lb'
  try {
    a?.setItem(claveUnidades(sub), JSON.stringify(soloLb))
  } catch {
    // Sin almacenamiento: la preferencia dura solo la sesión.
  }
}
