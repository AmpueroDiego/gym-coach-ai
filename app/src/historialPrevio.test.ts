import { describe, expect, it } from 'vitest'
import { MAX_VENTANAS, hayMasVentanas, ventanaPrevia } from './historialPrevio'

describe('ventanaPrevia', () => {
  it('la primera termina el día anterior a la ventana actual', () => {
    // La actual con dias=30 cubre 2026-09-10..2026-10-09.
    expect(ventanaPrevia('2026-10-09', 30, 1)).toEqual({ desde: '2026-08-11', hasta: '2026-09-09' })
  })
  it('las siguientes son contiguas, sin huecos ni solapes', () => {
    const v1 = ventanaPrevia('2026-10-09', 30, 1)
    const v2 = ventanaPrevia('2026-10-09', 30, 2)
    expect(v2.hasta).toBe('2026-08-10')
    expect(v1.desde).toBe('2026-08-11')
  })
})

describe('hayMasVentanas', () => {
  it('para con una ventana vacía o al llegar al tope', () => {
    expect(hayMasVentanas(1, 40)).toBe(true)
    expect(hayMasVentanas(1, 0)).toBe(false)
    expect(hayMasVentanas(MAX_VENTANAS, 40)).toBe(false)
  })
})
