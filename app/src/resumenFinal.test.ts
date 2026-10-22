import { afterEach, describe, expect, it } from 'vitest'
import { fijarIdioma } from './i18n'
import type { Serie } from './api'
import type { Plan } from './plan'
import { ariaRadar, resumenDeRutina } from './resumenFinal'

const HOY = '2026-10-06'
const INICIO = Date.UTC(2026, 9, 6, 10, 0, 0)
const plan = (ids: string[], fecha = HOY): Plan => ({
  fecha,
  rutinaId: 'r',
  nombre: 'R',
  inicio: INICIO,
  ejercicios: ids.map((id) => ({ id, series: 2, reps: 10, pesoKg: null, descansoS: 60 })),
})
let n = 0
const s = (ejercicioId: string, fecha: string, pesoKg: number, reps: number, estado?: string): Serie & { estado?: string } => ({
  id: String(n++),
  ejercicioId,
  fecha,
  pesoKg,
  reps,
  ts: `${fecha}T10:${String(n % 60).padStart(2, '0')}:00.000Z`,
  ...(estado ? { estado } : {}),
})
const kg = () => 'kg' as const
const ahora = INICIO + 3125_000 // 52:05

describe('resumenDeRutina', () => {
  it('sin historial: ejes nuevos y línea base', () => {
    const r = resumenDeRutina([s('a', HOY, 50, 10), s('b', HOY, 20, 10), s('c', HOY, 10, 10)], plan(['a', 'b', 'c']), HOY, kg, ahora)
    expect(r.ejes.every((e) => e.pct === null && e.promedioMes === null)).toBe(true)
    expect(r.radar).toBe(true)
    expect(r.avance).toBe('Primera vez con estos ejercicios: ya tienes tu línea base.')
    expect(r.duracion).toBe('52:05')
    expect(r.texto).not.toMatch(/NaN|Infinity/)
  })

  it('un solo día de historial', () => {
    const r = resumenDeRutina([s('a', '2026-10-01', 50, 10), s('a', HOY, 55, 10)], plan(['a']), HOY, kg, ahora)
    expect(r.ejes[0]).toMatchObject({ hoy: 550, promedioMes: 500, pct: 110 })
    expect(r.radar).toBe(false)
    expect(r.avance).toBe('Contra tu promedio del mes: +10% en a.')
  })

  it('media por día de entreno, no por serie', () => {
    const series = [
      s('a', '2026-10-01', 50, 10),
      s('a', '2026-10-01', 50, 10), // día 1: 1000
      s('a', '2026-10-03', 50, 10), // día 2: 500
      s('a', HOY, 75, 10),
    ]
    const r = resumenDeRutina(series, plan(['a']), HOY, kg, ahora)
    expect(r.ejes[0].promedioMes).toBe(750)
    expect(r.ejes[0].pct).toBe(100)
    expect(r.avance).toBe('En línea con tu promedio del mes (±5%).')
  })

  it('ventana hoy-30..hoy-1 cruzando mes y año', () => {
    const hoy = '2026-01-10'
    const series = [
      s('a', '2025-12-11', 10, 10), // hoy-30: entra
      s('a', '2025-12-10', 999, 10), // hoy-31: fuera
      s('a', hoy, 10, 10),
    ]
    const r = resumenDeRutina(series, plan(['a'], hoy), hoy, kg, ahora)
    expect(r.ejes[0].promedioMes).toBe(100)
  })

  it('excluye rechazadas hoy y en el historial', () => {
    const series = [s('a', '2026-10-01', 100, 10, 'rechazada'), s('a', HOY, 10, 10), s('a', HOY, 100, 10, 'rechazada')]
    const r = resumenDeRutina(series, plan(['a']), HOY, kg, ahora)
    expect(r.ejes[0]).toMatchObject({ hoy: 100, promedioMes: null, pct: null })
  })

  it('carga corporal por reps', () => {
    const r = resumenDeRutina([s('d', '2026-10-02', 0, 10), s('d', HOY, 0, 12)], plan(['d']), HOY, kg, ahora, new Set(['d']))
    expect(r.ejes[0]).toMatchObject({ hoy: 12, promedioMes: 10, pct: 120 })
    expect(r.volumen).toBe('12 reps')
  })

  it('ejercicio agregado sobre la marcha cuenta como eje', () => {
    const p = plan(['a'])
    p.ejercicios.push({ id: 'z', series: 3, reps: 10, pesoKg: null, descansoS: 90 })
    const r = resumenDeRutina([s('a', HOY, 10, 10), s('z', HOY, 10, 10)], p, HOY, kg, ahora)
    expect(r.ejes.map((e) => e.ejercicioId)).toEqual(['a', 'z'])
  })

  it('lb solo cambia la etiqueta; el % sale en kg', () => {
    const series = [s('a', '2026-10-01', 45.36, 10), s('a', HOY, 45.36, 10)]
    const enKg = resumenDeRutina(series, plan(['a']), HOY, kg, ahora)
    const enLb = resumenDeRutina(series, plan(['a']), HOY, () => 'lb', ahora)
    expect(enLb.ejes).toEqual(enKg.ejes)
    expect(enKg.volumen).toBe('454 kg')
    expect(enLb.volumen).toBe('1000 lb'.replace('1000', (1000).toLocaleString('es')))
  })

  it('1–2 ejercicios: sin radar', () => {
    const r = resumenDeRutina([s('a', HOY, 10, 10), s('b', HOY, 10, 10)], plan(['a', 'b']), HOY, kg, ahora)
    expect(r.radar).toBe(false)
  })

  it('ids repetidos en el plan = un eje; ejes sin series hoy no aparecen', () => {
    const r = resumenDeRutina([s('a', HOY, 10, 10)], plan(['a', 'a', 'b']), HOY, kg, ahora)
    expect(r.ejes.map((e) => e.ejercicioId)).toEqual(['a'])
  })

  it('promedio 0 => pct null, sin NaN', () => {
    const r = resumenDeRutina([s('a', '2026-10-01', 0, 10), s('a', HOY, 10, 10)], plan(['a']), HOY, kg, ahora)
    expect(r.ejes[0].pct).toBeNull()
    expect(r.texto + ariaRadar(r.ejes, (id) => id)).not.toMatch(/NaN|Infinity/)
  })

  it('máximo 3 mejoras y 3 bajadas, ordenadas por pct real con orden estable', () => {
    const ids = ['a', 'b', 'c', 'd', 'e', 'f', 'g', 'h']
    const hoyKg = [300, 200, 200, 150, 90, 50, 80, 60]
    const series = ids.flatMap((id, i) => [s(id, '2026-10-01', 10, 10), s(id, HOY, hoyKg[i], 1)])
    const r = resumenDeRutina(series, plan(ids), HOY, kg, ahora)
    expect(r.avance).toBe('Contra tu promedio del mes: +200% en a, +100% en b, +100% en c; −50% en f, −40% en h, −20% en g.')
    expect(ariaRadar(r.ejes, (id) => id)).toContain('a: 300% del promedio')
  })

  it('más de 15 ejes se cortan', () => {
    const ids = Array.from({ length: 17 }, (_, i) => 'e' + i)
    const r = resumenDeRutina(ids.map((id) => s(id, HOY, 10, 10)), plan(ids), HOY, kg, ahora)
    expect(r.ejes).toHaveLength(15)
  })
})

describe('calentamiento en el resumen', () => {
  it('no suma volumen ni cuenta para series, ejes ni días del promedio', () => {
    const cal = (id: string, fecha: string) => ({ ...s(id, fecha, 40, 10), tipoSerie: 'calentamiento' as const })
    const r = resumenDeRutina(
      [cal('a', '2026-10-02'), s('a', '2026-10-01', 50, 10), cal('a', HOY), s('a', HOY, 55, 10), cal('b', HOY)],
      plan(['a', 'b']), HOY, kg, ahora,
    )
    expect(r.ejes).toHaveLength(1)
    expect(r.ejes[0]).toMatchObject({ hoy: 550, promedioMes: 500, pct: 110 })
    expect(r.series).toBe('1/4')
    expect(r.volumen).toBe('550 kg')
  })
})

describe('resumen en inglés', () => {
  afterEach(() => fijarIdioma('es'))
  it('avance y texto en inglés', () => {
    fijarIdioma('en')
    const r = resumenDeRutina([s('a', '2026-10-01', 50, 10), s('a', HOY, 55, 10)], plan(['a']), HOY, kg, ahora)
    expect(r.avance).toBe('Versus your monthly average: +10% on a.')
    expect(r.texto).toContain('Sets ')
    expect(r.texto).toContain('Exercises ')
    expect(r.texto).not.toContain('Series ')
    expect(resumenDeRutina([], plan(['a']), HOY, kg, ahora).avance).toBe('No sets in this routine today.')
  })
  it('aria del radar en inglés', () => {
    fijarIdioma('en')
    expect(ariaRadar([{ ejercicioId: 'a', hoy: 1, promedioMes: 1, pct: 110 }, { ejercicioId: 'b', hoy: 1, promedioMes: null, pct: null }], (id) => id)).toBe(
      'Today versus your average for the last month. a: 110% of average; b: new.',
    )
  })
})
