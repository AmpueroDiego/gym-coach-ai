import { describe, expect, it } from 'vitest'
import type { EjercicioRutina } from './api'
import { restanteS } from './contador'
import {
  ajustar, claveDescansoActivo, claveDescansos, debeAvisar, descansoPara, elegirPreset, finDe,
  GRACIA_AVISO_MS, guardarDescansoActivo, guardarDescansos, leerDescansoActivo, leerDescansos,
  recordarDescanso, reiniciar, type DescansoActivo,
} from './descanso'

const almacenFalso = () => {
  const m = new Map<string, string>()
  return {
    m,
    a: {
      getItem: (k: string) => m.get(k) ?? null,
      setItem: (k: string, v: string) => void m.set(k, v),
      removeItem: (k: string) => void m.delete(k),
    },
  }
}

const AHORA = 1_000_000_000
const activo = (inicio: number, duracionS: number): DescansoActivo => ({ ejercicioId: 'banca', inicio, duracionS })
const plan = (id: string, descansoS: number) => [{ id, descansoS }] as unknown as EjercicioRutina[]

describe('ajustar', () => {
  it('+15 alarga la duración y conserva el inicio', () => {
    const d = ajustar(activo(AHORA, 60), 15, AHORA + 10_000)
    expect(d).toEqual(activo(AHORA, 75))
  })

  it('-15 con 10 s restantes termina ahora', () => {
    const ahora = AHORA + 50_000
    const d = ajustar(activo(AHORA, 60), -15, ahora)
    expect(finDe(d)).toBe(ahora)
    expect(restanteS(d, ahora)).toBe(0)
  })

  it('-15 con tiempo de sobra solo quita 15 s', () => {
    const d = ajustar(activo(AHORA, 120), -15, AHORA + 10_000)
    expect(d.duracionS).toBe(105)
    expect(d.inicio).toBe(AHORA)
  })

  it('no pasa de 600 s', () => {
    expect(ajustar(activo(AHORA, 595), 15, AHORA).duracionS).toBe(600)
  })
})

describe('elegirPreset / reiniciar', () => {
  it('un preset conserva el inicio y es la duración total', () => {
    const d = elegirPreset(activo(AHORA, 60), 120, AHORA + 30_000)
    expect(d.inicio).toBe(AHORA)
    expect(d.duracionS).toBe(120)
    expect(restanteS(d, AHORA + 30_000)).toBe(90)
  })

  it('reiniciar vuelve a empezar desde ahora con la misma duración', () => {
    const d = reiniciar(activo(AHORA, 90), AHORA + 40_000)
    expect(d).toEqual(activo(AHORA + 40_000, 90))
  })
})

describe('debeAvisar', () => {
  const d = activo(AHORA, 60)
  const fin = finDe(d)

  it('antes del fin no avisa', () => expect(debeAvisar(d, fin - 1)).toBe(false))
  it('justo al terminar avisa', () => expect(debeAvisar(d, fin)).toBe(true))
  it('en el borde exacto de la gracia avisa', () => expect(debeAvisar(d, fin + GRACIA_AVISO_MS)).toBe(true))
  it('1 ms después de la gracia se descarta en silencio', () => expect(debeAvisar(d, fin + GRACIA_AVISO_MS + 1)).toBe(false))
})

describe('descanso activo guardado', () => {
  it('ida y vuelta, y null lo borra', () => {
    const { a, m } = almacenFalso()
    guardarDescansoActivo('u1', activo(AHORA, 60), a)
    expect(leerDescansoActivo('u1', a)).toEqual(activo(AHORA, 60))
    guardarDescansoActivo('u1', null, a)
    expect(m.has(claveDescansoActivo('u1'))).toBe(false)
    expect(leerDescansoActivo('u1', a)).toBeNull()
  })

  it.each([
    ['JSON malformado', '{no'],
    ['array', '[]'],
    ['sin ejercicioId', JSON.stringify({ inicio: AHORA, duracionS: 60 })],
    ['ejercicioId vacío', JSON.stringify({ ejercicioId: '', inicio: AHORA, duracionS: 60 })],
    ['inicio texto', JSON.stringify({ ejercicioId: 'a', inicio: 'x', duracionS: 60 })],
    ['duración negativa', JSON.stringify({ ejercicioId: 'a', inicio: AHORA, duracionS: -1 })],
    ['duración 601', JSON.stringify({ ejercicioId: 'a', inicio: AHORA, duracionS: 601 })],
    ['duración nula', JSON.stringify({ ejercicioId: 'a', inicio: AHORA, duracionS: null })],
  ])('descarta %s', (_n, crudo) => {
    const { a, m } = almacenFalso()
    m.set(claveDescansoActivo('u1'), crudo)
    expect(leerDescansoActivo('u1', a)).toBeNull()
  })

  it('descarta un inicio en el futuro (más de 60 s) y tolera un desfase pequeño', () => {
    const { a, m } = almacenFalso()
    m.set(claveDescansoActivo('u1'), JSON.stringify(activo(Date.now() + 3_600_000, 60)))
    expect(leerDescansoActivo('u1', a)).toBeNull()
    const cerca = activo(Date.now() + 30_000, 60)
    m.set(claveDescansoActivo('u1'), JSON.stringify(cerca))
    expect(leerDescansoActivo('u1', a)).toEqual(cerca)
  })

  it('sin almacenamiento o si lanza, no revienta', () => {
    expect(leerDescansoActivo('u1', null)).toBeNull()
    const roto = { getItem: () => { throw new Error('x') }, setItem: () => { throw new Error('x') }, removeItem: () => { throw new Error('x') } }
    expect(leerDescansoActivo('u1', roto)).toBeNull()
    expect(() => guardarDescansoActivo('u1', activo(AHORA, 60), roto)).not.toThrow()
    expect(leerDescansos('u1', roto)).toEqual({})
    expect(() => guardarDescansos('u1', { a: 60 }, roto)).not.toThrow()
  })

  it('uno vencido fuera de la gracia se descarta y uno dentro avisa una vez', () => {
    const vencido = activo(AHORA, 60)
    expect(debeAvisar(vencido, finDe(vencido) + GRACIA_AVISO_MS + 1)).toBe(false)
    const reciente = activo(AHORA, 60)
    expect(debeAvisar(reciente, finDe(reciente) + 60_000)).toBe(true)
  })

  it('las cuentas no se cruzan', () => {
    const { a } = almacenFalso()
    guardarDescansoActivo('u1', activo(AHORA, 60), a)
    guardarDescansos('u1', { banca: 90 }, a)
    expect(leerDescansoActivo('u2', a)).toBeNull()
    expect(leerDescansos('u2', a)).toEqual({})
    expect(leerDescansos('u1', a)).toEqual({ banca: 90 })
  })
})

describe('preferencias por ejercicio', () => {
  it.each([14, 601, 1.5, '60', null, NaN])('descarta %s', (v) => {
    const { a, m } = almacenFalso()
    m.set(claveDescansos('u1'), JSON.stringify({ malo: v, bueno: 60 }))
    expect(leerDescansos('u1', a)).toEqual({ bueno: 60 })
  })

  it('acepta los extremos 15 y 600', () => {
    const { a, m } = almacenFalso()
    m.set(claveDescansos('u1'), JSON.stringify({ min: 15, max: 600 }))
    expect(leerDescansos('u1', a)).toEqual({ min: 15, max: 600 })
  })

  it('JSON malformado o forma rara devuelve vacío', () => {
    const { a, m } = almacenFalso()
    for (const crudo of ['{no', '[1,2]', '"x"', 'null', '5']) {
      m.set(claveDescansos('u1'), crudo)
      expect(leerDescansos('u1', a)).toEqual({})
    }
  })

  it('tope de 100 entradas al leer, al guardar y al recordar', () => {
    const { a, m } = almacenFalso()
    const muchas = Object.fromEntries(Array.from({ length: 150 }, (_, i) => [`e${i}`, 60]))
    m.set(claveDescansos('u1'), JSON.stringify(muchas))
    expect(Object.keys(leerDescansos('u1', a))).toHaveLength(100)

    guardarDescansos('u2', muchas, a)
    expect(Object.keys(JSON.parse(m.get(claveDescansos('u2'))!))).toHaveLength(100)

    const lleno = leerDescansos('u1', a)
    const nuevo = recordarDescanso(lleno, 'nuevo', 90)
    expect(Object.keys(nuevo)).toHaveLength(100)
    expect(nuevo.nuevo).toBe(90)
    expect(nuevo.e50).toBeUndefined()  // la más vieja sale
  })

  it('recordar mueve el ejercicio al final e ignora valores inválidos', () => {
    const p = recordarDescanso({ a: 30, b: 60 }, 'a', 90)
    expect(Object.keys(p)).toEqual(['b', 'a'])
    expect(recordarDescanso(p, 'a', 14)).toBe(p)
  })
})

describe('descansoPara (precedencia)', () => {
  it('preferencia > rutina > global', () => {
    expect(descansoPara('banca', { banca: 45 }, plan('banca', 120), 60)).toBe(45)
    expect(descansoPara('banca', {}, plan('banca', 120), 60)).toBe(120)
    expect(descansoPara('banca', {}, null, 60)).toBe(60)
    expect(descansoPara('banca', {}, plan('otro', 120), 60)).toBe(60)
  })

  it('rutina 0 = sin descanso, salvo que haya preferencia', () => {
    expect(descansoPara('banca', {}, plan('banca', 0), 60)).toBe(0)
    expect(descansoPara('banca', { banca: 90 }, plan('banca', 0), 60)).toBe(90)
  })

  it('una preferencia inválida en memoria no gana', () => {
    expect(descansoPara('banca', { banca: 14 }, plan('banca', 120), 60)).toBe(120)
    expect(descansoPara('constructor', {}, null, 60)).toBe(60)
  })
})
