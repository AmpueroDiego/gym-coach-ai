import { afterEach, describe, expect, it, vi } from 'vitest'
import { iniciarCiclo, MS_CUADRO, MS_FUNDIDO, MS_PAUSA, prefiereMenosMovimiento, siguienteCuadro } from './animacion'

afterEach(() => {
  vi.unstubAllGlobals()
  vi.useRealTimers()
})

describe('animacion', () => {
  it('alterna entre 0 y 1', () => {
    expect(siguienteCuadro(0)).toBe(1)
    expect(siguienteCuadro(1)).toBe(0)
    expect(MS_CUADRO).toBe(MS_PAUSA + MS_FUNDIDO)
  })

  it('el ciclo avanza tras la pausa y se repite', () => {
    vi.useFakeTimers()
    const f = vi.fn()
    iniciarCiclo(f)
    vi.advanceTimersByTime(MS_CUADRO - 1)
    expect(f).not.toHaveBeenCalled()
    vi.advanceTimersByTime(1)
    expect(f).toHaveBeenCalledTimes(1)
    vi.advanceTimersByTime(MS_CUADRO)
    expect(f).toHaveBeenCalledTimes(2)
  })

  it('detener el ciclo (pausa) no avanza más y uno nuevo reanuda', () => {
    vi.useFakeTimers()
    const f = vi.fn()
    const parar = iniciarCiclo(f)
    vi.advanceTimersByTime(MS_CUADRO)
    parar()
    vi.advanceTimersByTime(MS_CUADRO * 5)
    expect(f).toHaveBeenCalledTimes(1)
    iniciarCiclo(f)
    vi.advanceTimersByTime(MS_CUADRO)
    expect(f).toHaveBeenCalledTimes(2)
  })

  it('sin window asume menos movimiento', () => {
    expect(prefiereMenosMovimiento()).toBe(true)
  })

  it('sin matchMedia asume menos movimiento', () => {
    vi.stubGlobal('window', {})
    expect(prefiereMenosMovimiento()).toBe(true)
  })

  it('respeta la preferencia del sistema', () => {
    const con = (matches: boolean) =>
      vi.stubGlobal('window', { matchMedia: (q: string) => ({ matches: matches && q.includes('reduce') }) })
    con(true)
    expect(prefiereMenosMovimiento()).toBe(true)
    con(false)
    expect(prefiereMenosMovimiento()).toBe(false)
  })
})
