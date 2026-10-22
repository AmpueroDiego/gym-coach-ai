import { afterEach, describe, expect, it } from 'vitest'
import { fijarIdioma } from './i18n'
import type { PerfilCoach } from './api'
import { EQUIPOS, EXPERIENCIAS, alturaDeTexto, diasDeTexto, armarPerfil, minutosDeTexto, pesoDeTexto, puedeGuardar, textoPerfil, validarPerfil } from './perfilCoach'

const base: PerfilCoach = { minutosDisponibles: null, objetivo: null, notas: [], alturaCm: null, pesoCorporalKg: null }
const campos = (p: PerfilCoach) => validarPerfil(p).map((e) => e.campo)

describe('validarPerfil', () => {
  it('acepta el perfil vacío y los límites de minutos', () => {
    expect(validarPerfil(base)).toEqual([])
    expect(campos({ ...base, minutosDisponibles: 10 })).toEqual([])
    expect(campos({ ...base, minutosDisponibles: 180 })).toEqual([])
  })
  it('rechaza minutos fuera de 10-180 o no enteros', () => {
    for (const m of [9, 181, 45.5, NaN]) expect(campos({ ...base, minutosDisponibles: m })).toEqual(['minutosDisponibles'])
  })
  it('objetivo: 200 sí, 201 no, vacío o con salto de línea no', () => {
    expect(campos({ ...base, objetivo: 'a'.repeat(200) })).toEqual([])
    expect(campos({ ...base, objetivo: 'a'.repeat(201) })).toEqual(['objetivo'])
    expect(campos({ ...base, objetivo: '   ' })).toEqual(['objetivo'])
    expect(campos({ ...base, objetivo: 'a\nb' })).toEqual(['objetivo'])
  })
  it('notas: 10 sí, 11 no; 201 caracteres, salto de línea, bidi y ancho cero no', () => {
    expect(campos({ ...base, notas: Array(10).fill('x') })).toEqual([])
    expect(campos({ ...base, notas: Array(11).fill('x') })).toEqual(['notas'])
    expect(campos({ ...base, notas: ['a'.repeat(201)] })).toEqual(['notas'])
    expect(campos({ ...base, notas: ['a\nb'] })).toEqual(['notas'])
    expect(campos({ ...base, notas: ['a‮b'] })).toEqual(['notas'])
    expect(campos({ ...base, notas: ['a​b'] })).toEqual(['notas'])
  })
})

describe('textoPerfil / minutosDeTexto / armarPerfil', () => {
  it('recorta y valida', () => {
    expect(textoPerfil('  hombro  ')).toBe('hombro')
    expect(textoPerfil('')).toBeNull()
  })
  it('minutos: vacío es null y lo no numérico NaN', () => {
    expect(minutosDeTexto('')).toBeNull()
    expect(minutosDeTexto(' 45 ')).toBe(45)
    expect(minutosDeTexto('4,5')).toBeNaN()
    expect(minutosDeTexto('-5')).toBeNaN()
  })
  it('arma el perfil recortado y reporta errores', () => {
    const ok = armarPerfil('45', ' ganar fuerza ', [' me duele el hombro '], ' 175 ', '78,5')
    expect(ok.perfil).toEqual({
      minutosDisponibles: 45,
      objetivo: 'ganar fuerza',
      notas: ['me duele el hombro'],
      alturaCm: 175,
      pesoCorporalKg: 78.5,
      equipo: null,
    })
    expect(ok.errores).toEqual([])
    expect(armarPerfil('9', '', [], '', '').errores.map((e) => e.campo)).toEqual(['minutosDisponibles'])
    expect(armarPerfil('abc', '', [], '', '').errores).toHaveLength(1)
    expect(armarPerfil('', '', [], '99', '78.55').errores.map((e) => e.campo)).toEqual(['alturaCm', 'pesoCorporalKg'])
    expect(armarPerfil('', '', [], '', '').perfil).toMatchObject({ alturaCm: null, pesoCorporalKg: null })
  })
  it('altura: vacío null, 1-3 dígitos, resto NaN', () => {
    expect(alturaDeTexto('')).toBeNull()
    expect(alturaDeTexto('175')).toBe(175)
    for (const t of ['175.5', '1750', 'abc', '-5']) expect(alturaDeTexto(t)).toBeNaN()
  })
  it('peso: coma o punto, un decimal máximo, resto NaN', () => {
    expect(pesoDeTexto('')).toBeNull()
    expect(pesoDeTexto('78,5')).toBe(78.5)
    expect(pesoDeTexto('78.5')).toBe(78.5)
    expect(pesoDeTexto('80')).toBe(80)
    for (const t of ['78.55', 'abc', '-5', '78,']) expect(pesoDeTexto(t)).toBeNaN()
  })
  it('límites de altura y peso', () => {
    for (const a of [100, 250]) expect(campos({ ...base, alturaCm: a })).toEqual([])
    for (const a of [99, 251, 175.5, NaN]) expect(campos({ ...base, alturaCm: a })).toEqual(['alturaCm'])
    for (const w of [30, 300, 78.5]) expect(campos({ ...base, pesoCorporalKg: w })).toEqual([])
    for (const w of [29.9, 300.1, 78.55, NaN]) expect(campos({ ...base, pesoCorporalKg: w })).toEqual(['pesoCorporalKg'])
  })
})

describe('equipo', () => {
  it('el perfil armado lleva equipo explícito (null borra, no omite)', () => {
    expect(armarPerfil('', '', [], '', '', 'mixto').perfil.equipo).toBe('mixto')
    const sin = armarPerfil('', '', [], '', '').perfil
    expect(Object.hasOwn(sin, 'equipo')).toBe(true)
    expect(sin.equipo).toBeNull()
  })
  it('acepta los tres valores, null y ausente; rechaza otro texto', () => {
    for (const e of ['maquina', 'libre', 'mixto', null, undefined] as const) expect(campos({ ...base, equipo: e })).toEqual([])
    expect(campos({ ...base, equipo: 'pesas' as never })).toEqual(['equipo'])
  })
})

describe('días y experiencia', () => {
  it('diasDeTexto: vacío es null, un dígito es número, el resto NaN', () => {
    expect(diasDeTexto('')).toBeNull()
    expect(diasDeTexto('3')).toBe(3)
    expect(diasDeTexto('0')).toBe(0)
    for (const x of ['8', '3.5', 'a']) expect(diasDeTexto(x)).toSatisfy((n: number | null) => n === 8 || Number.isNaN(n))
  })
  it('validarPerfil rechaza días 0 y 8 y experiencia inválida', () => {
    expect(campos({ ...base, diasPorSemana: 0 })).toEqual(['diasPorSemana'])
    expect(campos({ ...base, diasPorSemana: 8 })).toEqual(['diasPorSemana'])
    expect(campos({ ...base, diasPorSemana: 3, experiencia: 'mas6m' })).toEqual([])
    expect(campos({ ...base, experiencia: 'Nunca' as never })).toEqual(['experiencia'])
  })
  it('armarPerfil: sin tocar, el body no trae las claves; tocados, van (null borra)', () => {
    const con = armarPerfil('', '', [], '', '', null, 'nunca', '4').perfil
    expect(con).toMatchObject({ experiencia: 'nunca', diasPorSemana: 4 })
    const sin = armarPerfil('', '', [], '', '').perfil
    expect(Object.hasOwn(sin, 'experiencia')).toBe(false)
    expect(Object.hasOwn(sin, 'diasPorSemana')).toBe(false)
    const borra = armarPerfil('', '', [], '', '', null, null, '').perfil
    expect(borra.experiencia).toBeNull()
    expect(borra.diasPorSemana).toBeNull()
  })
  it('en inglés', () => {
    fijarIdioma('en')
    expect(EXPERIENCIAS.map((o) => o.texto())).toEqual(['Never trained', 'Less than 6 months', 'More than 6 months', 'Not set'])
    expect(validarPerfil({ ...base, diasPorSemana: 9 })[0].mensaje).toBe('Between 1 and 7 days.')
    fijarIdioma('es')
  })
})

describe('puedeGuardar', () => {
  it('solo con sesión y tras leer el perfil', () => {
    expect(puedeGuardar('listo', true)).toBe(true)
    for (const e of ['sinCargar', 'cargando', 'errorCarga', 'guardando'] as const) expect(puedeGuardar(e, true)).toBe(false)
    expect(puedeGuardar('listo', false)).toBe(false)
  })
})

describe('perfil en inglés', () => {
  afterEach(() => fijarIdioma('es'))
  it('mensajes y opciones de equipo en inglés', () => {
    fijarIdioma('en')
    expect(validarPerfil({ ...base, minutosDisponibles: 5 })[0].mensaje).toBe('Between 10 and 180 minutes.')
    expect(EQUIPOS.map((o) => o.texto())).toEqual(['Machines', 'Free weights', 'Mixed', 'Let it figure out'])
  })
})
