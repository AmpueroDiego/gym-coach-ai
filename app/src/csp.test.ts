import { describe, expect, it } from 'vitest'
import config from '../staticwebapp.config.json'

const csp = config.globalHeaders['Content-Security-Policy']
const directiva = (nombre: string) => csp.split(';').map((d) => d.trim()).find((d) => d.startsWith(`${nombre} `)) ?? ''

describe('CSP', () => {
  // Las fotos de la ficha vienen de GitHub raw: <img> (img-src) y el caché del service worker (connect-src).
  it('permite las fotos de la ficha en img-src y connect-src', () => {
    expect(directiva('img-src')).toContain('https://raw.githubusercontent.com')
    expect(directiva('connect-src')).toContain('https://raw.githubusercontent.com')
  })
  it('no abre el resto: default-src sigue en self y sin comodines', () => {
    expect(directiva('default-src')).toBe("default-src 'self'")
    expect(csp).not.toMatch(/\*|https:(?!\/\/)/)
  })
})
