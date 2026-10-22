import { renderToStaticMarkup } from 'react-dom/server'
import { afterEach, describe, expect, it } from 'vitest'
import { fijarIdioma } from '../i18n'
import type { BorradoresEjercicio } from '../estadoUi'
import BloquePlan from './BloquePlan'

const html = (borradores: BorradoresEjercicio, ejercicioId = 'e1') =>
  renderToStaticMarkup(
    <BloquePlan
      ejercicioId={ejercicioId} nombre="Banca" unidad="kg" alCambiarUnidad={() => {}}
      objetivo={{ id: 'e1', series: 2, reps: 5, pesoKg: null, descansoS: 90 }}
      corporal={false} hechas={[]} anterior={[]} filas={[]} volumen={[]} etiquetaVolumen="kg"
      alGuardar={() => {}} alEditar={() => {}} alMover={() => {}} esPrimero esUltimo
      borradores={borradores} alBorrador={() => {}}
    />,
  )

describe('BloquePlan borradores', () => {
  it('restaura el borrador en la misma unidad', () => {
    const h = html({ 0: { peso: '80', reps: '5', tipo: null, unidad: 'kg' } })
    expect(h).toMatch(/aria-label="Serie 1: peso en kg"[^>]*value="80"/)
    expect(h).toMatch(/aria-label="Serie 1: repeticiones"[^>]*value="5"/)
  })
  it('en otra unidad descarta el peso pero conserva reps', () => {
    const h = html({ 0: { peso: '80', reps: '5', tipo: null, unidad: 'lb' } })
    expect(h).not.toContain('value="80"')
    expect(h).toMatch(/aria-label="Serie 1: repeticiones"[^>]*value="5"/)
  })
  it('una fila extra con borrador no desaparece', () => {
    expect(html({ 3: { peso: '70', reps: '', tipo: null, unidad: 'kg' } })).toContain('Serie 4: peso en kg')
  })
})

describe('BloquePlan ficha', () => {
  it('con ficha: la silueta mini va en la cabecera, tras el título, sin anidarse en otro button', () => {
    const h = html({}, 'prensa')
    expect(h).toMatch(/<h2>Banca<\/h2><button type="button" class="silueta-boton" aria-label="Ficha de Banca"><span class="silueta-mini"/)
    expect(h).toContain('aria-label="Guardar serie 1"')
    expect(h).not.toMatch(/<button[^>]*>(?:(?!<\/button>)[\s\S])*<button/)
  })
  it('sin ficha no hay botón de ficha', () => {
    expect(html({}, 'no-existe')).not.toContain('silueta-boton')
  })
})

describe('BloquePlan en inglés', () => {
  afterEach(() => fijarIdioma('es'))
  it('etiquetas de las filas y + Set en inglés', () => {
    fijarIdioma('en')
    const h = html({})
    expect(h).toContain('aria-label="Set 1: weight in kg"')
    expect(h).toContain('aria-label="Save set 1"')
    expect(h).toContain('+ Set')
    expect(h).not.toContain('+ Serie')
  })
})
