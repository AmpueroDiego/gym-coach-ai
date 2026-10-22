import { renderToStaticMarkup } from 'react-dom/server'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { fijarIdioma } from '../i18n'
import type { RutinaPropuesta } from '../api'
import TarjetaRutina from './TarjetaRutina'

const rutina: RutinaPropuesta = {
  id: 'r1', nombre: 'Empuje', actualizada: '', minutosEstimados: 26,
  ejercicios: [
    { id: 'press-banca', series: 4, reps: 8, pesoKg: 62.5, descansoS: 90 },
    { id: 'fondos', series: 3, reps: 10, pesoKg: null, descansoS: 0 },
  ],
}
const NOMBRES: Record<string, string> = { 'press-banca': 'Press de banca', fondos: 'Fondos' }

describe('TarjetaRutina', () => {
  it('muestra nombres, series × reps, peso y minutos', () => {
    const h = renderToStaticMarkup(<TarjetaRutina rutina={rutina} nombre={(id) => NOMBRES[id]} sub="u1" alEmpezar={() => {}} />)
    expect(h).toContain('Empuje')
    expect(h).toContain('~26 min')
    expect(h).toContain('Press de banca')
    expect(h).toContain('4 × 8 · 62.5 kg')
    expect(h).toContain('Fondos')
    expect(h).toContain('3 × 10')
    expect(h).toMatch(/<button[^>]*>Empezar<\/button>/)
    expect(h).toMatch(/<button[^>]*>Guardar como rutina<\/button>/)
  })
})

it('un ejercicio repetido no choca de key', () => {
  const error = vi.spyOn(console, 'error').mockImplementation(() => {})
  const dup = { ...rutina, ejercicios: [rutina.ejercicios[0], rutina.ejercicios[0]] }
  const h = renderToStaticMarkup(<TarjetaRutina rutina={dup} nombre={(id) => NOMBRES[id]} sub="u1" alEmpezar={() => {}} />)
  expect(h.match(/Press de banca/g)).toHaveLength(2)
  expect(error).not.toHaveBeenCalled()
  error.mockRestore()
})

describe('aviso de peso', () => {
  afterEach(() => fijarIdioma('es'))
  const ver = (r: RutinaPropuesta, extra = {}) =>
    renderToStaticMarkup(<TarjetaRutina rutina={r} nombre={(id) => NOMBRES[id]} sub="u1" alEmpezar={() => {}} {...extra} />)
  it('sale con un pesoKg null, en es y en', () => {
    expect(ver(rutina)).toContain('Sin peso: elige uno con el que hagas todas las repeticiones')
    fijarIdioma('en')
    expect(ver(rutina)).toContain('No weight: pick one you can do all the reps with')
  })
  it('no sale si todos tienen peso', () => {
    const conPeso = { ...rutina, ejercicios: [rutina.ejercicios[0]] }
    expect(ver(conPeso)).not.toContain('aviso-peso')
  })
  it('guardadaFuera la muestra como guardada', () => {
    const h = ver(rutina, { guardadaFuera: true })
    expect(h).toMatch(/<button[^>]*disabled=""[^>]*>Guardada<\/button>/)
  })
})

describe('TarjetaRutina en inglés', () => {
  afterEach(() => fijarIdioma('es'))
  it('botones y aria en inglés', () => {
    fijarIdioma('en')
    const h = renderToStaticMarkup(<TarjetaRutina rutina={rutina} nombre={(id) => NOMBRES[id]} sub="u1" alEmpezar={() => {}} />)
    expect(h).toContain('aria-label="Suggested routine: Empuje"')
    expect(h).toMatch(/<button[^>]*>Start<\/button>/)
    expect(h).toMatch(/<button[^>]*>Save as routine<\/button>/)
  })
})
