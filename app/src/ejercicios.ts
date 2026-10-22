// Catálogo en el cliente: nombre según preferencia, búsqueda y filtros.
import type { Ejercicio } from './api'
import { idiomaApp, textos, type Idioma, type NombresEj } from './i18n'

type Almacen = Pick<Storage, 'getItem' | 'setItem'> | null

export const claveCatalogo = 'gc.ejercicios'
const MAX_CATALOGO = 500

const texto = (v: unknown) => typeof v === 'string' && v.length <= 100

/** Último catálogo bajado, para mostrarlo sin sesión; null si no hay o está dañado. */
export function leerCatalogo(a: Almacen): Ejercicio[] | null {
  try {
    const crudo: unknown = JSON.parse(a?.getItem(claveCatalogo) ?? 'null')
    if (!Array.isArray(crudo)) return null
    const ejs = crudo
      .filter(
        (x): x is Ejercicio =>
          typeof x === 'object' && x !== null && texto(x.id) && texto(x.nombre) && texto(x.nombreEn) && texto(x.grupo) &&
          (x.equipo === 'maquina' || x.equipo === 'libre') && (x.cargaCorporal === undefined || typeof x.cargaCorporal === 'boolean'),
      )
      .slice(0, MAX_CATALOGO)
    return ejs.length > 0 ? ejs : null
  } catch {
    return null
  }
}

export function guardarCatalogo(a: Almacen, ejs: Ejercicio[]): void {
  try {
    a?.setItem(claveCatalogo, JSON.stringify(ejs.slice(0, MAX_CATALOGO)))
  } catch {
    // Sin almacenamiento: solo se pierde el caché.
  }
}

/** 'app': solo el idioma de la app. 'ambos': el de la app primero ("Press banca · Bench Press" en es). */
export function nombreEjercicio(e: Pick<Ejercicio, 'nombre' | 'nombreEn'>, nombres: NombresEj, idioma: Idioma = idiomaApp()): string {
  const [uno, otro] = idioma === 'en' ? [e.nombreEn, e.nombre] : [e.nombre, e.nombreEn]
  return nombres === 'ambos' ? `${uno} · ${otro}` : uno
}

export interface FiltroEjercicios {
  texto: string
  equipo: 'todos' | 'maquina' | 'libre'
  grupo: 'todos' | string
}

const limpiar = (t: string) => t.normalize('NFD').replace(/[̀-ͯ]/g, '').toLowerCase()

/** Sin tildes ni mayúsculas, en ambos nombres y el grupo; todas las palabras deben coincidir. */
export function filtrarEjercicios(lista: Ejercicio[], f: FiltroEjercicios): Ejercicio[] {
  const palabras = limpiar(f.texto).split(/\s+/).filter(Boolean)
  return lista.filter((e) => {
    if (f.equipo !== 'todos' && e.equipo !== f.equipo) return false
    if (f.grupo !== 'todos' && e.grupo !== f.grupo) return false
    const pajar = limpiar(`${e.nombre} ${e.nombreEn} ${e.grupo} ${propia(GRUPOS_ES, e.grupo)} ${propia(GRUPOS_EN, e.grupo)}`)
    return palabras.every((p) => pajar.includes(p))
  })
}

/** Orden estable de aparición. */
export const gruposDe = (lista: Ejercicio[]): string[] => [...new Set(lista.map((e) => e.grupo))]

const GRUPOS_ES: Record<string, string> = {
  pecho: 'Pecho',
  espalda: 'Espalda',
  hombros: 'Hombros',
  biceps: 'Bíceps',
  triceps: 'Tríceps',
  core: 'Core',
  cuadriceps: 'Cuádriceps',
  isquios: 'Isquios',
  gluteos: 'Glúteos',
  pantorrillas: 'Pantorrillas',
  aductores: 'Aductores',
  otro: 'Otro',
}
const GRUPOS_EN: Record<string, string> = {
  pecho: 'Chest',
  espalda: 'Back',
  hombros: 'Shoulders',
  biceps: 'Biceps',
  triceps: 'Triceps',
  core: 'Core',
  cuadriceps: 'Quads',
  isquios: 'Hamstrings',
  gluteos: 'Glutes',
  pantorrillas: 'Calves',
  aductores: 'Adductors',
  otro: 'Other',
}
const t = textos(GRUPOS_ES, GRUPOS_EN)
// hasOwn: un grupo como 'toString' no debe devolver lo heredado de Object.prototype.
const propia = (m: Record<string, string>, g: string) => (Object.hasOwn(m, g) ? m[g] : '')

/** En el idioma de la app; un grupo desconocido sale con su slug capitalizado. */
export function etiquetaGrupo(g: string): string {
  return propia(t(), g) || g.charAt(0).toUpperCase() + g.slice(1)
}
