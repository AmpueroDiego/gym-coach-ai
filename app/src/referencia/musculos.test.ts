import { describe, expect, it } from 'vitest'
import { MuscleType } from 'react-body-highlighter'
import datos from './datos.json'
import { A_RBH, datosSilueta, vistaPrincipal } from './musculos'
import { NOMBRE_MUSCULO, type MusculoRef } from './referencia'

const VALIDOS = new Set<string>(Object.values(MuscleType))
const TODOS = Object.keys(NOMBRE_MUSCULO) as MusculoRef[]

describe('A_RBH', () => {
  it('todas las claves de MusculoRef tienen nombres válidos de la lib', () => {
    expect(Object.keys(A_RBH).sort()).toEqual([...TODOS].sort())
    for (const m of TODOS) {
      expect(A_RBH[m].length).toBeGreaterThan(0)
      for (const n of A_RBH[m]) expect(VALIDOS.has(n)).toBe(true)
    }
  })

  it('todo músculo de datos.json tiene entrada en A_RBH', () => {
    const fichas = Object.values(datos.ejercicios) as { principales: string[]; secundarios: string[] }[]
    for (const f of fichas) {
      for (const m of [...f.principales, ...f.secundarios]) {
        expect(Object.hasOwn(A_RBH, m), m).toBe(true)
      }
    }
  })

  it('aductores usa los polígonos de muslo interno (la lib los nombra al revés)', () => {
    expect(A_RBH.adductors).toEqual(['abductors', 'adductor'])
  })
})

describe('datosSilueta', () => {
  it('lats + middle back secundarios: upper-back una sola vez con frecuencia 1', () => {
    expect(datosSilueta([], ['lats', 'middle back'])).toEqual([{ name: 'secundarios', muscles: ['upper-back'], frequency: 1 }])
  })

  it('lats + middle back principales: upper-back una vez, frecuencia 2', () => {
    expect(datosSilueta(['lats', 'middle back'], [])).toEqual([{ name: 'principales', muscles: ['upper-back'], frequency: 2 }])
  })

  it('un músculo en ambas listas queda solo en principales', () => {
    const d = datosSilueta(['chest'], ['chest', 'triceps'])
    expect(d[0].muscles).toEqual(['chest'])
    expect(d[1].muscles).toEqual(['triceps'])
  })

  it('omite grupos vacíos', () => {
    expect(datosSilueta([], [])).toEqual([])
  })
})

describe('vistaPrincipal', () => {
  it('hamstrings posterior, chest anterior, vacío anterior', () => {
    expect(vistaPrincipal(['hamstrings'])).toBe('posterior')
    expect(vistaPrincipal(['chest'])).toBe('anterior')
    expect(vistaPrincipal([])).toBe('anterior')
  })

  it('empate cae en anterior', () => {
    expect(vistaPrincipal(['adductors'])).toBe('anterior')
  })
})
