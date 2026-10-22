import { describe, expect, it } from 'vitest'
import { derivarSesionUI } from './sesionUI'

const diego = { sub: '42', nombre: 'Diego', foto: null }
const otra = { sub: '7', nombre: 'Otra', foto: null }

describe('derivarSesionUI', () => {
  it('pendiente: sin banda, sin sesión y esperando', () => {
    expect(derivarSesionUI('pendiente', null, diego)).toEqual({ delaCuenta: false, conSesion: false, mostrarReconectar: false, esperando: true })
  })
  it('sin_red: conSesion optimista y sin banda', () => {
    const r = derivarSesionUI('sin_red', null, diego)
    expect(r.conSesion).toBe(true)
    expect(r.mostrarReconectar).toBe(false)
    expect(r.esperando).toBe(false)
  })
  it('ok con el mismo sub: conSesion', () => {
    expect(derivarSesionUI('ok', diego, diego)).toEqual({ delaCuenta: true, conSesion: true, mostrarReconectar: false, esperando: false })
  })
  it('ok con otro sub: Reconectar y sin sesión', () => {
    const r = derivarSesionUI('ok', otra, diego)
    expect(r.conSesion).toBe(false)
    expect(r.mostrarReconectar).toBe(true)
  })
  it('ausente: Reconectar', () => {
    const r = derivarSesionUI('ausente', null, diego)
    expect(r.conSesion).toBe(false)
    expect(r.mostrarReconectar).toBe(true)
  })
  it('sin cuenta: nada', () => {
    for (const v of ['pendiente', 'ok', 'sin_red', 'ausente'] as const) {
      expect(derivarSesionUI(v, diego, null)).toEqual({ delaCuenta: false, conSesion: false, mostrarReconectar: false, esperando: false })
    }
  })
})
