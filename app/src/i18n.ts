// Idioma de la app (es/en) sin librerías: store de módulo + un useIdioma() en App que re-renderiza todo el árbol.
import { useSyncExternalStore } from 'react'

export type Idioma = 'es' | 'en'
export type NombresEj = 'app' | 'ambos'

/** Globales del dispositivo, no por cuenta. */
export const claveIdioma = 'gc.idioma'
export const claveNombres = 'gc.nombres'

let actual: Idioma = 'es'
const oyentes = new Set<() => void>()

export const idiomaApp = (): Idioma => actual

/** Único setter: notifica a los suscritos y deja <html lang> al día (lectores de pantalla, guiones). */
export function fijarIdioma(i: Idioma): void {
  if (typeof document !== 'undefined') document.documentElement.lang = i
  if (i === actual) return
  actual = i
  for (const cb of [...oyentes]) cb()
}

export function suscribirIdioma(cb: () => void): () => void {
  oyentes.add(cb)
  return () => void oyentes.delete(cb)
}

export const useIdioma = (): Idioma => useSyncExternalStore(suscribirIdioma, idiomaApp, idiomaApp)

/** Un diccionario por módulo: si a `en` le falta o le sobra una clave, no compila. Se lee con t() en cada render. */
export function textos<T extends object>(es: T, en: NoInfer<T>): () => T {
  return () => (actual === 'en' ? en : es)
}

export const localeApp = (): string => (actual === 'en' ? 'en-US' : 'es')

type Almacen = Pick<Storage, 'getItem' | 'setItem'> | null

/**
 * Lee las preferencias y migra el formato viejo (gc.idioma = 'es'|'en'|'ambos' era el idioma de los NOMBRES).
 * Sin preferencia válida usa el idioma del navegador y lo persiste. Idempotente.
 */
export function leerPrefsIdioma(a: Almacen, lenguaNavegador: string): { idioma: Idioma; nombres: NombresEj } {
  let crudoIdioma: string | null
  let crudoNombres: string | null
  try {
    crudoIdioma = a?.getItem(claveIdioma) ?? null
    crudoNombres = a?.getItem(claveNombres) ?? null
  } catch {
    return { idioma: 'es', nombres: 'app' }
  }
  let idioma: Idioma
  let nombres: NombresEj
  if (crudoIdioma === 'ambos') {
    idioma = 'es'
    nombres = 'ambos'
  } else {
    idioma = crudoIdioma === 'es' || crudoIdioma === 'en' ? crudoIdioma : lenguaNavegador.toLowerCase().startsWith('en') ? 'en' : 'es'
    nombres = crudoNombres === 'ambos' ? 'ambos' : 'app'
  }
  if (crudoIdioma !== idioma) guardarIdioma(a, idioma)
  if (crudoNombres !== nombres) guardarNombres(a, nombres)
  return { idioma, nombres }
}

export function guardarIdioma(a: Almacen, i: Idioma): void {
  try {
    a?.setItem(claveIdioma, i)
  } catch {
    // Sin almacenamiento: dura solo la sesión.
  }
}

export function guardarNombres(a: Almacen, n: NombresEj): void {
  try {
    a?.setItem(claveNombres, n)
  } catch {
    // Sin almacenamiento: dura solo la sesión.
  }
}
