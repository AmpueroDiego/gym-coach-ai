import { renderToStaticMarkup } from 'react-dom/server'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { fijarIdioma } from '../i18n'
import { mostrarBotonGoogle } from '../auth'
import Reconectar from './Reconectar'

vi.mock('../auth', () => ({
  mostrarBotonGoogle: vi.fn().mockResolvedValue(undefined),
}))

// Sin jsdom en el proyecto: se prueba el marcado; el flujo de crear la sesión se prueba en auth.test.
describe('Reconectar', () => {
  it('es una banda de estado, no un diálogo', () => {
    const html = renderToStaticMarkup(<Reconectar alEntrar={() => {}} />)
    expect(html).not.toContain('role="dialog"')
    expect(html).toContain('role="status"')
    expect(html).toContain('Sin sesión: tus series se guardan aquí y se suben al reconectar.')
    expect(html).toMatch(/<button[^>]*>Reconectar<\/button>/)
    // El botón de Google solo se pinta al tocar Reconectar.
    expect(mostrarBotonGoogle).not.toHaveBeenCalled()
  })
})

describe('Reconectar en inglés', () => {
  afterEach(() => fijarIdioma('es'))
  it('banda y botón en inglés', () => {
    fijarIdioma('en')
    const html = renderToStaticMarkup(<Reconectar alEntrar={() => {}} />)
    expect(html).toContain('Signed out: your sets are saved here and upload when you reconnect.')
    expect(html).toMatch(/<button[^>]*>Reconnect<\/button>/)
  })
})
