import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import type { Ejercicio } from '../api'
import SelectorEjercicio from './SelectorEjercicio'

const ej = (id: string, nombre: string): Ejercicio => ({ id, nombre, nombreEn: nombre, grupo: 'piernas', equipo: 'maquina' })

const html = renderToStaticMarkup(
  <SelectorEjercicio
    ejercicios={[ej('prensa', 'Prensa'), ej('sin-ficha-xyz', 'Otro')]}
    nombres="app"
    recientes={['prensa']}
    alElegir={() => {}}
    alCerrar={() => {}}
  />,
)

describe('SelectorEjercicio con ficha', () => {
  it('ofrece el ⓘ solo al ejercicio con ficha', () => {
    expect(html).toContain('aria-label="Ficha de Prensa"')
    expect(html).not.toContain('Ficha de Otro')
  })
  it('el ⓘ es hermano del botón de elegir: ningún button dentro de otro', () => {
    expect(html).not.toMatch(/<button[^>]*>(?:(?!<\/button>)[\s\S])*<button/)
  })
  it('el ⓘ queda dentro del mismo li que el botón de elegir', () => {
    expect(html).toMatch(/<li><button>Prensa<\/button><button type="button" class="info-ejercicio"/)
  })
})
