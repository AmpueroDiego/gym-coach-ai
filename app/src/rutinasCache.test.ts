import { describe, expect, it } from 'vitest'
import type { Rutina } from './api'
import { claveRutinasCache, guardarRutinasCache, leerRutinasCache } from './rutinasCache'

const almacenDe = (m = new Map<string, string>()) => ({ getItem: (k: string) => m.get(k) ?? null, setItem: (k: string, v: string) => void m.set(k, v) })
const rutina = (id: string): Rutina => ({
  id, nombre: 'Push', actualizada: '2026-10-06T10:00:00Z',
  ejercicios: [{ id: 'banca', series: 3, reps: 8, pesoKg: 60, descansoS: 90 }],
})

describe('caché de rutinas', () => {
  it('ida y vuelta por cuenta', () => {
    const a = almacenDe()
    guardarRutinasCache(a, 'A', [rutina('r1')])
    expect(leerRutinasCache(a, 'A')).toEqual([rutina('r1')])
    expect(leerRutinasCache(a, 'B')).toBeNull()
  })

  it('filtra inválidos', () => {
    const m = new Map([[claveRutinasCache('A'), JSON.stringify([
      rutina('ok'), null, { ...rutina('x'), nombre: 'n'.repeat(41) }, { ...rutina('y'), actualizada: 3 },
      { ...rutina('z'), ejercicios: [{ id: 'a' }] }, { ...rutina('w'), ejercicios: Array(16).fill(rutina('q').ejercicios[0]) },
    ])]])
    expect(leerRutinasCache(almacenDe(m), 'A')).toEqual([rutina('ok')])
  })

  it('JSON roto o sin almacén: null', () => {
    expect(leerRutinasCache(almacenDe(new Map([[claveRutinasCache('A'), '{no']])), 'A')).toBeNull()
    expect(leerRutinasCache(null, 'A')).toBeNull()
  })
})
