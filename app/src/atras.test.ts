import { describe, expect, it } from 'vitest'
import { decidirPop, profundidad, retroceder } from './atras'

describe('botón atrás', () => {
  it('cuenta un nivel por pestaña y uno por pantalla encima', () => {
    expect(profundidad(false, false)).toBe(0)
    expect(profundidad(true, false)).toBe(1)
    expect(profundidad(false, true)).toBe(1)
    expect(profundidad(true, true)).toBe(2)
  })
  it('cierra primero lo de encima y luego vuelve a Entreno', () => {
    expect(retroceder(true, true, 1)).toEqual({ enOtraPestana: true, sobrepuesta: false })
    expect(retroceder(true, true, 0)).toEqual({ enOtraPestana: false, sobrepuesta: false })
    expect(retroceder(true, false, 0)).toEqual({ enOtraPestana: false, sobrepuesta: false })
    expect(retroceder(false, true, 0)).toEqual({ enOtraPestana: false, sobrepuesta: false })
  })
  it('un popstate sin go pendiente es del usuario', () => {
    expect(decidirPop(2, 0, 1)).toEqual({ accion: 'retroceder', nuevo: 1 })
  })
  it('ignora el popstate propio cuando el nivel coincide', () => {
    expect(decidirPop(0, 1, 0)).toEqual({ accion: 'ignorar', ignorar: 0, reempujar: [], ir: 0 })
  })
  it('con varios go pendientes solo compara el último', () => {
    expect(decidirPop(1, 2, 0)).toEqual({ accion: 'ignorar', ignorar: 1, reempujar: [], ir: 0 })
  })
  it('repone los niveles si la UI subió antes del popstate propio', () => {
    expect(decidirPop(2, 1, 0)).toEqual({ accion: 'ignorar', ignorar: 0, reempujar: [1, 2], ir: 0 })
  })
  it('vuelve a bajar si el historial quedó más arriba de lo esperado', () => {
    expect(decidirPop(0, 1, 1)).toEqual({ accion: 'ignorar', ignorar: 1, reempujar: [], ir: -1 })
  })
  it('no toca nada si ya está a esa profundidad', () => {
    expect(retroceder(true, false, 1)).toEqual({ enOtraPestana: true, sobrepuesta: false })
  })
})
