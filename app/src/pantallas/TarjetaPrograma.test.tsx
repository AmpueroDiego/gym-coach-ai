import { renderToStaticMarkup } from 'react-dom/server'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { fijarIdioma } from '../i18n'
import type { ProgramaPropuesto, RutinaPropuesta } from '../api'
import TarjetaPrograma, { guardarEnOrden } from './TarjetaPrograma'

const r = (id: string): RutinaPropuesta => ({
  id, nombre: 'Sesión ' + id, actualizada: '', minutosEstimados: 30,
  ejercicios: [{ id: 'press-banca', series: 3, reps: 10, pesoKg: null, descansoS: 60 }],
})
const programa: ProgramaPropuesto = { nombre: 'Full body 3 días', rutinas: [r('a'), r('b'), r('c')] }
const ver = () => renderToStaticMarkup(<TarjetaPrograma programa={programa} nombre={() => 'Press de banca'} sub="u1" alEmpezar={() => {}} />)

describe('TarjetaPrograma', () => {
  afterEach(() => fijarIdioma('es'))
  it('pinta nombre, sesiones, una tarjeta por rutina y Guardar todas', () => {
    const h = ver()
    expect(h).toContain('Full body 3 días')
    expect(h).toContain('3 sesiones')
    expect(h.match(/class="tarjeta-rutina"/g)).toHaveLength(3)
    expect(h).toMatch(/<button[^>]*>Guardar todas<\/button>/)
  })
  it('en inglés', () => {
    fijarIdioma('en')
    const h = ver()
    expect(h).toContain('3 sessions')
    expect(h).toContain('Suggested program: Full body 3 días')
    expect(h).toMatch(/<button[^>]*>Save all<\/button>/)
  })
})

describe('guardarEnOrden', () => {
  it('si falla la 2.ª devuelve el error y no llama la 3.ª', async () => {
    const boom = new Error('x')
    const guardar = vi.fn(async (x: RutinaPropuesta) => { if (x.id === 'b') throw boom })
    const alGuardar = vi.fn()
    const res = await guardarEnOrden(programa.rutinas, new Set(), guardar, alGuardar)
    expect(res).toEqual({ guardadas: 1, error: boom })
    expect(guardar.mock.calls.map((c) => c[0].id)).toEqual(['a', 'b'])
    expect(alGuardar.mock.calls).toEqual([['a']])
  })
  it('salta las ya guardadas', async () => {
    const guardar = vi.fn(async (_x: RutinaPropuesta) => {})
    const res = await guardarEnOrden(programa.rutinas, new Set(['a']), guardar, () => {})
    expect(res).toEqual({ guardadas: 3 })
    expect(guardar.mock.calls.map((c) => c[0].id)).toEqual(['b', 'c'])
  })
})
