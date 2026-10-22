import { describe, expect, it } from 'vitest'
import type { Serie } from './api'
import { sesionesDe, ultimaSesionConRutina } from './resumen'

let n = 0
const s = (fecha: string, hora: string, ejercicioId: string, pesoKg: number, reps: number, extra: Partial<Serie> & { estado?: string } = {}) => ({
  id: String(++n), ejercicioId, pesoKg, reps, fecha, ts: `${fecha}T${hora}:00Z`, ...extra,
})

describe('sesionesDe', () => {
  it('dos rutinas el mismo día = dos sesiones; sin rutina = una', () => {
    const r = sesionesDe([
      s('2026-10-06', '10:00', 'a', 10, 5, { rutina: 'Push' }),
      s('2026-10-06', '12:00', 'b', 10, 5, { rutina: 'Pull' }),
      s('2026-10-06', '13:00', 'c', 10, 5),
      s('2026-10-06', '14:00', 'd', 10, 5),
    ])
    expect(r.map((x) => [x.rutina, x.series])).toEqual([[null, 2], ['Pull', 1], ['Push', 1]])
  })
  it('orden descendente por fecha y usa s.fecha, no ts', () => {
    const r = sesionesDe([
      s('2026-10-04', '10:00', 'a', 1, 1),
      s('2026-10-05', '10:00', 'a', 1, 1, { ts: '2026-10-06T02:00:00Z' }),
      s('2026-10-06', '10:00', 'a', 1, 1),
    ])
    expect(r.map((x) => x.fecha)).toEqual(['2026-10-06', '2026-10-05', '2026-10-04'])
  })
  it('excluye rechazadas y calcula volumen y orden de ejercicios', () => {
    const [x] = sesionesDe([
      s('2026-10-06', '10:00', 'b', 60, 8),
      s('2026-10-06', '10:10', 'a', 62.5, 6),
      s('2026-10-06', '10:20', 'b', 1000, 1, { estado: 'rechazada' }),
    ])
    expect(x.volumenKg).toBe(855)
    expect(x.series).toBe(2)
    expect(x.ejercicios.map((e) => e.ejercicioId)).toEqual(['b', 'a'])
    expect(x.ejercicios[0]).toMatchObject({ volumenKg: 480, reps: 8 })
  })
  it('carga corporal: volumen 0 y reps expuestas', () => {
    const [x] = sesionesDe([s('2026-10-06', '10:00', 'd', 10, 8), s('2026-10-06', '10:05', 'd', 0, 7)], new Set(['d']))
    expect(x.volumenKg).toBe(0)
    expect(x.ejercicios[0].reps).toBe(15)
  })
})

describe('ultimaSesionConRutina', () => {
  it('la más reciente con rutina', () => {
    const r = sesionesDe([
      s('2026-10-06', '10:00', 'a', 1, 1),
      s('2026-10-05', '10:00', 'a', 1, 1, { rutina: 'Push' }),
      s('2026-10-04', '10:00', 'a', 1, 1, { rutina: 'Pull' }),
    ])
    expect(ultimaSesionConRutina(r)?.rutina).toBe('Push')
    expect(ultimaSesionConRutina([])).toBeNull()
  })
})

describe('redondeo de volumen', () => {
  it('no deja ruido de flotantes', () => {
    const r = sesionesDe([s('2026-10-06', '10:00', 'a', 45.36, 10)])
    expect(r[0].ejercicios[0].volumenKg).toBe(453.6)
    expect(r[0].volumenKg).toBe(453.6)
  })
})

describe('calentamiento en sesiones', () => {
  it('se lista pero no suma volumen, reps ni series', () => {
    const [ses] = sesionesDe([
      s('2026-10-06', '10:00', 'banca', 40, 10, { tipoSerie: 'calentamiento' }),
      s('2026-10-06', '10:05', 'banca', 60, 8),
      s('2026-10-06', '10:10', 'banca', 60, 6, { tipoSerie: 'fallo' }),
    ])
    expect(ses.ejercicios[0].series).toHaveLength(3)
    expect(ses.ejercicios[0]).toMatchObject({ volumenKg: 840, reps: 14 })
    expect(ses).toMatchObject({ volumenKg: 840, series: 2 })
  })
  it('una sesión de solo calentamiento queda con 0 series', () => {
    const [ses] = sesionesDe([s('2026-10-06', '10:00', 'banca', 40, 10, { tipoSerie: 'calentamiento' })])
    expect(ses).toMatchObject({ volumenKg: 0, series: 0 })
  })
})
