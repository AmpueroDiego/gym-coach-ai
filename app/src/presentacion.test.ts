import { afterEach, describe, expect, it } from 'vitest'
import { fijarIdioma } from './i18n'
import {
  armarRutina,
  etiquetaDescanso,
  estadoEjercicio,
  etiquetaEjercicio,
  etiquetaFecha,
  resolverFila,
  filaDe,
  filaNueva,
  inicialDia,
  letraTipo,
  mensajeFallo,
  mover,
  puntosGrafico,
  textoObjetivo,
  textoVolumenEjercicio,
  textoVolumenSesion,
  BASE_GRAFICO,
} from './presentacion'
import { lbAKg } from './unidades'
import { Rechazada } from './api'

const kg = () => 'kg' as const

afterEach(() => fijarIdioma('es'))

describe('fechas', () => {
  it('etiqueta en español sin depender del ICU', () => {
    expect(etiquetaFecha('2026-10-06')).toBe('mar 6 oct')
    expect(etiquetaFecha('2026-01-04')).toBe('dom 4 ene')
  })
  it('en inglés: Tue Oct 6 e iniciales S M T W T F S', () => {
    fijarIdioma('en')
    expect(etiquetaFecha('2026-10-06')).toBe('Tue Oct 6')
    expect(etiquetaFecha('2026-01-04')).toBe('Sun Jan 4')
    expect(inicialDia('2026-10-07')).toBe('W')
    expect(inicialDia('2026-10-04')).toBe('S')
    expect(etiquetaDescanso(0)).toBe('No rest')
    expect(textoVolumenEjercicio({ ejercicioId: 'a', series: [], volumenKg: 1000, reps: 10 }, 'kg', false)).toBe('1,000 kg')
    expect(mensajeFallo(new Error('x'))).toContain('connect')
    expect(etiquetaEjercicio('x1', 'x1', false)).toBe('x1 (no longer exists)')
  })
  it('inicial del día (X = miércoles)', () => {
    expect(inicialDia('2026-10-07')).toBe('X')
    expect(inicialDia('2026-10-05')).toBe('L')
  })
})

describe('etiquetaDescanso', () => {
  it('cubre segundos, minutos y cero', () => {
    expect(etiquetaDescanso(0)).toBe('Sin descanso')
    expect(etiquetaDescanso(45)).toBe('45 s')
    expect(etiquetaDescanso(90)).toBe('90 s')
    expect(etiquetaDescanso(120)).toBe('2 min')
    expect(etiquetaDescanso(300)).toBe('5 min')
  })
})

describe('textoObjetivo', () => {
  it('con y sin peso, en la unidad del ejercicio', () => {
    expect(textoObjetivo({ series: 4, reps: 8, pesoKg: 80 }, 'kg')).toBe('4 × 8 · 80 kg')
    expect(textoObjetivo({ series: 4, reps: 8, pesoKg: null }, 'kg')).toBe('4 × 8')
    expect(textoObjetivo({ series: 3, reps: 10, pesoKg: lbAKg(135) }, 'lb')).toBe('3 × 10 · 135 lb')
  })
})

describe('volumen en texto', () => {
  const e = (id: string, volumenKg: number, reps: number) => ({ ejercicioId: id, series: [], volumenKg, reps })
  it('por ejercicio: kg, lb y corporal en reps', () => {
    expect(textoVolumenEjercicio(e('a', 1000, 10), 'kg', false)).toBe('1000 kg'.replace('1000', (1000).toLocaleString('es')))
    expect(textoVolumenEjercicio(e('a', 453.59237, 10), 'lb', false)).toBe('1000 lb'.replace('1000', (1000).toLocaleString('es')))
    expect(textoVolumenEjercicio(e('a', 0, 30), 'kg', true)).toBe('30 reps')
  })
  it('sesión mixta va en kg; corporales aparte', () => {
    const u = (id: string) => (id === 'b' ? 'lb' : 'kg')
    expect(textoVolumenSesion([e('a', 100, 5), e('b', 100, 5), e('c', 0, 20)], u, new Set(['c']))).toBe('200 kg · 20 reps')
  })
  it('todo en lb va en lb', () => {
    expect(textoVolumenSesion([e('b', 453.59237, 5)], () => 'lb', new Set())).toBe(`${(1000).toLocaleString('es')} lb`)
  })
  it('flotantes redondeados', () => {
    expect(textoVolumenSesion([e('a', 45.36 * 10, 10)], kg, new Set())).toBe('454 kg')
  })
})

describe('puntosGrafico', () => {
  it('día sin series en la base y el máximo arriba', () => {
    const g = puntosGrafico([
      { fecha: '2026-10-01', valor: 0 },
      { fecha: '2026-10-02', valor: 100 },
      { fecha: '2026-10-03', valor: 50 },
      { fecha: '2026-10-04', valor: 0 },
      { fecha: '2026-10-05', valor: 0 },
      { fecha: '2026-10-06', valor: 0 },
    ])
    expect(g.max).toBe(100)
    expect(g.puntos).toHaveLength(6)
    expect(g.puntos[0].y).toBe(BASE_GRAFICO)
    expect(g.puntos[1].y).toBeLessThan(g.puntos[2].y)
    expect(g.puntos[0].x).toBeLessThan(g.puntos[5].x)
    expect(g.iniciales).toEqual(['J', 'V', 'S', 'D', 'L', 'M'])
  })
  it('todo en cero queda plano sin NaN', () => {
    const g = puntosGrafico([{ fecha: '2026-10-06', valor: 0 }, { fecha: '2026-10-07', valor: 0 }])
    expect(g.puntos.every((p) => p.y === BASE_GRAFICO && Number.isFinite(p.x))).toBe(true)
  })
})

describe('armarRutina', () => {
  it('arma con valores por defecto y peso vacío = null', () => {
    const r = armarRutina('  Pierna ', [filaNueva('sentadilla')], kg)
    expect(r).toEqual({
      nombre: 'Pierna',
      ejercicios: [{ id: 'sentadilla', series: 3, reps: 10, pesoKg: null, descansoS: 0 }],
    })
  })
  it('respeta topes del servidor', () => {
    expect(armarRutina('', [filaNueva('a')], kg)).toHaveProperty('error')
    expect(armarRutina('x'.repeat(41), [filaNueva('a')], kg)).toHaveProperty('error')
    expect(armarRutina('A', [], kg)).toHaveProperty('error')
    expect(armarRutina('A', Array.from({ length: 16 }, (_, i) => filaNueva('e' + i)), kg)).toHaveProperty('error')
    expect(armarRutina('A', [filaNueva('a'), filaNueva('a')], kg)).toHaveProperty('error')
    expect(armarRutina('A', [{ ...filaNueva('a'), reps: '0' }], kg)).toHaveProperty('error')
    expect(armarRutina('A', [{ ...filaNueva('a'), reps: '7.5' }], kg)).toHaveProperty('error')
    expect(armarRutina('A', [{ ...filaNueva('a'), series: 11 }], kg)).toHaveProperty('error')
    expect(armarRutina('A', [{ ...filaNueva('a'), pesoTexto: '501' }], kg)).toHaveProperty('error')
  })
  it('peso en lb sin deriva si no se cambió el texto', () => {
    const base = 61.23
    const fila = filaDe({ id: 'a', series: 4, reps: 8, pesoKg: base, descansoS: 120 }, 'lb')
    const r = armarRutina('A', [fila], () => 'lb')
    expect('ejercicios' in r && r.ejercicios[0].pesoKg).toBe(base)
  })
})

describe('estadoEjercicio', () => {
  it('nada, en curso y hecho; las rechazadas no cuentan', () => {
    expect(estadoEjercicio([], 3)).toBe('nada')
    expect(estadoEjercicio([{ estado: 'rechazada' }], 1)).toBe('nada')
    expect(estadoEjercicio([{ estado: 'ok' }, { estado: 'pendiente' }], 3)).toBe('en curso')
    expect(estadoEjercicio([{}, {}, {}, {}], 3)).toBe('hecho')
  })
})

describe('mensajeFallo', () => {
  it('muestra el motivo del 400 y textos para el resto', () => {
    expect(mensajeFallo(new Rechazada('ejercicios: Debe tener entre 1 y 15 ejercicios.'))).toContain('entre 1 y 15')
    expect(mensajeFallo(new Error('x'))).toBe('No se pudo conectar. Revisa tu conexión.')
  })
})

describe('resolverFila', () => {
  const sug = { pesoKg: 45.36, reps: 8 }
  it('vacío = sugerido', () => {
    const r = resolverFila('', '', 'kg', sug)
    expect(r).toMatchObject({ pesoKg: 45.36, reps: 8, pesoOk: true, repsOk: true, pesoMal: false, repsMal: false })
  })
  it('anti-deriva: el sugerido en lb escrito tal cual conserva los kg exactos', () => {
    expect(resolverFila('100', '', 'lb', sug).pesoKg).toBe(45.36)
  })
  it('sin sugerencia y vacío: no válido pero sin rojo', () => {
    const r = resolverFila('', '', 'kg', { pesoKg: null, reps: null })
    expect(r).toMatchObject({ pesoOk: false, repsOk: false, pesoMal: false, repsMal: false })
  })
  it('peso 0 sugerido (carga corporal) es válido', () => {
    expect(resolverFila('', '', 'kg', { pesoKg: 0, reps: 10 }).pesoOk).toBe(true)
  })
  it('texto inválido se marca', () => {
    const r = resolverFila('abc', 'x', 'kg', sug)
    expect(r).toMatchObject({ pesoOk: false, repsOk: false, pesoMal: true, repsMal: true })
  })
  it('reps 0, 101 y decimales no valen; 1 y 100 sí', () => {
    for (const t of ['0', '101', '8.5', '1e1', '-3']) expect(resolverFila('', t, 'kg', sug).repsOk).toBe(false)
    for (const t of ['1', '100']) expect(resolverFila('', t, 'kg', sug).repsOk).toBe(true)
  })
})

describe('etiquetaEjercicio', () => {
  it('avisa si ya no existe', () => {
    expect(etiquetaEjercicio('x1', 'x1', false)).toBe('x1 (ya no existe)')
    expect(etiquetaEjercicio('x1', 'Banca', true)).toBe('Banca')
  })
})

describe('mover', () => {
  it('sube, baja y no sale de rango', () => {
    expect(mover([1, 2, 3], 1, -1)).toEqual([2, 1, 3])
    expect(mover([1, 2, 3], 1, 1)).toEqual([1, 3, 2])
    expect(mover([1, 2, 3], 0, -1)).toEqual([1, 2, 3])
    expect(mover([1, 2, 3], 2, 1)).toEqual([1, 2, 3])
  })
})

describe('tipos de serie en pantalla', () => {
  it('estadoEjercicio no cuenta el calentamiento', () => {
    expect(estadoEjercicio([{ tipoSerie: 'calentamiento' }], 1)).toBe('nada')
    expect(estadoEjercicio([{ tipoSerie: 'calentamiento' }, {}], 2)).toBe('en curso')
    expect(estadoEjercicio([{ tipoSerie: 'fallo' }, { tipoSerie: 'drop' }], 2)).toBe('hecho')
  })
  it('letraTipo: normal sin marca', () => {
    expect(letraTipo('calentamiento')).toBe('C')
    expect(letraTipo('fallo')).toBe('F')
    expect(letraTipo('drop')).toBe('D')
    expect(letraTipo('normal')).toBeNull()
    expect(letraTipo(undefined)).toBeNull()
  })
})
