import { describe, expect, it } from 'vitest'
import { ajusteAlRedimensionar, calcularAncla, cercaDelFinal, destinoFinal, respuestaVisible, rellenoParaAncla } from './chatScroll'

describe('cercaDelFinal', () => {
  it('80 px del final cuenta, 81 no', () => {
    expect(cercaDelFinal(120, 500, 700)).toBe(true) // faltan 80
    expect(cercaDelFinal(119, 500, 700)).toBe(false) // faltan 81
  })
  it('contenido corto y oculto', () => {
    expect(cercaDelFinal(0, 500, 300)).toBe(true)
    expect(cercaDelFinal(0, 0, 5000)).toBe(true)
  })
})

describe('calcularAncla', () => {
  it('contenido corto: hay relleno y el scroll queda bajo el margen', () => {
    const r = calcularAncla({ offsetTopUsuario: 200, clientHeight: 500, altoContenido: 400 })
    expect(r.scrollTop).toBe(188)
    expect(r.relleno).toBe(288)
    expect(r.scrollTop + 500).toBe(400 + r.relleno)
  })
  it('contenido largo: sin relleno', () => {
    expect(calcularAncla({ offsetTopUsuario: 900, clientHeight: 500, altoContenido: 2000 }).relleno).toBe(0)
  })
  it('offsetTop menor que el margen: scrollTop 0', () => {
    expect(calcularAncla({ offsetTopUsuario: 5, clientHeight: 500, altoContenido: 300 }).scrollTop).toBe(0)
  })
})

describe('no negativos', () => {
  it('rellenoParaAncla y destinoFinal', () => {
    expect(rellenoParaAncla(10, 100, 1000)).toBe(0)
    expect(destinoFinal(100, 500)).toBe(0)
    expect(destinoFinal(1000, 500)).toBe(500)
  })
})

describe('ajusteAlRedimensionar', () => {
  it('cerca y la altura baja 300: sube 300', () => {
    expect(ajusteAlRedimensionar({ scrollTop: 100, alturaAntes: 600, alturaAhora: 300, cercaAntes: true })).toBe(400)
  })
  it('cerca y la altura sube: no queda negativo', () => {
    expect(ajusteAlRedimensionar({ scrollTop: 100, alturaAntes: 300, alturaAhora: 900, cercaAntes: true })).toBe(0)
  })
  it('lejos: igual', () => {
    expect(ajusteAlRedimensionar({ scrollTop: 100, alturaAntes: 600, alturaAhora: 300, cercaAntes: false })).toBe(100)
  })
})

describe('respuestaVisible', () => {
  it('dentro, arriba, abajo y borde', () => {
    expect(respuestaVisible(300, 100, 500)).toBe(true)
    expect(respuestaVisible(50, 100, 500)).toBe(false)
    expect(respuestaVisible(700, 100, 500)).toBe(false)
    expect(respuestaVisible(560, 100, 500)).toBe(true) // 100+500-40
    expect(respuestaVisible(561, 100, 500)).toBe(false)
  })
})
