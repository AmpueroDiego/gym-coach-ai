import { afterEach, describe, expect, it } from 'vitest'
import {
  claveIdioma, claveNombres, fijarIdioma, guardarIdioma, guardarNombres, idiomaApp, leerPrefsIdioma, localeApp, suscribirIdioma,
  textos,
} from './i18n'

const almacenDe = (m = new Map<string, string>()) => ({ getItem: (k: string) => m.get(k) ?? null, setItem: (k: string, v: string) => void m.set(k, v) })

afterEach(() => fijarIdioma('es'))

describe('leerPrefsIdioma', () => {
  it('migra "ambos" a es + ambos y escribe ambas claves', () => {
    const m = new Map([[claveIdioma, 'ambos']])
    expect(leerPrefsIdioma(almacenDe(m), 'en-US')).toEqual({ idioma: 'es', nombres: 'ambos' })
    expect(m.get(claveIdioma)).toBe('es')
    expect(m.get(claveNombres)).toBe('ambos')
  })
  it('"es" y "en" se respetan; nombres por defecto app', () => {
    expect(leerPrefsIdioma(almacenDe(new Map([[claveIdioma, 'es']])), 'en-US')).toEqual({ idioma: 'es', nombres: 'app' })
    const m = new Map([[claveIdioma, 'en']])
    expect(leerPrefsIdioma(almacenDe(m), 'es-CL')).toEqual({ idioma: 'en', nombres: 'app' })
    expect(m.get(claveNombres)).toBe('app')
  })
  it('conserva nombres = ambos ya guardado', () => {
    const m = new Map([[claveIdioma, 'en'], [claveNombres, 'ambos']])
    expect(leerPrefsIdioma(almacenDe(m), 'es')).toEqual({ idioma: 'en', nombres: 'ambos' })
  })
  it('sin preferencia: navegador en -> en, otro -> es, y se persiste', () => {
    const m = new Map<string, string>()
    expect(leerPrefsIdioma(almacenDe(m), 'en-US').idioma).toBe('en')
    expect(m.get(claveIdioma)).toBe('en')
    const n = new Map<string, string>()
    expect(leerPrefsIdioma(almacenDe(n), 'es-CL').idioma).toBe('es')
    expect(n.get(claveIdioma)).toBe('es')
    expect(leerPrefsIdioma(almacenDe(), '').idioma).toBe('es')
  })
  it('basura cae al navegador', () => {
    const m = new Map([[claveIdioma, 'fr'], [claveNombres, 'x']])
    expect(leerPrefsIdioma(almacenDe(m), 'en-GB')).toEqual({ idioma: 'en', nombres: 'app' })
    expect(m.get(claveIdioma)).toBe('en')
    expect(m.get(claveNombres)).toBe('app')
  })
  it('storage que lanza o ausente: es/app', () => {
    const roto = { getItem: () => { throw new Error('x') }, setItem: () => { throw new Error('x') } }
    expect(leerPrefsIdioma(roto, 'en-US')).toEqual({ idioma: 'es', nombres: 'app' })
    expect(leerPrefsIdioma(null, 'es')).toEqual({ idioma: 'es', nombres: 'app' })
    expect(leerPrefsIdioma(null, 'en-US').idioma).toBe('en')
  })
  it('setItem que lanza no rompe la lectura', () => {
    const a = { getItem: () => null, setItem: () => { throw new Error('lleno') } }
    expect(leerPrefsIdioma(a, 'en')).toEqual({ idioma: 'en', nombres: 'app' })
  })
  it('segunda lectura estable', () => {
    const a = almacenDe(new Map([[claveIdioma, 'ambos']]))
    const uno = leerPrefsIdioma(a, 'en-US')
    expect(leerPrefsIdioma(a, 'en-US')).toEqual(uno)
    const b = almacenDe()
    const dos = leerPrefsIdioma(b, 'en-US')
    expect(leerPrefsIdioma(b, 'es')).toEqual(dos)
  })
  it('guardar ida y vuelta', () => {
    const m = new Map<string, string>()
    guardarIdioma(almacenDe(m), 'en')
    guardarNombres(almacenDe(m), 'ambos')
    expect(leerPrefsIdioma(almacenDe(m), 'es')).toEqual({ idioma: 'en', nombres: 'ambos' })
    expect(() => guardarIdioma(null, 'es')).not.toThrow()
  })
})

describe('textos e idioma', () => {
  const t = textos(
    { titulo: 'Hoy', series: (n: number) => `${n} ${n === 1 ? 'serie' : 'series'}` },
    { titulo: 'Today', series: (n) => `${n} ${n === 1 ? 'set' : 'sets'}` },
  )
  it('sigue a fijarIdioma', () => {
    expect(t().titulo).toBe('Hoy')
    expect(t().series(1)).toBe('1 serie')
    fijarIdioma('en')
    expect(idiomaApp()).toBe('en')
    expect(t().titulo).toBe('Today')
    expect(t().series(3)).toBe('3 sets')
  })
  it('notifica al suscrito, deja de notificar al desuscribir y actualiza <html lang>', () => {
    let n = 0
    const baja = suscribirIdioma(() => n++)
    fijarIdioma('en')
    expect(n).toBe(1)
    if (typeof document !== 'undefined') expect(document.documentElement.lang).toBe('en')
    fijarIdioma('en')
    expect(n).toBe(1)
    baja()
    fijarIdioma('es')
    expect(n).toBe(1)
  })
  it('localeApp', () => {
    expect(localeApp()).toBe('es')
    fijarIdioma('en')
    expect(localeApp()).toBe('en-US')
  })
})
