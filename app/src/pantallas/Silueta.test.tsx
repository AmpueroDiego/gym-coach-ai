import { renderToStaticMarkup } from 'react-dom/server'
import { afterEach, describe, expect, it } from 'vitest'
import { fijarIdioma } from '../i18n'
import Silueta from './Silueta'

const cuenta = (h: string) => (h.match(/<polygon/g) ?? []).length

describe('Silueta', () => {
  it('normal: dos vistas (66 polígonos), role img y leyenda en texto', () => {
    const h = renderToStaticMarkup(<Silueta principales={['chest']} secundarios={['triceps', 'shoulders']} />)
    expect(cuenta(h)).toBe(66)
    expect(h).toContain('role="img"')
    expect(h).toContain('Principales: Pecho; secundarios: Tríceps, Hombros')
    expect(h).toContain('#e53935')
    expect(h).toContain('#ef9a9a')
  })

  it('mini: una vista (33 polígonos) y aria-hidden', () => {
    const h = renderToStaticMarkup(<Silueta principales={['hamstrings']} secundarios={[]} mini />)
    expect(cuenta(h)).toBe(33)
    expect(h).toContain('aria-hidden="true"')
    expect(h).not.toContain('role="img"')
  })

  it('en inglés traduce leyenda y aria-label, y "none" sin secundarios', () => {
    fijarIdioma('en')
    const h = renderToStaticMarkup(<Silueta principales={['chest']} secundarios={[]} />)
    expect(h).toContain('Primary: Chest; secondary: none')
    expect(h).toContain('Secondary: none')
  })

  afterEach(() => fijarIdioma('es'))
})
