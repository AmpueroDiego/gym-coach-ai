import { describe, expect, it } from 'vitest'
import {
  claveUnidades, guardarUnidades, kgALb, lbAKg, leerUnidades, pesoEnTexto, resolverPesoKg, sumarPaso,
} from './unidades'

const almacenFalso = () => {
  const m = new Map<string, string>()
  return { m, a: { getItem: (k: string) => m.get(k) ?? null, setItem: (k: string, v: string) => void m.set(k, v) } }
}

describe('conversión', () => {
  it('lb <-> kg', () => {
    expect(lbAKg(45)).toBe(20.41)
    expect(lbAKg(100)).toBe(45.36)
    expect(kgALb(20.41)).toBe(45)
    expect(pesoEnTexto(20.41, 'lb')).toBe('45')
    expect(pesoEnTexto(45, 'kg')).toBe('45')
    expect(pesoEnTexto(62.5, 'kg')).toBe('62.5')
  })

  it('barrido 0–1102.3 lb: ida y vuelta sin deriva', () => {
    for (let i = 0; i <= 11023; i++) {
      const x = i / 10
      expect(pesoEnTexto(lbAKg(x), 'lb')).toBe(String(x))
    }
  })
})

describe('resolverPesoKg', () => {
  it('sin cambios devuelve la base sin reconvertir', () => {
    expect(resolverPesoKg('44.1', 'lb', 20)).toBe(20)
  })
  it('valida decimales y rango', () => {
    expect(resolverPesoKg('45.25', 'lb', null)).toBeNaN()
    expect(resolverPesoKg('1102.4', 'lb', null)).toBeNaN()
    expect(resolverPesoKg('1102.3', 'lb', null)).toBe(500)
    expect(resolverPesoKg('500.01', 'kg', null)).toBeNaN()
    expect(resolverPesoKg('62.555', 'kg', null)).toBeNaN()
    expect(resolverPesoKg('', 'kg', null)).toBeNaN()
    expect(resolverPesoKg('abc', 'kg', null)).toBeNaN()
    expect(resolverPesoKg('-5', 'kg', null)).toBeNaN()
  })
  it('acepta coma decimal', () => {
    expect(resolverPesoKg('62,5', 'kg', null)).toBe(62.5)
    expect(resolverPesoKg('45', 'lb', null)).toBe(20.41)
  })
})

describe('sumarPaso', () => {
  it('suma y limita', () => {
    expect(sumarPaso('45', 'lb', 1)).toBe('50')
    expect(sumarPaso('0', 'kg', -1)).toBe('0')
    expect(sumarPaso('60', 'kg', 1)).toBe('62.5')
    expect(sumarPaso('', 'kg', 1)).toBe('2.5')
    expect(sumarPaso('499', 'kg', 1)).toBe('500')
    expect(sumarPaso('1100', 'lb', 1)).toBe('1102.3')
  })
})

describe('preferencia por ejercicio', () => {
  it('separa por sub y solo guarda lb', () => {
    const { a } = almacenFalso()
    guardarUnidades(a, 'A', { press: 'lb', sentadilla: 'kg' })
    expect(leerUnidades(a, 'A')).toEqual({ press: 'lb' })
    expect(leerUnidades(a, 'B')).toEqual({})
  })
  it('ignora valores inválidos, JSON roto y null', () => {
    const { a, m } = almacenFalso()
    m.set(claveUnidades('A'), JSON.stringify({ x: 'oz', y: 'lb' }))
    expect(leerUnidades(a, 'A')).toEqual({ y: 'lb' })
    m.set(claveUnidades('A'), '{roto')
    expect(leerUnidades(a, 'A')).toEqual({})
    expect(leerUnidades(null, 'A')).toEqual({})
  })
})
