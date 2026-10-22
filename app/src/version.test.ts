import { afterEach, describe, expect, it } from 'vitest'
import { fijarIdioma } from './i18n'
import { textoVersion, textoVersionApp } from './version'

afterEach(() => fijarIdioma('es'))

describe('textoVersion', () => {
  it('con sha y fecha', () => {
    expect(textoVersion('abc1234', '2026-10-14')).toBe('versión abc1234 · 2026-10-14')
  })
  it('sin datos es dev', () => {
    expect(textoVersion()).toBe('versión dev')
  })
  it('solo sha es dev', () => {
    expect(textoVersion('abc1234')).toBe('versión dev')
  })
  it('textoVersionApp empieza con "versión "', () => {
    expect(textoVersionApp().startsWith('versión ')).toBe(true)
  })
  it('en inglés', () => {
    fijarIdioma('en')
    expect(textoVersion('abc1234', '2026-10-14')).toBe('version abc1234 · 2026-10-14')
    expect(textoVersion()).toBe('dev version')
  })
})
