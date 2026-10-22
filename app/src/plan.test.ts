import { describe, expect, it } from 'vitest'
import type { EjercicioRutina, Serie } from './api'
import { bloquesDelDia, clavePlan, guardarPlan, leerPlan, moverEnPlan, type Plan } from './plan'

const ej = (id: string, series = 3): EjercicioRutina => ({ id, series, reps: 10, pesoKg: null, descansoS: 90 })
const plan: Plan = { fecha: '2026-10-06', rutinaId: 'r', nombre: 'Push', ejercicios: [ej('a'), ej('b')], inicio: 1000 }

const almacenFalso = () => {
  const m = new Map<string, string>()
  return { m, a: { getItem: (k: string) => m.get(k) ?? null, setItem: (k: string, v: string) => void m.set(k, v) } }
}

const s = (id: string, ejercicioId: string, ts: string): Serie => ({
  id, ejercicioId, pesoKg: 10, reps: 5, fecha: '2026-10-06', ts,
})

describe('leerPlan / guardarPlan', () => {
  it('ida y vuelta, por cuenta', () => {
    const { a } = almacenFalso()
    guardarPlan(a, 'A', plan)
    expect(leerPlan(a, 'A', '2026-10-06')).toEqual(plan)
    expect(leerPlan(a, 'B', '2026-10-06')).toBeNull()
  })
  it('caduca otro día y se puede borrar', () => {
    const { a } = almacenFalso()
    guardarPlan(a, 'A', plan)
    expect(leerPlan(a, 'A', '2026-10-07')).toBeNull()
    guardarPlan(a, 'A', null)
    expect(leerPlan(a, 'A', '2026-10-06')).toBeNull()
  })
  it('JSON roto o forma mala -> null', () => {
    const { a, m } = almacenFalso()
    m.set(clavePlan('A'), '{roto')
    expect(leerPlan(a, 'A', '2026-10-06')).toBeNull()
    m.set(clavePlan('A'), JSON.stringify({ ...plan, ejercicios: ['a'] }))
    expect(leerPlan(a, 'A', '2026-10-06')).toBeNull()
    m.set(clavePlan('A'), JSON.stringify({ ...plan, ejercicios: [{ id: 'a' }] }))
    expect(leerPlan(a, 'A', '2026-10-06')).toBeNull()
    expect(leerPlan(null, 'A', '2026-10-06')).toBeNull()
  })
})

describe('bloquesDelDia', () => {
  it('plan primero en su orden (vacíos incluidos), luego el resto por primera serie', () => {
    const series = [s('1', 'z', '2026-10-06T10:00:00Z'), s('2', 'b', '2026-10-06T10:05:00Z'), s('3', 'y', '2026-10-06T10:02:00Z')]
    const b = bloquesDelDia(series, [ej('a'), ej('b')])
    expect(b.map((x) => [x.ejercicioId, x.series.length, x.enPlan])).toEqual([
      ['a', 0, true], ['b', 1, true], ['z', 1, false], ['y', 1, false],
    ])
    expect(b[0].objetivo?.id).toBe('a')
    expect(b[2].objetivo).toBeUndefined()
  })
  it('duplicados del plan = un bloque', () => {
    expect(bloquesDelDia([], [ej('a'), ej('a')])).toHaveLength(1)
  })
})

describe('leerPlan con localStorage hostil', () => {
  const con = (ejercicios: unknown) => {
    const { m, a } = almacenFalso()
    m.set(clavePlan('A'), JSON.stringify({ ...plan, ejercicios }))
    return leerPlan(a, 'A', '2026-10-06')
  }
  it('rechaza valores fuera de rango o no enteros', () => {
    expect(con([{ ...ej('a'), series: 0 }])).toBeNull()
    expect(con([{ ...ej('a'), series: 3.5 }])).toBeNull()
    expect(con([{ ...ej('a'), descansoS: -1 }])).toBeNull()
    expect(con([{ ...ej('a'), reps: 101 }])).toBeNull()
    expect(con([{ ...ej('a'), pesoKg: -5 }])).toBeNull()
  })
  it('el nombre admite 40 caracteres, no 41', () => {
    const { m, a } = almacenFalso()
    m.set(clavePlan('A'), JSON.stringify({ ...plan, nombre: 'x'.repeat(40) }))
    expect(leerPlan(a, 'A', '2026-10-06')).not.toBeNull()
    m.set(clavePlan('A'), JSON.stringify({ ...plan, nombre: 'x'.repeat(41) }))
    expect(leerPlan(a, 'A', '2026-10-06')).toBeNull()
  })
  it.each([
    ['inicio texto', { inicio: 'x' }],
    ['inicio nulo', { inicio: null }],
    ['rutinaId numérico', { rutinaId: 5 }],
    ['fecha nula', { fecha: null }],
    ['ejercicios objeto', { ejercicios: {} }],
  ])('un campo inválido (%s) descarta el plan sin lanzar', (_n, malo) => {
    const { m, a } = almacenFalso()
    m.set(clavePlan('A'), JSON.stringify({ ...plan, ...malo }))
    expect(leerPlan(a, 'A', '2026-10-06')).toBeNull()
  })
  it('rechaza más de 15 ejercicios y acepta 15', () => {
    expect(con(Array.from({ length: 16 }, (_, i) => ej('e' + i)))).toBeNull()
    expect(con(Array.from({ length: 15 }, (_, i) => ej('e' + i)))).not.toBeNull()
  })
})

describe('moverEnPlan', () => {
  const tres: Plan = { ...plan, ejercicios: [ej('a'), ej('b'), ej('c')] }
  it('sube y baja sin mutar el original', () => {
    expect(moverEnPlan(tres, 'b', -1).ejercicios.map((e) => e.id)).toEqual(['b', 'a', 'c'])
    expect(moverEnPlan(tres, 'b', 1).ejercicios.map((e) => e.id)).toEqual(['a', 'c', 'b'])
    expect(moverEnPlan(tres, 'b', 1)).toMatchObject({ rutinaId: 'r', nombre: 'Push', inicio: 1000 })
    expect(tres.ejercicios.map((e) => e.id)).toEqual(['a', 'b', 'c'])
  })
  it('en los bordes o con id inexistente devuelve el mismo plan', () => {
    expect(moverEnPlan(tres, 'a', -1)).toBe(tres)
    expect(moverEnPlan(tres, 'c', 1)).toBe(tres)
    expect(moverEnPlan(tres, 'z', 1)).toBe(tres)
  })
  it('el plan movido se persiste y se lee igual', () => {
    const { a } = almacenFalso()
    const movido = moverEnPlan(tres, 'c', -1)
    guardarPlan(a, 'A', movido)
    expect(leerPlan(a, 'A', '2026-10-06')).toEqual(movido)
  })
})

describe('plan propuesto', () => {
  it('conserva la marca de propuesta y descarta valores raros', () => {
    const base = { fecha: '2026-10-06', rutinaId: 'r', nombre: 'N', ejercicios: [], inicio: 1 }
    const a = (v: unknown) => ({ getItem: () => JSON.stringify(v), setItem: () => {} })
    expect(leerPlan(a({ ...base, propuesta: true }), 'A', '2026-10-06')?.propuesta).toBe(true)
    expect(leerPlan(a({ ...base, propuesta: 'si' }), 'A', '2026-10-06')?.propuesta).toBeUndefined()
  })
})
