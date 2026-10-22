// Última lista de rutinas bajada (por cuenta), para mostrarla sin sesión.
import type { Rutina } from './api'
import { esEjercicioRutina } from './plan'
import { MAX_RUTINAS } from './presentacion'

type Almacen = Pick<Storage, 'getItem' | 'setItem'> | null

export const claveRutinasCache = (sub: string) => 'gc.rutinas:' + sub

export function leerRutinasCache(a: Almacen, sub: string): Rutina[] | null {
  try {
    const crudo: unknown = JSON.parse(a?.getItem(claveRutinasCache(sub)) ?? 'null')
    if (!Array.isArray(crudo)) return null
    return crudo
      .filter(
        (x): x is Rutina =>
          typeof x === 'object' && x !== null && typeof x.id === 'string' && x.id.length <= 100 &&
          typeof x.nombre === 'string' && x.nombre.length <= 40 && typeof x.actualizada === 'string' &&
          Array.isArray(x.ejercicios) && x.ejercicios.length <= 15 && x.ejercicios.every(esEjercicioRutina),
      )
      .slice(0, MAX_RUTINAS)
  } catch {
    return null
  }
}

export function guardarRutinasCache(a: Almacen, sub: string, rs: Rutina[]): void {
  try {
    a?.setItem(claveRutinasCache(sub), JSON.stringify(rs))
  } catch {
    // Sin almacenamiento: solo se pierde el caché.
  }
}
