import { describe, expect, it } from 'vitest'
import { borradorInicial, claveUi, conBorrador, filasPorBorradores, guardarUi, leerUi, uiVacia, type BorradorFila, type EstadoUi } from './estadoUi'

const HOY = '2026-10-09'
const b = (peso = '80', reps = '5'): BorradorFila => ({ peso, reps, tipo: null, unidad: 'kg' })

const almacen = (texto?: string) => {
  const m = new Map<string, string>()
  if (texto !== undefined) m.set(claveUi('u1'), texto)
  return { getItem: (k: string) => m.get(k) ?? null, setItem: (k: string, v: string) => void m.set(k, v) }
}

describe('leerUi / guardarUi', () => {
  it('la pestaña coach persiste', () => {
    const a = almacen()
    guardarUi(a, 'u1', { ...uiVacia(HOY), pestana: 'coach' })
    expect(leerUi(a, 'u1', HOY).pestana).toBe('coach')
  })
  it('hace ida y vuelta', () => {
    const a = almacen()
    const ui: EstadoUi = { fecha: HOY, pestana: 'progreso', hoja: 'e1', ultimoEjercicio: 'e2', filas: { e1: { 0: b() } } }
    guardarUi(a, 'u1', ui)
    expect(leerUi(a, 'u1', HOY)).toEqual(ui)
  })
  it('otra fecha, otra cuenta o JSON roto dan la vacía', () => {
    const a = almacen()
    guardarUi(a, 'u1', { ...uiVacia('2026-10-08'), pestana: 'mas' })
    expect(leerUi(a, 'u1', HOY)).toEqual(uiVacia(HOY))
    guardarUi(a, 'u1', { ...uiVacia(HOY), pestana: 'mas' })
    expect(leerUi(a, 'u2', HOY)).toEqual(uiVacia(HOY))
    expect(leerUi(almacen('{roto'), 'u1', HOY)).toEqual(uiVacia(HOY))
    expect(leerUi(null, 'u1', HOY)).toEqual(uiVacia(HOY))
  })
  it('descarta lo inválido sin tirar todo', () => {
    const t = JSON.stringify({
      fecha: HOY,
      pestana: 'otra',
      hoja: 'e1',
      ultimoEjercicio: 7,
      filas: { e1: { 0: b(), 50: b(), '-1': b(), 2: { ...b(), unidad: 'oz' }, 3: { ...b(), tipo: 'raro' }, 4: { ...b(), peso: '12345678901' } } },
    })
    const ui = leerUi(almacen(t), 'u1', HOY)
    expect(ui.pestana).toBe('entreno')
    expect(ui.hoja).toBe('e1')
    expect(ui.ultimoEjercicio).toBeNull()
    expect(Object.keys(ui.filas.e1)).toEqual(['0'])
  })
  it("ids como '__proto__' y 'toString' no rompen", () => {
    const t = '{"fecha":"' + HOY + '","filas":{"__proto__":{"0":{"peso":"1","reps":"1","tipo":null,"unidad":"kg"}},"toString":{"1":{"peso":"2","reps":"2","tipo":"drop","unidad":"lb"}}}}'
    const ui = leerUi(almacen(t), 'u1', HOY)
    expect(Object.keys(ui.filas).sort()).toEqual(['__proto__', 'toString'])
    expect(Object.getOwnPropertyDescriptor(ui.filas, 'toString')?.value[1].peso).toBe('2')
    expect(({} as Record<string, unknown>).peso).toBeUndefined()
  })
})

describe('conBorrador', () => {
  it('agrega sin mutar la entrada', () => {
    const filas = { e1: { 0: b() } }
    const sig = conBorrador(filas, 'e1', 1, b('90'))
    expect(sig.e1[1].peso).toBe('90')
    expect(sig.e1[0].peso).toBe('80')
    expect(Object.keys(filas.e1)).toEqual(['0'])
  })
  it('null o vacío quita la entrada y el ejercicio sin filas', () => {
    const filas = { e1: { 0: b() }, e2: { 0: b(), 1: b() } }
    const vacio: BorradorFila = { peso: '', reps: '', tipo: null, unidad: 'kg' }
    expect(conBorrador(filas, 'e1', 0, null)).not.toHaveProperty('e1')
    expect(conBorrador(filas, 'e1', 0, vacio)).not.toHaveProperty('e1')
    expect(Object.keys(conBorrador(filas, 'e2', 0, null).e2)).toEqual(['1'])
    expect(filas.e1[0]).toBeDefined()
  })
  it('un tipo elegido sin texto no cuenta como vacío', () => {
    expect(conBorrador({}, 'e1', 0, { peso: '', reps: '', tipo: 'drop', unidad: 'kg' }).e1[0].tipo).toBe('drop')
  })
  it("tipo 'normal' sin texto cuenta como vacío", () => {
    const sig = conBorrador({ e1: { 0: b() } }, 'e1', 0, { peso: '', reps: '', tipo: 'normal', unidad: 'kg' })
    expect(sig).not.toHaveProperty('e1')
  })
  it("acepta '__proto__' como id", () => {
    const sig = conBorrador({}, '__proto__', 0, b())
    expect(Object.keys(sig)).toEqual(['__proto__'])
  })
})

describe('límites de leerUi', () => {
  const leer = (filas: unknown, pestana = 'entreno') => leerUi(almacen(JSON.stringify({ fecha: HOY, pestana, filas })), 'u1', HOY)
  it('descarta peso de 11 chars, índice 30, "01" y unidad stone', () => {
    const ui = leer({ e1: { 30: b(), '01': b(), 1: { ...b(), peso: '12345678901' }, 2: { ...b(), unidad: 'stone' }, 3: b() } })
    expect(Object.keys(ui.filas.e1)).toEqual(['3'])
  })
  it('pestaña inválida cae a entreno', () => {
    expect(leer({}, 'x').pestana).toBe('entreno')
  })
  it('filtra antes de limitar ejercicios', () => {
    const filas: Record<string, unknown> = {}
    for (let i = 0; i < 25; i++) filas['malo' + i] = { 99: b() }
    filas.bueno = { 0: b() }
    expect(Object.keys(leer(filas).filas)).toEqual(['bueno'])
  })
})

describe('helpers de BloquePlan', () => {
  it('huérfanos bajo hechas no inflan el total', () => {
    expect(filasPorBorradores({ 0: b(), 1: b() }, 2)).toBe(0)
    expect(filasPorBorradores({ 0: b(), 4: b() }, 2)).toBe(5)
  })
  it('otra unidad conserva reps y tipo, no el peso', () => {
    expect(borradorInicial({ peso: '80', reps: '5', tipo: 'drop', unidad: 'lb' }, 'kg')).toEqual({ peso: '', reps: '5', tipo: 'drop' })
    expect(borradorInicial(undefined, 'kg')).toEqual({ peso: '', reps: '', tipo: null })
  })
})
