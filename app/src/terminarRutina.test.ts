import { describe, expect, it } from 'vitest'
import type { Rutina, Serie } from './api'
import type { Plan } from './plan'
import {
  agregarAlPlan, guardarAgregados, guardarOrdenInicial, leerAgregados, leerOrdenInicial, nuevosParaRutina, ordenCambiado, rutinaConCambios,
  rutinaConNuevos, rutinaNuevaDePlan,
} from './terminarRutina'

const HOY = '2026-10-06'
const plan = (ids: string[]): Plan => ({
  fecha: HOY,
  rutinaId: 'r1',
  nombre: 'Empuje',
  inicio: 0,
  ejercicios: ids.map((id) => ({ id, series: 3, reps: 10, pesoKg: null, descansoS: 60 })),
})
let n = 0
const serie = (ejercicioId: string, pesoKg: number, reps: number, ts: string, extra: Partial<Serie & { estado: string }> = {}) => ({
  id: String(n++),
  ejercicioId,
  pesoKg,
  reps,
  fecha: HOY,
  ts: `${HOY}T${ts}:00.000Z`,
  ...extra,
})

const memoria = () => {
  const m = new Map<string, string>()
  return { getItem: (k: string) => m.get(k) ?? null, setItem: (k: string, v: string) => void m.set(k, v) }
}

describe('agregados en almacenamiento', () => {
  it('caducan con la fecha y validan el contenido', () => {
    const a = memoria()
    guardarAgregados(a, 'u', { fecha: HOY, ids: ['a', 'a', 'b'] })
    expect(leerAgregados(a, 'u', HOY)).toEqual(['a', 'b'])
    expect(leerAgregados(a, 'u', '2026-10-07')).toEqual([])
    expect(leerAgregados(a, 'otro', HOY)).toEqual([])
    a.setItem('gc.planAgregados:u', JSON.stringify({ fecha: HOY, ids: [1, 'c'] }))
    expect(leerAgregados(a, 'u', HOY)).toEqual(['c'])
    a.setItem('gc.planAgregados:u', '{roto')
    expect(leerAgregados(a, 'u', HOY)).toEqual([])
  })
})

describe('agregarAlPlan', () => {
  it('añade al final con objetivo por defecto', () => {
    const p = agregarAlPlan(plan(['a']), 'b', 120)
    expect(p.ejercicios.map((e) => e.id)).toEqual(['a', 'b'])
    expect(p.ejercicios[1]).toEqual({ id: 'b', series: 3, reps: 10, pesoKg: null, descansoS: 120 })
  })
  it('no duplica ni pasa de 15', () => {
    const p = plan(['a'])
    expect(agregarAlPlan(p, 'a', 90)).toBe(p)
    const lleno = plan(Array.from({ length: 15 }, (_, i) => 'e' + i))
    expect(agregarAlPlan(lleno, 'x', 90)).toBe(lleno)
  })
})

describe('nuevosParaRutina', () => {
  it('sin agregados no hay nada', () => {
    expect(nuevosParaRutina([serie('a', 50, 10, '10:00')], plan(['a']), [])).toEqual([])
  })
  it('agregado sin series se descarta', () => {
    expect(nuevosParaRutina([serie('a', 50, 10, '10:00')], plan(['a', 'b']), ['b'])).toEqual([])
  })
  it('con series: cuenta, y reps/peso de la última de hoy', () => {
    const s = [
      serie('b', 40, 12, '10:05'),
      serie('b', 45.36, 8, '10:10'),
      serie('b', 42.5, 10, '10:08'),
      serie('b', 99, 1, '10:20', { estado: 'rechazada' }),
      serie('b', 70, 5, '09:00', { fecha: '2026-10-05' }),
    ]
    expect(nuevosParaRutina(s, plan(['a', 'b']), ['b'])).toEqual([
      { id: 'b', series: 3, reps: 8, pesoKg: 45.36, descansoS: 60 },
    ])
  })
  it('máximo 10 series', () => {
    const s = Array.from({ length: 12 }, (_, i) => serie('b', 10, 10, `10:${String(i).padStart(2, '0')}`))
    expect(nuevosParaRutina(s, plan(['b']), ['b'])[0].series).toBe(10)
  })
  it('reps dentro de 1–100 aunque el dato local venga fuera de rango', () => {
    expect(nuevosParaRutina([serie('b', 10, 250, '10:00')], plan(['b']), ['b'])[0].reps).toBe(100)
    expect(nuevosParaRutina([serie('b', 10, 0, '10:00')], plan(['b']), ['b'])[0].reps).toBe(1)
  })
  it('pesoKg fuera de 0–500 pasa a null', () => {
    expect(nuevosParaRutina([serie('b', 600, 10, '10:00')], plan(['b']), ['b'])[0].pesoKg).toBeNull()
    expect(nuevosParaRutina([serie('b', -1, 10, '10:00')], plan(['b']), ['b'])[0].pesoKg).toBeNull()
    expect(nuevosParaRutina([serie('b', 0, 10, '10:00')], plan(['b']), ['b'])[0].pesoKg).toBe(0)
  })
  it('ids repetidos o ya en la rutina cuentan una vez / se omiten', () => {
    const s = [serie('b', 10, 10, '10:00'), serie('c', 10, 10, '10:01')]
    expect(nuevosParaRutina(s, plan(['b', 'c']), ['b', 'b', 'c'], ['c']).map((e) => e.id)).toEqual(['b'])
  })
})

describe('rutinaConNuevos', () => {
  const rutina = (k: number): Rutina => ({
    id: 'r1',
    nombre: 'Empuje',
    actualizada: '',
    ejercicios: Array.from({ length: k }, (_, i) => ({ id: 'e' + i, series: 3, reps: 10, pesoKg: null, descansoS: 90 })),
  })
  const nuevo = (id: string) => ({ id, series: 2, reps: 8, pesoKg: 20, descansoS: 60 })

  it('agrega al final', () => {
    const r = rutinaConNuevos(rutina(2), [nuevo('x')])
    expect('ejercicios' in r && r.ejercicios.map((e) => e.id)).toEqual(['e0', 'e1', 'x'])
  })
  it('tope 15', () => {
    expect(rutinaConNuevos(rutina(15), [nuevo('x')])).toHaveProperty('error')
    expect(rutinaConNuevos(rutina(14), [nuevo('x')])).not.toHaveProperty('error')
  })
  it('no repite un id que ya estaba', () => {
    const r = rutinaConNuevos(rutina(2), [nuevo('e1')])
    expect('ejercicios' in r && r.ejercicios).toHaveLength(2)
  })
})

describe('orden de la rutina al terminar', () => {
  it('guarda y lee el orden inicial solo del mismo día', () => {
    const a = memoria()
    guardarOrdenInicial(a, 'u', { fecha: HOY, ids: ['a', 'b'] })
    expect(leerOrdenInicial(a, 'u', HOY)).toEqual(['a', 'b'])
    expect(leerOrdenInicial(a, 'u', '2026-10-07')).toBeNull()
    expect(leerOrdenInicial(a, 'otro', HOY)).toBeNull()
  })
  it('detecta cambios de orden e ignora los agregados', () => {
    expect(ordenCambiado(['a', 'b', 'c'], plan(['a', 'b', 'c']))).toBe(false)
    expect(ordenCambiado(['a', 'b', 'c'], plan(['b', 'a', 'c']))).toBe(true)
    expect(ordenCambiado(['a', 'b'], plan(['a', 'b', 'x']))).toBe(false)
    expect(ordenCambiado(['a', 'b'], plan(['x', 'a', 'b']))).toBe(false)
    expect(ordenCambiado(null, plan(['b', 'a']))).toBe(false)
  })
  const rutina: Rutina = { id: 'r1', nombre: 'Empuje', ejercicios: plan(['a', 'b', 'c']).ejercicios } as Rutina
  it('reordena según el plan y deja al final lo que el plan no tiene', () => {
    const r = rutinaConCambios(rutina, [], ['c', 'a'])
    expect('error' in r).toBe(false)
    if (!('error' in r)) expect(r.ejercicios.map((e) => e.id)).toEqual(['c', 'a', 'b'])
  })
  it('sin orden solo agrega los nuevos al final', () => {
    const nuevo = plan(['x']).ejercicios
    const r = rutinaConCambios(rutina, nuevo, null)
    if (!('error' in r)) expect(r.ejercicios.map((e) => e.id)).toEqual(['a', 'b', 'c', 'x'])
  })
  it('con orden y nuevos, el nuevo queda donde lo puso el plan', () => {
    const r = rutinaConCambios(rutina, plan(['x']).ejercicios, ['x', 'c', 'b', 'a'])
    if (!('error' in r)) expect(r.ejercicios.map((e) => e.id)).toEqual(['x', 'c', 'b', 'a'])
  })
})

describe('rutinaNuevaDePlan', () => {
  it('lleva el nombre propuesto y los ejercicios finales del plan (agregados y orden)', () => {
    const p = { ...plan(['c', 'a', 'x']), nombre: 'Empuje' }
    const r = rutinaNuevaDePlan(p, 'nuevo-id')
    expect(r).toEqual({ id: 'nuevo-id', nombre: 'Empuje', ejercicios: p.ejercicios })
    expect(r.ejercicios.map((e) => e.id)).toEqual(['c', 'a', 'x'])
  })
})

describe('tope de ejercicios', () => {
  it('el error es un dato, no texto traducido', () => {
    const r: Rutina = {
      id: 'r1',
      nombre: 'Empuje',
      actualizada: '',
      ejercicios: Array.from({ length: 15 }, (_, i) => ({ id: 'e' + i, series: 3, reps: 10, pesoKg: null, descansoS: 90 })),
    }
    const nuevo = { id: 'x', series: 2, reps: 8, pesoKg: 20, descansoS: 60 }
    expect(rutinaConNuevos(r, [nuevo])).toEqual({ error: 'demasiados', n: 16, max: 15 })
  })
})
