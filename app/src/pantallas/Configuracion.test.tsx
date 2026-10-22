import { renderToStaticMarkup } from 'react-dom/server'
import { afterEach, describe, expect, it } from 'vitest'
import { fijarIdioma } from '../i18n'
import Configuracion from './Configuracion'

afterEach(() => fijarIdioma('es'))

const vista = (idioma: 'es' | 'en') =>
  renderToStaticMarkup(
    <Configuracion idioma={idioma} alCambiarIdioma={() => {}} nombres="app" alCambiarNombres={() => {}} descansoS={0} alCambiarDescanso={() => {}} />,
  )

describe('Configuracion', () => {
  it('en español', () => {
    const html = vista('es')
    expect(html).toContain('Idioma de la app')
    expect(html).toContain('En ambos idiomas')
    expect(html).toContain('Sin descanso')
  })
  it('en inglés no queda español', () => {
    fijarIdioma('en')
    const html = vista('en')
    expect(html).toContain('App language')
    expect(html).toContain('Exercise names')
    expect(html).toContain('No rest')
    expect(html).not.toContain('Idioma')
    expect(html).not.toContain('Descanso')
  })
  it('marca el idioma activo y deja Español/English en su propio idioma', () => {
    fijarIdioma('en')
    expect(vista('en')).toMatch(/checked=""[^>]*\/>English/)
    expect(vista('en')).toContain('Español')
  })
})
