import { describe, expect, it } from 'vitest'
import type { EjercicioRutina, Serie } from './api'
import { filasSugeridas, objetivoParaSugerir, sesionAnterior, sugerida } from './anterior'

const HOY = '2026-10-06'
let n = 0
const s = (fecha: string, pesoKg: number, reps: number, extra: Partial<Serie & { estado: string }> = {}) => ({
  id: String(n++),
  ejercicioId: 'banca',
  fecha,
  pesoKg,
  reps,
  ts: `${fecha}T10:${String(n).padStart(2, '0')}:00.000Z`,
  ...extra,
})
const obj = (o: Partial<EjercicioRutina> = {}): EjercicioRutina => ({ id: 'banca', series: 3, reps: 8, pesoKg: 60, descansoS: 90, ...o })

describe('sesionAnterior', () => {
  it('elige la fecha más reciente antes de hoy (ayer gana a hace 3 semanas)', () => {
    const series = [s('2026-09-15', 50, 10), s('2026-10-05', 60, 8), s('2026-10-05', 62.5, 6)]
    expect(sesionAnterior(series, 'banca', HOY).map((x) => x.pesoKg)).toEqual([60, 62.5])
  })
  it('hoy no cuenta y otros ejercicios tampoco', () => {
    const series = [s(HOY, 70, 5), s('2026-10-01', 60, 8), s('2026-10-04', 99, 1, { ejercicioId: 'remo' })]
    expect(sesionAnterior(series, 'banca', HOY).map((x) => x.fecha)).toEqual(['2026-10-01'])
  })
  it('excluye rechazadas (si un día solo tiene rechazadas, no es la sesión)', () => {
    const series = [s('2026-10-01', 60, 8), s('2026-10-05', 999, 8, { estado: 'rechazada' })]
    expect(sesionAnterior(series, 'banca', HOY).map((x) => x.fecha)).toEqual(['2026-10-01'])
  })
  it('agrupa por fecha aunque el ts caiga en otro día (UTC) y cruza meses', () => {
    const series = [s('2026-09-30', 60, 8, { ts: '2026-10-01T02:00:00.000Z' }), s('2026-09-29', 50, 8)]
    expect(sesionAnterior(series, 'banca', '2026-10-01').map((x) => x.fecha)).toEqual(['2026-09-30'])
  })
  it('ordena por ts', () => {
    const a = s('2026-10-05', 60, 8, { ts: '2026-10-05T10:10:00.000Z' })
    const b = s('2026-10-05', 50, 10, { ts: '2026-10-05T10:00:00.000Z' })
    expect(sesionAnterior([a, b], 'banca', HOY)).toEqual([b, a])
  })
})

describe('filasSugeridas', () => {
  it('sin historial: objetivo', () => {
    expect(filasSugeridas([], obj())).toEqual([
      { ref: null, pesoKg: 60, reps: 8 },
      { ref: null, pesoKg: 60, reps: 8 },
      { ref: null, pesoKg: 60, reps: 8 },
    ])
  })
  it('sin historial ni peso objetivo: peso null', () => {
    expect(filasSugeridas([], obj({ pesoKg: null, series: 1 }))).toEqual([{ ref: null, pesoKg: null, reps: 8 }])
  })
  it('una fila por serie anterior, con su referencia', () => {
    const ant = [s('2026-10-05', 60, 8), s('2026-10-05', 62.5, 6)]
    const f = filasSugeridas(ant, obj({ series: 4 }))
    expect(f).toHaveLength(2)
    expect(f[1]).toEqual({ ref: ant[1], pesoKg: 62.5, reps: 6 })
  })
  it('más series en la rutina que en la sesión anterior: la fila extra sugiere la última y ref null', () => {
    const ant = [s('2026-10-05', 60, 8), s('2026-10-05', 62.5, 6)]
    expect(sugerida(ant, obj({ series: 4 }), 3)).toEqual({ ref: null, pesoKg: 62.5, reps: 6 })
  })
  it('tope 10 filas', () => {
    const ant = Array.from({ length: 12 }, () => s('2026-10-05', 60, 8))
    expect(filasSugeridas(ant, null)).toHaveLength(10)
  })
  it('carga corporal sin historial ni peso objetivo sugiere 0 kg; con carga, igual que antes', () => {
    expect(filasSugeridas([], objetivoParaSugerir(obj({ pesoKg: null, series: 1 }), true))).toEqual([{ ref: null, pesoKg: 0, reps: 8 }])
    expect(objetivoParaSugerir(obj({ pesoKg: null }), false).pesoKg).toBeNull()
    expect(objetivoParaSugerir(obj({ pesoKg: 10 }), true).pesoKg).toBe(10)
  })
  it('carga corporal: peso 0 se sugiere como 0, no como vacío', () => {
    const ant = [s('2026-10-05', 0, 12, { ejercicioId: 'dominadas' })]
    expect(filasSugeridas(ant, null)[0]).toMatchObject({ pesoKg: 0, reps: 12 })
  })
})

describe('tipo de serie sugerido', () => {
  it('hereda solo el calentamiento de la serie del mismo número', () => {
    const ant = [
      s('2026-10-05', 40, 10, { tipoSerie: 'calentamiento' }),
      s('2026-10-05', 60, 8, { tipoSerie: 'fallo' }),
      s('2026-10-05', 50, 10, { tipoSerie: 'drop' }),
      s('2026-10-05', 60, 8),
    ]
    expect(filasSugeridas(ant, obj()).map((f) => f.tipoSerie)).toEqual(['calentamiento', undefined, undefined, undefined])
  })
  it('una fila extra (sin ref) no hereda el tipo de la última', () => {
    const ant = [s('2026-10-05', 40, 10, { tipoSerie: 'calentamiento' })]
    expect(sugerida(ant, obj(), 1).tipoSerie).toBeUndefined()
  })
  it('serie vieja sin campo: sin tipo', () => {
    expect(filasSugeridas([s('2026-10-05', 60, 8)], obj())[0].tipoSerie).toBeUndefined()
  })
})
