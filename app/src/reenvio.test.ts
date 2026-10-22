import { describe, expect, it } from 'vitest'
import { msHastaMedianoche, proximoReenvio } from './reenvio'

const s = (x: object) => ({ id: 'a', ejercicioId: 'e', pesoKg: 1, reps: 1, fecha: '2026-01-01', ts: 't', ...x })

describe('proximoReenvio', () => {
  it('nada que reprogramar', () => {
    expect(proximoReenvio(null, 1000)).toBeNull()
    expect(proximoReenvio('[]', 1000)).toBeNull()
    expect(proximoReenvio(JSON.stringify([s({ estado: 'rechazada' }), s({ estado: 'pendiente' })]), 1000)).toBeNull()
  })

  it('el menor Retry-After de los 429', () => {
    const l = [s({ estado: 'pendiente', reintentarDesde: 9000 }), s({ estado: 'pendiente', reintentarDesde: 5000 })]
    expect(proximoReenvio(JSON.stringify(l), 1000)).toBe(5000)
  })

  it('backoff fijo para las en error, y gana el más cercano', () => {
    expect(proximoReenvio(JSON.stringify([s({ estado: 'error' })]), 1000, 30_000)).toBe(31_000)
    const l = [s({ estado: 'error' }), s({ estado: 'pendiente', reintentarDesde: 2000 })]
    expect(proximoReenvio(JSON.stringify(l), 1000, 30_000)).toBe(2000)
  })

  it('un Retry-After vencido se reintenta ya', () => {
    expect(proximoReenvio(JSON.stringify([s({ estado: 'pendiente', reintentarDesde: 10 })]), 1000)).toBe(1000)
  })

  it('datos raros no revientan', () => {
    expect(proximoReenvio('{', 1000)).toBeNull()
    expect(proximoReenvio('{"a":1}', 1000)).toBeNull()
    expect(proximoReenvio('[null, 3, "x", {"estado":"pendiente","reintentarDesde":"9"}]', 1000)).toBeNull()
  })
})

describe('msHastaMedianoche', () => {
  it('cuenta hasta las 00:00 locales del día siguiente', () => {
    expect(msHastaMedianoche(new Date(2026, 9, 6, 23, 59, 0))).toBe(60_000)
    expect(msHastaMedianoche(new Date(2026, 9, 6, 0, 0, 0))).toBe(24 * 3600_000)
  })
})
