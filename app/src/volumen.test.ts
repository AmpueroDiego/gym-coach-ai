import { describe, expect, it } from 'vitest'
import type { Serie } from './api'
import { volumenUltimosDias } from './volumen'

const s = (fecha: string, pesoKg: number, reps: number, extra: Partial<Serie> & { estado?: string } = {}) => ({
  id: Math.random().toString(), ejercicioId: 'x', pesoKg, reps, fecha, ts: `${fecha}T12:00:00Z`, ...extra,
})

describe('volumenUltimosDias', () => {
  it('siempre 6 puntos terminando en hoy', () => {
    const p = volumenUltimosDias([], 'x', '2026-10-06', 'kg')
    expect(p.map((x) => x.fecha)).toEqual(['2026-10-01', '2026-10-02', '2026-10-03', '2026-10-04', '2026-10-05', '2026-10-06'])
    expect(p.every((x) => x.valor === 0)).toBe(true)
  })
  it('suma pesoKg×reps del día', () => {
    const p = volumenUltimosDias([s('2026-10-06', 60, 8), s('2026-10-06', 62.5, 6)], 'x', '2026-10-06', 'kg')
    expect(p[5].valor).toBe(855)
  })
  it('en lb convierte la suma', () => {
    expect(volumenUltimosDias([s('2026-10-06', 20.41, 10)], 'x', '2026-10-06', 'lb')[5].valor).toBe(450)
  })
  it('agrupa por fecha, no por ts', () => {
    const p = volumenUltimosDias([s('2026-10-05', 10, 10, { ts: '2026-10-06T02:00:00Z' })], 'x', '2026-10-06', 'kg')
    expect(p[4]).toEqual({ fecha: '2026-10-05', valor: 100 })
    expect(p[5].valor).toBe(0)
  })
  it('deja fuera hoy-6, otros ejercicios y rechazadas', () => {
    const p = volumenUltimosDias(
      [s('2026-09-30', 10, 10), s('2026-10-06', 10, 10, { ejercicioId: 'otro' }), s('2026-10-06', 10, 10, { estado: 'rechazada' })],
      'x', '2026-10-06', 'kg',
    )
    expect(p.every((x) => x.valor === 0)).toBe(true)
  })
  it('cruza mes', () => {
    const p = volumenUltimosDias([s('2026-09-27', 10, 1)], 'x', '2026-10-02', 'kg')
    expect(p[0]).toEqual({ fecha: '2026-09-27', valor: 10 })
  })
  it('carga corporal suma reps', () => {
    expect(volumenUltimosDias([s('2026-10-06', 10, 8), s('2026-10-06', 0, 7)], 'x', '2026-10-06', 'lb', true)[5].valor).toBe(15)
  })
})

describe('volumenUltimosDias con tipos de serie', () => {
  it('el calentamiento no suma; fallo y drop sí', () => {
    const p = volumenUltimosDias(
      [s('2026-10-06', 40, 10, { tipoSerie: 'calentamiento' }), s('2026-10-06', 60, 8, { tipoSerie: 'fallo' }), s('2026-10-06', 50, 6, { tipoSerie: 'drop' })],
      'x', '2026-10-06', 'kg',
    )
    expect(p[5].valor).toBe(780)
    expect(volumenUltimosDias([s('2026-10-06', 0, 10, { tipoSerie: 'calentamiento' })], 'x', '2026-10-06', 'kg', true)[5].valor).toBe(0)
  })
})
