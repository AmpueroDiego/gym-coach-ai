// Descanso programable: todo sale de la hora de fin, así sobrevive a recargas y a la pantalla bloqueada.
import type { EjercicioRutina } from './api'
import { descansoDe, type Descanso } from './contador'

export const PRESETS_DESCANSO = [30, 60, 90, 120, 180] as const
export const PASO_S = 15
export const MIN_DESCANSO_S = 15
export const MAX_DESCANSO_S = 600
/** Si la app estuvo cerrada y el descanso terminó hace más que esto, no tiene sentido sonar. */
export const GRACIA_AVISO_MS = 120000
const TOLERANCIA_FUTURO_MS = 60_000
const MAX_PREFERENCIAS = 100

export type DescansoActivo = Descanso & { ejercicioId: string }

export const claveDescansos = (sub: string) => 'gc.descansos:' + sub
export const claveDescansoActivo = (sub: string) => 'gc.descansoActivo:' + sub

type Almacen = Pick<Storage, 'getItem' | 'setItem' | 'removeItem'> | null

// localStorage puede no existir o lanzar (modo privado, política del navegador).
function almacenLocal(): Almacen {
  try {
    return typeof localStorage === 'undefined' ? null : localStorage
  } catch {
    return null
  }
}

const esDuracionValida = (n: unknown): n is number =>
  typeof n === 'number' && Number.isInteger(n) && n >= MIN_DESCANSO_S && n <= MAX_DESCANSO_S

export const finDe = (d: Descanso): number => d.inicio + d.duracionS * 1000

/** ±paso sobre la hora de fin; restar nunca la deja antes de `ahora` (termina ya). Máximo 600 s en total. */
export function ajustar<T extends Descanso>(d: T, deltaS: number, ahora: number): T {
  const fin = finDe(d) + deltaS * 1000
  if (fin <= ahora) return { ...d, inicio: ahora, duracionS: 0 }
  return { ...d, duracionS: Math.min(MAX_DESCANSO_S, (fin - d.inicio) / 1000) }
}

/** El preset es la duración TOTAL desde el inicio (que no cambia), no "restante". */
export function elegirPreset<T extends Descanso>(d: T, s: number, _ahora: number): T {
  return { ...d, duracionS: Math.min(MAX_DESCANSO_S, Math.max(MIN_DESCANSO_S, s)) }
}

export function reiniciar<T extends Descanso>(d: T, ahora: number): T {
  return { ...d, inicio: ahora }
}

/** Suena si terminó (no antes) hace como mucho la gracia; si fue antes, se descarta en silencio. */
export function debeAvisar(d: Descanso, ahora: number): boolean {
  const pasado = ahora - finDe(d)
  return pasado >= 0 && pasado <= GRACIA_AVISO_MS
}

export function leerDescansoActivo(sub: string, a: Almacen = almacenLocal()): DescansoActivo | null {
  try {
    const crudo: unknown = JSON.parse(a?.getItem(claveDescansoActivo(sub)) ?? 'null')
    if (typeof crudo !== 'object' || crudo === null || Array.isArray(crudo)) return null
    const { ejercicioId, inicio, duracionS } = crudo as Record<string, unknown>
    if (typeof ejercicioId !== 'string' || !ejercicioId) return null
    if (typeof inicio !== 'number' || !Number.isFinite(inicio) || inicio <= 0) return null
    // Un inicio en el futuro (reloj movido) mostraría un descanso eterno.
    if (inicio > Date.now() + TOLERANCIA_FUTURO_MS) return null
    // 0 es válido: "−15" cerca del final lo deja terminando ya.
    if (typeof duracionS !== 'number' || !Number.isFinite(duracionS) || duracionS < 0 || duracionS > MAX_DESCANSO_S) return null
    return { ejercicioId, inicio, duracionS }
  } catch {
    return null
  }
}

export function guardarDescansoActivo(sub: string, d: DescansoActivo | null, a: Almacen = almacenLocal()): void {
  try {
    if (d) a?.setItem(claveDescansoActivo(sub), JSON.stringify(d))
    else a?.removeItem(claveDescansoActivo(sub))
  } catch {
    // Sin almacenamiento: el descanso dura solo la sesión.
  }
}

/** ejercicioId -> segundos (enteros 15..600). Lo inválido se descarta; máximo 100 (se quedan las últimas). */
export function leerDescansos(sub: string, a: Almacen = almacenLocal()): Record<string, number> {
  try {
    const crudo: unknown = JSON.parse(a?.getItem(claveDescansos(sub)) ?? '{}')
    if (typeof crudo !== 'object' || crudo === null || Array.isArray(crudo)) return {}
    const validas = Object.entries(crudo).filter(([id, v]) => id !== '' && esDuracionValida(v))
    return Object.fromEntries(validas.slice(-MAX_PREFERENCIAS)) as Record<string, number>
  } catch {
    return {}
  }
}

export function guardarDescansos(sub: string, m: Record<string, number>, a: Almacen = almacenLocal()): void {
  const validas = Object.entries(m).filter(([id, v]) => id !== '' && esDuracionValida(v))
  try {
    a?.setItem(claveDescansos(sub), JSON.stringify(Object.fromEntries(validas.slice(-MAX_PREFERENCIAS))))
  } catch {
    // Sin almacenamiento: la preferencia dura solo la sesión.
  }
}

/** Anota la última elección del ejercicio (la deja al final, así al llegar al tope se van las más viejas). */
export function recordarDescanso(prefs: Record<string, number>, ejercicioId: string, s: number): Record<string, number> {
  if (!ejercicioId || !esDuracionValida(s)) return prefs
  const { [ejercicioId]: _viejo, ...resto } = prefs
  return Object.fromEntries(Object.entries({ ...resto, [ejercicioId]: s }).slice(-MAX_PREFERENCIAS))
}

/** Precedencia: lo último que se eligió para el ejercicio > descansoS de la rutina (0 = sin descanso) > global. */
export function descansoPara(
  ejercicioId: string,
  prefs: Record<string, number>,
  plan: EjercicioRutina[] | null,
  defectoS: number,
): number {
  const pref = Object.hasOwn(prefs, ejercicioId) ? prefs[ejercicioId] : undefined
  if (esDuracionValida(pref)) return pref
  return descansoDe(ejercicioId, plan, defectoS)
}
