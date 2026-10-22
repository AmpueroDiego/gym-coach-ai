import { afterEach, describe, expect, it } from 'vitest'
import type { Ejercicio } from './api'
import {
  claveCatalogo, etiquetaGrupo, filtrarEjercicios, gruposDe, guardarCatalogo, leerCatalogo, nombreEjercicio,
} from './ejercicios'
import { fijarIdioma } from './i18n'

afterEach(() => fijarIdioma('es'))

const e = (id: string, nombre: string, nombreEn: string, grupo: string, equipo: 'maquina' | 'libre'): Ejercicio => ({
  id, nombre, nombreEn, grupo, equipo,
})
const lista = [
  e('jalon', 'Jalón al pecho', 'Lat pulldown', 'espalda', 'maquina'),
  e('pec', 'Mariposa', 'Pec deck', 'pecho', 'maquina'),
  e('banca', 'Press de banca', 'Bench press', 'pecho', 'libre'),
  e('curl', 'Curl de bíceps', 'Biceps curl', 'biceps', 'libre'),
]
const todos = { texto: '', equipo: 'todos', grupo: 'todos' } as const
const ids = (l: Ejercicio[]) => l.map((x) => x.id)

describe('filtrarEjercicios', () => {
  it('busca en ambos idiomas, sin tildes ni mayúsculas', () => {
    expect(ids(filtrarEjercicios(lista, { ...todos, texto: 'lat pull' }))).toEqual(['jalon'])
    expect(ids(filtrarEjercicios(lista, { ...todos, texto: 'JALON' }))).toEqual(['jalon'])
    expect(ids(filtrarEjercicios(lista, { ...todos, texto: 'pec deck' }))).toEqual(['pec'])
    expect(ids(filtrarEjercicios(lista, { ...todos, texto: 'mariposa' }))).toEqual(['pec'])
    expect(ids(filtrarEjercicios(lista, { ...todos, texto: 'biceps' }))).toEqual(['curl'])
  })
  it('todas las palabras deben coincidir', () => {
    expect(ids(filtrarEjercicios(lista, { ...todos, texto: 'press pecho' }))).toEqual(['banca'])
    expect(filtrarEjercicios(lista, { ...todos, texto: 'press jalon' })).toEqual([])
  })
  it('combina con filtros; texto vacío devuelve todo lo filtrado', () => {
    expect(ids(filtrarEjercicios(lista, { ...todos, equipo: 'libre' }))).toEqual(['banca', 'curl'])
    expect(ids(filtrarEjercicios(lista, { ...todos, grupo: 'pecho' }))).toEqual(['pec', 'banca'])
    expect(ids(filtrarEjercicios(lista, { texto: 'press', equipo: 'maquina', grupo: 'pecho' }))).toEqual([])
    expect(filtrarEjercicios(lista, todos)).toHaveLength(4)
  })
})

describe('utilidades', () => {
  it('gruposDe conserva el orden de aparición', () => {
    expect(gruposDe(lista)).toEqual(['espalda', 'pecho', 'biceps'])
  })
  it('etiquetaGrupo', () => {
    expect(etiquetaGrupo('biceps')).toBe('Bíceps')
    expect(etiquetaGrupo('cuadriceps')).toBe('Cuádriceps')
    expect(etiquetaGrupo('gluteos')).toBe('Glúteos')
    expect(etiquetaGrupo('espalda')).toBe('Espalda')
  })
  it('etiquetaGrupo en inglés', () => {
    fijarIdioma('en')
    expect(etiquetaGrupo('pecho')).toBe('Chest')
    expect(etiquetaGrupo('cuadriceps')).toBe('Quads')
    expect(etiquetaGrupo('otro')).toBe('Other')
    expect(etiquetaGrupo('desconocido')).toBe('Desconocido')
    expect(etiquetaGrupo('toString')).toBe('ToString')
  })
  it('nombreEjercicio app/ambos en es', () => {
    expect(nombreEjercicio(lista[0], 'app')).toBe('Jalón al pecho')
    expect(nombreEjercicio(lista[0], 'ambos')).toBe('Jalón al pecho · Lat pulldown')
  })
  it('nombreEjercicio app/ambos en en: el idioma de la app va primero', () => {
    fijarIdioma('en')
    expect(nombreEjercicio(lista[0], 'app')).toBe('Lat pulldown')
    expect(nombreEjercicio(lista[0], 'ambos')).toBe('Lat pulldown · Jalón al pecho')
    expect(nombreEjercicio(lista[0], 'app', 'es')).toBe('Jalón al pecho')
  })
  it('filtrarEjercicios encuentra "chest" y "pecho" en cualquier idioma de la app', () => {
    expect(ids(filtrarEjercicios(lista, { ...todos, texto: 'chest' }))).toEqual(['pec', 'banca'])
    fijarIdioma('en')
    expect(ids(filtrarEjercicios(lista, { ...todos, texto: 'pecho' }))).toEqual(['jalon', 'pec', 'banca'])
  })
})

describe('catálogo en caché', () => {
  const almacenDe = (m = new Map<string, string>()) => ({ getItem: (k: string) => m.get(k) ?? null, setItem: (k: string, v: string) => void m.set(k, v) })

  it('ida y vuelta', () => {
    const a = almacenDe()
    guardarCatalogo(a, [...lista, { ...e('dom', 'Dominadas', 'Pull-up', 'espalda', 'libre'), cargaCorporal: true }])
    expect(leerCatalogo(a)).toHaveLength(5)
    expect(leerCatalogo(a)![4].cargaCorporal).toBe(true)
  })

  it('filtra inválidos', () => {
    const malos = [null, 3, { ...lista[1], equipo: 'otro' }, { ...lista[2], id: 5 }, { ...lista[3], grupo: 'x'.repeat(101) }, { ...lista[3], cargaCorporal: 'si' }]
    const m = new Map([[claveCatalogo, JSON.stringify([lista[0], ...malos])]])
    expect(leerCatalogo(almacenDe(m))).toEqual([lista[0]])
  })

  it('vacío, roto o sin almacén: null', () => {
    expect(leerCatalogo(almacenDe())).toBeNull()
    expect(leerCatalogo(almacenDe(new Map([[claveCatalogo, '[]']])))).toBeNull()
    expect(leerCatalogo(almacenDe(new Map([[claveCatalogo, '{no json']])))).toBeNull()
    expect(leerCatalogo(null)).toBeNull()
  })
})
