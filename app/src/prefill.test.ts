import { describe, expect, it } from 'vitest'
import type { Serie } from './api'
import { agruparPorEjercicio, derivarPrefill, formatoSerie, sumarDias } from './prefill'

const HOY = '2026-10-06'
let n = 0
const s = (fecha: string, hora: string, pesoKg: number, reps: number, ejercicioId = 'press-banca'): Serie => ({
  id: `id-${n++}`,
  ejercicioId,
  pesoKg,
  reps,
  fecha,
  ts: `${fecha}T${hora}:00.000Z`,
})

describe('derivarPrefill', () => {
  it('sin historial: 0 kg × 10 y sin anterior', () => {
    expect(derivarPrefill([], HOY)).toEqual({ pesoKg: 0, reps: 10, anterior: null })
  })

  it('solo hoy: copia la última de hoy y no hay anterior', () => {
    const p = derivarPrefill([s(HOY, '10:05', 62.5, 7), s(HOY, '10:00', 60, 8)], HOY)
    expect(p).toMatchObject({ pesoKg: 62.5, reps: 7, anterior: null })
  })

  it('hoy + días previos: prefill de hoy, anterior = último día distinto de hoy', () => {
    const p = derivarPrefill(
      [s(HOY, '10:00', 65, 6), s('2026-10-03', '18:00', 60, 8), s('2026-09-30', '18:00', 57.5, 8)],
      HOY,
    )
    expect(p.pesoKg).toBe(65)
    expect(p.reps).toBe(6)
    expect(p.anterior?.fecha).toBe('2026-10-03')
    expect(p.anterior?.series.map((x) => x.pesoKg)).toEqual([60])
  })

  it('sin series hoy: copia la última del día anterior', () => {
    const p = derivarPrefill([s('2026-10-03', '18:10', 60, 7), s('2026-10-03', '18:00', 60, 8)], HOY)
    expect(p).toMatchObject({ pesoKg: 60, reps: 7 })
  })

  it('varias series el mismo día: anterior en orden cronológico aunque llegue desordenado', () => {
    const p = derivarPrefill(
      [s('2026-10-03', '18:00', 60, 8), s('2026-10-03', '18:10', 60, 7), s('2026-10-03', '18:05', 60, 8)],
      HOY,
    )
    expect(p.anterior?.series.map((x) => x.reps)).toEqual([8, 8, 7])
    expect(p.reps).toBe(7)
  })
})

describe('agruparPorEjercicio', () => {
  it('agrupa en el orden en que se empezó cada ejercicio', () => {
    const g = agruparPorEjercicio([
      s(HOY, '10:10', 60, 8, 'press-banca'),
      s(HOY, '10:00', 100, 5, 'sentadilla'),
      s(HOY, '10:20', 100, 5, 'sentadilla'),
    ])
    expect(g.map((x) => [x.ejercicioId, x.series.length])).toEqual([
      ['sentadilla', 2],
      ['press-banca', 1],
    ])
  })
})

describe('sumarDias', () => {
  it('cruza meses', () => {
    expect(sumarDias('2026-10-01', -1)).toBe('2026-09-30')
    expect(sumarDias('2026-10-06', -29)).toBe('2026-09-07')
  })
})

describe('formatoSerie', () => {
  it('kg por defecto y lb con unidad', () => {
    expect(formatoSerie({ pesoKg: 62.5, reps: 8 })).toBe('62.5 × 8')
    expect(formatoSerie({ pesoKg: 62.5, reps: 8 }, 'kg')).toBe('62.5 × 8')
    expect(formatoSerie({ pesoKg: 20.41, reps: 10 }, 'lb')).toBe('45 lb × 10')
  })
})
