import { describe, expect, it } from 'vitest'
import { msHastaAsesoria } from './asesoria'

describe('msHastaAsesoria', () => {
  it('antes de las 7:30 devuelve lo que falta', () => {
    expect(msHastaAsesoria(new Date(2026, 9, 9, 7, 0), '2026-10-09')).toBe(30 * 60_000)
  })
  it('a las 7:30 o después, null', () => {
    expect(msHastaAsesoria(new Date(2026, 9, 9, 7, 30), '2026-10-09')).toBeNull()
    expect(msHastaAsesoria(new Date(2026, 9, 9, 9, 0), '2026-10-09')).toBeNull()
  })
})
