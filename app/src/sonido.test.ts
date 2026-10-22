import { describe, expect, it, vi } from 'vitest'
import { crearAvisador } from './sonido'

function contextoFalso() {
  const osciladores: { start: ReturnType<typeof vi.fn>; frequency: { value: number } }[] = []
  const ctx = {
    currentTime: 0,
    destination: {},
    resume: vi.fn(() => Promise.resolve()),
    createOscillator: () => {
      const o = { frequency: { value: 0 }, connect: vi.fn(), start: vi.fn(), stop: vi.fn() }
      osciladores.push(o)
      return o
    },
    createGain: () => ({ gain: { setValueAtTime: vi.fn(), linearRampToValueAtTime: vi.fn() }, connect: vi.fn() }),
  }
  return { ctx: ctx as unknown as AudioContext, osciladores, resume: ctx.resume }
}

describe('crearAvisador', () => {
  it('desbloquear crea el contexto una vez y llama resume', () => {
    const f = contextoFalso()
    const crear = vi.fn(() => f.ctx)
    const av = crearAvisador({ crearContexto: crear, vibrar: vi.fn() })
    av.desbloquear()
    av.desbloquear()
    expect(crear).toHaveBeenCalledTimes(1)
    expect(f.resume).toHaveBeenCalledTimes(2)
  })
  it.each([['suspended', 1], ['interrupted', 1], ['running', 0]])('sonar con el contexto %s reanuda %i vez', (state, veces) => {
    const f = contextoFalso()
    ;(f.ctx as unknown as { state: string }).state = state
    crearAvisador({ crearContexto: () => f.ctx, vibrar: vi.fn() }).sonar()
    expect(f.resume).toHaveBeenCalledTimes(veces)
    expect(f.osciladores).toHaveLength(3)
  })
  it('si resume lanza, igual suena y vibra', () => {
    const f = contextoFalso()
    ;(f.ctx as unknown as { state: string }).state = 'interrupted'
    f.resume.mockImplementation(() => { throw new Error('x') })
    const vibrar = vi.fn()
    expect(() => crearAvisador({ crearContexto: () => f.ctx, vibrar }).sonar()).not.toThrow()
    expect(f.osciladores).toHaveLength(3)
    expect(vibrar).toHaveBeenCalled()
  })
  it('sonar: 3 pitidos a 880 Hz y vibra', () => {
    const f = contextoFalso()
    const vibrar = vi.fn()
    const av = crearAvisador({ crearContexto: () => f.ctx, vibrar })
    av.sonar()
    expect(f.osciladores).toHaveLength(3)
    expect(f.osciladores.every((o) => o.frequency.value === 880 && o.start.mock.calls.length === 1)).toBe(true)
    expect(vibrar).toHaveBeenCalledWith([200, 100, 200])
  })
  it('sin contexto o con fallas no lanza y aun así vibra', () => {
    const vibrar = vi.fn()
    const sinCtx = crearAvisador({ crearContexto: () => null, vibrar })
    expect(() => { sinCtx.desbloquear(); sinCtx.sonar() }).not.toThrow()
    expect(vibrar).toHaveBeenCalledTimes(1)
    const roto = crearAvisador({ crearContexto: () => { throw new Error('x') }, vibrar: () => { throw new Error('y') } })
    expect(() => { roto.desbloquear(); roto.sonar() }).not.toThrow()
  })
})
