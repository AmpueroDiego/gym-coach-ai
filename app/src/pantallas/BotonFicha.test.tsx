import { renderToStaticMarkup } from 'react-dom/server'
import { afterEach, describe, expect, it } from 'vitest'
import { fijarIdioma } from '../i18n'
import BotonFicha from './BotonFicha'

describe('BotonFicha', () => {
  it('muestra el botón accesible si hay ficha', () => {
    const html = renderToStaticMarkup(<BotonFicha ejercicioId="prensa" nombre="Prensa" />)
    expect(html).toContain('aria-label="Ficha de Prensa"')
    expect(html).toContain('type="button"')
    expect(html).toContain('class="info-ejercicio"')
  })

  it('conSilueta: botón con la silueta mini en vez del ⓘ', () => {
    const html = renderToStaticMarkup(<BotonFicha ejercicioId="prensa" nombre="Prensa" conSilueta />)
    expect(html).toContain('class="silueta-boton"')
    expect(html).toContain('silueta-mini')
    expect(html).not.toContain('ⓘ')
  })

  it('conSilueta sin ficha no renderiza nada', () => {
    expect(renderToStaticMarkup(<BotonFicha ejercicioId="no-existe" nombre="X" conSilueta />)).toBe('')
  })

  it('no renderiza nada sin ficha', () => {
    expect(renderToStaticMarkup(<BotonFicha ejercicioId="no-existe" nombre="X" />)).toBe('')
  })

  it('cerrado no incluye el diálogo', () => {
    expect(renderToStaticMarkup(<BotonFicha ejercicioId="prensa" nombre="Prensa" />)).not.toContain('role="dialog"')
  })

  it('en inglés el aria-label va en inglés', () => {
    fijarIdioma('en')
    expect(renderToStaticMarkup(<BotonFicha ejercicioId="prensa" nombre="Leg press" />)).toContain('aria-label="Details for Leg press"')
  })

  afterEach(() => fijarIdioma('es'))
})
