import { describe, expect, it } from 'vitest'
import type { EjercicioRutina, Serie } from './api'
import {
  claveDescanso, descansoDe, formatoReloj, guardarDescanso, leerDescanso, progresoPlan, restanteS, tiempoEntrenoS,
} from './contador'

const ej = (id: string, series: number, descansoS = 90): EjercicioRutina => ({ id, series, reps: 10, pesoKg: null, descansoS })
const s = (ejercicioId: string, estado?: string): Serie & { estado?: string } => ({
  id: Math.random().toString(), ejercicioId, pesoKg: 10, reps: 5, fecha: '2026-10-06', ts: '2026-10-06T10:00:00Z', estado,
})

describe('descanso configurado', () => {
  it('por defecto 0 (sin descanso); inválido -> 0; ida y vuelta', () => {
    const m = new Map<string, string>()
    const a = { getItem: (k: string) => m.get(k) ?? null, setItem: (k: string, v: string) => void m.set(k, v) }
    expect(leerDescanso(a)).toBe(0)
    m.set(claveDescanso, '75')
    expect(leerDescanso(a)).toBe(0)
    guardarDescanso(a, 120)
    expect(leerDescanso(a)).toBe(120)
    expect(leerDescanso(null)).toBe(0)
  })
})

describe('descansoDe', () => {
  it('usa el del plan (0 incluido) o el global fuera del plan', () => {
    const plan = [ej('a', 3, 60), ej('b', 3, 0)]
    expect(descansoDe('a', plan, 90)).toBe(60)
    expect(descansoDe('b', plan, 90)).toBe(0)
    expect(descansoDe('z', plan, 90)).toBe(90)
    expect(descansoDe('a', null, 90)).toBe(90)
  })
})

describe('restanteS', () => {
  it('redondea hacia arriba y nunca es negativo', () => {
    const d = { inicio: 1000, duracionS: 60 }
    expect(restanteS(d, 1000)).toBe(60)
    expect(restanteS(d, 1500)).toBe(60)
    expect(restanteS(d, 1001 + 1000)).toBe(59)
    expect(restanteS(d, 61_000)).toBe(0)
    expect(restanteS(d, 999_999)).toBe(0)
    expect(restanteS(null, 5)).toBe(0)
    // Inicio en el futuro: nunca más que la duración.
    expect(restanteS({ inicio: 10_000_000, duracionS: 60 }, 1000)).toBe(60)
  })
})

describe('reloj', () => {
  it('formatoReloj', () => {
    expect(formatoReloj(0)).toBe('0:00')
    expect(formatoReloj(59)).toBe('0:59')
    expect(formatoReloj(61)).toBe('1:01')
    expect(formatoReloj(3599)).toBe('59:59')
    expect(formatoReloj(3600)).toBe('1:00:00')
    expect(formatoReloj(3725)).toBe('1:02:05')
  })
  it('tiempoEntrenoS', () => {
    expect(tiempoEntrenoS(null, 5000)).toBe(0)
    expect(tiempoEntrenoS({ inicio: 1000 }, 66_500)).toBe(65)
    expect(tiempoEntrenoS({ inicio: 9000 }, 1000)).toBe(0)
  })
})

describe('progresoPlan', () => {
  it('plan vacío', () => {
    expect(progresoPlan([s('a')], [])).toEqual({ ejerciciosHechos: 0, ejerciciosTotal: 0, seriesHechas: 0, seriesObjetivo: 0 })
  })
  it('hecho al llegar al objetivo; no cuenta de más ni fuera del plan ni rechazadas', () => {
    const plan = [ej('a', 2), ej('b', 3)]
    const series = [s('a'), s('a'), s('a'), s('b'), s('b', 'rechazada'), s('z')]
    expect(progresoPlan(series, plan)).toEqual({ ejerciciosHechos: 1, ejerciciosTotal: 2, seriesHechas: 3, seriesObjetivo: 5 })
  })
})

describe('entradas no finitas', () => {
  it('no propagan NaN', () => {
    expect(restanteS({ inicio: NaN, duracionS: 90 }, 1000)).toBe(0)
    expect(formatoReloj(NaN)).toBe('0:00')
    expect(tiempoEntrenoS({ inicio: NaN }, 1000)).toBe(0)
  })
})

describe('progresoPlan con calentamiento', () => {
  it('el calentamiento no cuenta para las series objetivo', () => {
    const cal = { ...s('a'), tipoSerie: 'calentamiento' as const }
    expect(progresoPlan([cal, cal, s('a')], [ej('a', 2)])).toEqual({ ejerciciosHechos: 0, ejerciciosTotal: 1, seriesHechas: 1, seriesObjetivo: 2 })
    expect(progresoPlan([cal, s('a'), { ...s('a'), tipoSerie: 'drop' as const }], [ej('a', 2)]).ejerciciosHechos).toBe(1)
  })
})
