// Estado de la UI que sobrevive a que el sistema mate la pestaña: una entrada por cuenta, caduca al cambiar el día.
import type { TipoSerie } from './api'
import type { Pestana } from './pantallas/Navegacion'
import type { Unidad } from './unidades'

export interface BorradorFila { peso: string; reps: string; tipo: TipoSerie | null; unidad: Unidad }
/** Índice absoluto de fila -> lo escrito sin guardar. */
export type BorradoresEjercicio = Record<number, BorradorFila>
export interface EstadoUi {
  fecha: string
  pestana: Pestana
  /** ejercicioId de la hoja NUEVA abierta. */
  hoja: string | null
  ultimoEjercicio: string | null
  /** ejercicioId -> borradores de sus filas. */
  filas: Record<string, BorradoresEjercicio>
}

export const claveUi = (sub: string) => 'gc.ui:' + sub

type Almacen = Pick<Storage, 'getItem' | 'setItem'> | null

const PESTANAS: readonly string[] = ['entreno', 'rutinas', 'progreso', 'coach', 'mas']
const TIPOS: readonly string[] = ['calentamiento', 'normal', 'fallo', 'drop']
const MAX_ID = 100
const MAX_EJERCICIOS = 20
const MAX_FILAS = 30

export const uiVacia = (fecha: string): EstadoUi => ({ fecha, pestana: 'entreno', hoja: null, ultimoEjercicio: null, filas: {} })

const esObjeto = (x: unknown): x is Record<string, unknown> => typeof x === 'object' && x !== null && !Array.isArray(x)
const tieneId = (x: unknown): x is string => typeof x === 'string' && x.length <= MAX_ID
const idONull = (x: unknown) => (tieneId(x) ? x : null)

const leerBorrador = (x: unknown): BorradorFila | null => {
  if (!esObjeto(x)) return null
  const { peso, reps, tipo, unidad } = x
  if (typeof peso !== 'string' || peso.length > 10 || typeof reps !== 'string' || reps.length > 5) return null
  if (unidad !== 'kg' && unidad !== 'lb') return null
  if (tipo !== null && !(typeof tipo === 'string' && TIPOS.includes(tipo))) return null
  return { peso, reps, tipo: tipo as TipoSerie | null, unidad }
}

export function leerUi(a: Almacen, sub: string, hoy: string): EstadoUi {
  try {
    const u = JSON.parse(a?.getItem(claveUi(sub)) ?? 'null') as unknown
    if (!esObjeto(u) || u.fecha !== hoy) return uiVacia(hoy)
    // Sin prototipo: un id como '__proto__' o 'toString' es una clave más, no toca Object.prototype.
    const filas: EstadoUi['filas'] = Object.create(null)
    if (esObjeto(u.filas)) {
      // Filtra primero y limita después: basura al principio no desplaza lo válido.
      let n = 0
      for (const [id, porFila] of Object.entries(u.filas)) {
        if (n >= MAX_EJERCICIOS) break
        if (!tieneId(id) || !esObjeto(porFila)) continue
        const borradores: BorradoresEjercicio = Object.create(null)
        for (const [k, v] of Object.entries(porFila)) {
          const i = Number(k)
          const b = leerBorrador(v)
          if (b && Number.isInteger(i) && i >= 0 && i < MAX_FILAS && String(i) === k) borradores[i] = b
        }
        if (Object.keys(borradores).length > 0) {
          filas[id] = borradores
          n++
        }
      }
    }
    return {
      fecha: hoy,
      pestana: typeof u.pestana === 'string' && PESTANAS.includes(u.pestana) ? (u.pestana as Pestana) : 'entreno',
      hoja: idONull(u.hoja),
      ultimoEjercicio: idONull(u.ultimoEjercicio),
      filas,
    }
  } catch {
    return uiVacia(hoy)
  }
}

export function guardarUi(a: Almacen, sub: string, ui: EstadoUi): void {
  try {
    a?.setItem(claveUi(sub), JSON.stringify(ui))
  } catch {
    // Sin almacenamiento: el estado dura solo la sesión.
  }
}

/** Borrador inicial de una fila: en otra unidad el peso no vale (otra escala), pero reps y tipo sí. */
export const borradorInicial = (b: BorradorFila | undefined, unidad: Unidad): Pick<BorradorFila, 'peso' | 'reps' | 'tipo'> =>
  b ? { peso: b.unidad === unidad ? b.peso : '', reps: b.reps, tipo: b.tipo } : { peso: '', reps: '', tipo: null }

/** Filas que exigen los borradores: máximo índice + 1, ignorando huérfanos de filas ya hechas (< desde). */
export const filasPorBorradores = (b: BorradoresEjercicio, desde: number): number =>
  Math.max(0, ...Object.keys(b).map(Number).filter((i) => i >= desde).map((i) => i + 1))

/** Pone (o quita, si b es null o está vacío) el borrador de una fila; no muta la entrada. */
export function conBorrador(filas: EstadoUi['filas'], ejercicioId: string, indice: number, b: BorradorFila | null): EstadoUi['filas'] {
  const vacio = b === null || (b.peso === '' && b.reps === '' && (b.tipo === null || b.tipo === 'normal'))
  const sig: EstadoUi['filas'] = Object.create(null)
  for (const [id, f] of Object.entries(filas)) if (id !== ejercicioId) sig[id] = f
  const previas = Object.hasOwn(filas, ejercicioId) ? filas[ejercicioId] : {}
  const del: BorradoresEjercicio = Object.create(null)
  for (const [k, v] of Object.entries(previas)) if (Number(k) !== indice) del[Number(k)] = v
  if (b && !vacio) del[indice] = b
  if (Object.keys(del).length > 0) sig[ejercicioId] = del
  return sig
}
