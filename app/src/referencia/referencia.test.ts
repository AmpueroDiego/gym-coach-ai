/// <reference types="node" />
import { readFileSync } from 'node:fs'
import { afterEach, describe, expect, it } from 'vitest'
import { fijarIdioma } from '../i18n'
import datos from './datos.json'
import { NOMBRE_MUSCULO, NOMBRE_MUSCULO_EN, REF_SHA, equipoDe, fichaDe, nombreMusculo, pasosDe, urlImagen, type MusculoRef } from './referencia'

const catalogo = readFileSync(new URL('../../../api/Catalogo.cs', import.meta.url), 'utf8')
const slugs = [...catalogo.matchAll(/new\("([^"]+)"/g)].map((m) => m[1])
const entradas = Object.entries(datos.ejercicios)
const APROXIMADOS = [
  'jalon-pecho-maquina',
  'remo-bajo-iso-lateral',
  'elevaciones-laterales-maquina',
  'prensa-horizontal',
]

describe('datos de referencia', () => {
  it('cubre exactamente el catálogo', () => {
    expect(slugs.length).toBe(58)
    expect(Object.keys(datos.ejercicios).sort()).toEqual([...slugs].sort())
  })

  it('el SHA del JSON coincide con REF_SHA', () => {
    expect(datos.fuente.sha).toBe(REF_SHA)
  })

  it.each(entradas)('%s: imágenes con formato válido', (_slug, e) => {
    expect(e.imagenes).toHaveLength(2)
    for (const img of e.imagenes) expect(img).toMatch(/^[A-Za-z0-9_-]+\/[01]\.jpg$/)
    for (const img of e.imagenes) expect(img.startsWith(e.refId + '/')).toBe(true)
  })

  it.each(entradas)('%s: músculos conocidos', (_slug, e) => {
    expect(e.principales.length).toBeGreaterThan(0)
    for (const m of [...e.principales, ...e.secundarios])
      expect(Object.hasOwn(NOMBRE_MUSCULO, m as MusculoRef)).toBe(true)
  })

  it.each(entradas)('%s: pasos en español y equipo', (_slug, e) => {
    expect(e.pasos.length).toBeGreaterThanOrEqual(3)
    expect(e.pasos.length).toBeLessThanOrEqual(6)
    for (const p of e.pasos) {
      expect(p.trim()).not.toBe('')
      expect(p.length).toBeLessThanOrEqual(140)
      expect(p).not.toMatch(/\b(the|your|and)\b/i)
    }
    expect(e.equipo.trim()).not.toBe('')
  })

  it.each(entradas)('%s: pasos y equipo en inglés', (_slug, e) => {
    expect(e.pasosEn.length).toBeGreaterThanOrEqual(3)
    expect(e.pasosEn.length).toBeLessThanOrEqual(5)
    for (const p of e.pasosEn) {
      expect(p.trim()).not.toBe('')
      expect(p.length).toBeLessThanOrEqual(160)
      expect(p).not.toMatch(/[áéíóúñ¿¡]/)
      expect(p).not.toMatch(/\b(el|la|los|las|con|tu|del|sin)\b/i)
    }
    expect(e.equipoEn.trim()).not.toBe('')
    expect(e.equipoEn).not.toMatch(/[áéíóúñ]/)
  })

  it('los 4 sin foto exacta están marcados como aproximados y solo ellos', () => {
    for (const s of APROXIMADOS) expect(fichaDe(s)?.aproximada).toBe(true)
    const marcados = entradas.filter(([, e]) => 'aproximada' in e).map(([s]) => s)
    expect(marcados.sort()).toEqual([...APROXIMADOS].sort())
  })
})

describe('fichaDe', () => {
  it('devuelve la ficha de un slug del catálogo', () => {
    expect(fichaDe('prensa')).not.toBeNull()
  })

  it('devuelve null para ids inexistentes o heredados del prototipo', () => {
    expect(fichaDe('toString')).toBeNull()
    expect(fichaDe('')).toBeNull()
    expect(fichaDe('no-existe')).toBeNull()
  })
})

describe('idioma de la ficha', () => {
  afterEach(() => fijarIdioma('es'))
  const ficha = fichaDe('prensa') as NonNullable<ReturnType<typeof fichaDe>>

  it('en es usa los textos en español', () => {
    expect(pasosDe(ficha)).toBe(ficha.pasos)
    expect(equipoDe(ficha)).toBe(ficha.equipo)
    expect(nombreMusculo('chest')).toBe(NOMBRE_MUSCULO.chest)
  })

  it('en en usa pasosEn, equipoEn y músculos en inglés', () => {
    fijarIdioma('en')
    expect(pasosDe(ficha)).toBe(ficha.pasosEn)
    expect(equipoDe(ficha)).toBe(ficha.equipoEn)
    expect(nombreMusculo('chest')).toBe('Chest')
    expect(nombreMusculo('quadriceps')).toBe('Quads')
  })

  it('en en sin pasosEn cae al español', () => {
    fijarIdioma('en')
    const sinEn = { ...ficha, pasosEn: [], equipoEn: '' }
    expect(pasosDe(sinEn)).toBe(ficha.pasos)
    expect(equipoDe(sinEn)).toBe(ficha.equipo)
  })

  it('NOMBRE_MUSCULO_EN cubre los mismos músculos', () => {
    expect(Object.keys(NOMBRE_MUSCULO_EN).sort()).toEqual(Object.keys(NOMBRE_MUSCULO).sort())
  })
})

describe('urlImagen', () => {
  it('apunta al SHA fijo, nunca a main', () => {
    const url = urlImagen('Leg_Press/0.jpg')
    expect(url).toContain(REF_SHA)
    expect(url).not.toContain('/main/')
    expect(url.endsWith('/exercises/Leg_Press/0.jpg')).toBe(true)
  })
})
