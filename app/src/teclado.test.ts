import { describe, expect, it } from 'vitest'
import { esCampoDeTexto, tecladoAbierto, transicionTeclado } from './teclado'

describe('tecladoAbierto', () => {
  it('detecta el achique del viewport', () => {
    expect(tecladoAbierto(800, 800)).toBe(false)
    expect(tecladoAbierto(800, 560)).toBe(true)
    expect(tecladoAbierto(800, 700)).toBe(false)
  })
  it('frontera en 120', () => {
    expect(tecladoAbierto(800, 680)).toBe(false)
    expect(tecladoAbierto(800, 679)).toBe(true)
  })
})

describe('esCampoDeTexto', () => {
  it('acepta campos editables', () => {
    expect(esCampoDeTexto({ tagName: 'TEXTAREA' })).toBe(true)
    expect(esCampoDeTexto({ tagName: 'INPUT' })).toBe(true)
    for (const type of ['text', 'search', 'number', 'tel', 'email', 'url', 'password']) {
      expect(esCampoDeTexto({ tagName: 'INPUT', type })).toBe(true)
    }
    expect(esCampoDeTexto({ tagName: 'DIV', isContentEditable: true })).toBe(true)
  })
  it('rechaza lo demás', () => {
    expect(esCampoDeTexto(null)).toBe(false)
    for (const type of ['checkbox', 'radio', 'range', 'button', 'submit', 'file', 'color', 'date']) {
      expect(esCampoDeTexto({ tagName: 'INPUT', type })).toBe(false)
    }
    expect(esCampoDeTexto({ tagName: 'INPUT', type: 'text', readOnly: true })).toBe(false)
    expect(esCampoDeTexto({ tagName: 'TEXTAREA', readOnly: true })).toBe(false)
    expect(esCampoDeTexto({ tagName: 'BUTTON' })).toBe(false)
    expect(esCampoDeTexto({ tagName: 'DIV' })).toBe(false)
  })
})

describe('transicionTeclado', () => {
  it('sin haber visto el teclado abierto no cierra', () => {
    expect(transicionTeclado({ visto: false }, 800, 800)).toEqual({ visto: false, cerrar: false })
  })
  it('abre, cierra y no repite', () => {
    const a = transicionTeclado({ visto: false }, 800, 500)
    expect(a).toEqual({ visto: true, cerrar: false })
    const b = transicionTeclado(a, 800, 800)
    expect(b).toEqual({ visto: false, cerrar: true })
    expect(transicionTeclado(b, 800, 800)).toEqual({ visto: false, cerrar: false })
  })
})
