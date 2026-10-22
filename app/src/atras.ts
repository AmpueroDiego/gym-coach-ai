// Botón atrás de Android: que retroceda dentro de la app (sobrepuesta -> pestaña -> Entreno) y no la cierre.
// Modelo: profundidad 0 = Entreno sin nada abierto; +1 por estar en otra pestaña; +1 por una pantalla encima.
// Cada nivel es UNA entrada del historial: cambiar de pestaña no acumula entradas.
import { useEffect, useRef } from 'react'
import { textos } from './i18n'

export const profundidad = (enOtraPestana: boolean, sobrepuesta: boolean): number => (enOtraPestana ? 1 : 0) + (sobrepuesta ? 1 : 0)

/** Estado tras retroceder hasta `nuevo`: primero se cierra lo de encima, luego se vuelve a Entreno. */
export function retroceder(enOtraPestana: boolean, sobrepuesta: boolean, nuevo: number): { enOtraPestana: boolean; sobrepuesta: boolean } {
  let o = enOtraPestana
  let s = sobrepuesta
  while (profundidad(o, s) > nuevo) {
    if (s) s = false
    else o = false
  }
  return { enOtraPestana: o, sobrepuesta: s }
}

/** Mismo texto en la flecha ← del editor de rutinas y en el atrás de Android. */
const t = textos(
  { descartar: 'Hay cambios sin guardar. ¿Salir y descartarlos?' },
  { descartar: 'You have unsaved changes. Leave and discard them?' },
)
export const confirmarDescartar = (): string => t().descartar

const MARCA ='gcNivel'

function nivelDe(estado: unknown): number {
  const n = typeof estado === 'object' && estado !== null ? (estado as Record<string, unknown>)[MARCA] : undefined
  return typeof n === 'number' && Number.isInteger(n) && n > 0 && n <= 2 ? n : 0
}

export type DecisionPop =
  | { accion: 'ignorar'; ignorar: number; reempujar: number[]; ir: number }
  | { accion: 'retroceder'; nuevo: number }

/**
 * Qué hacer ante un popstate. `esperado`: nivel que la UI cree tener; `ignorar`: popstates propios (history.go) pendientes.
 * Al consumir el último propio, si el historial quedó en otro nivel que el esperado (la UI subió antes de que
 * el go se ejecutara) se repone: por debajo, empujando los niveles que faltan; por encima, con otro go.
 */
export function decidirPop(esperado: number, ignorar: number, nivelEstado: number): DecisionPop {
  if (ignorar <= 0) return { accion: 'retroceder', nuevo: nivelEstado }
  const resto = ignorar - 1
  if (resto > 0 || nivelEstado === esperado) return { accion: 'ignorar', ignorar: resto, reempujar: [], ir: 0 }
  if (nivelEstado < esperado) {
    const reempujar: number[] = []
    for (let i = nivelEstado + 1; i <= esperado; i++) reempujar.push(i)
    return { accion: 'ignorar', ignorar: 0, reempujar, ir: 0 }
  }
  return { accion: 'ignorar', ignorar: 1, reempujar: [], ir: esperado - nivelEstado }
}

/**
 * `nivel`: profundidad actual de la UI. Al subir se empujan entradas; al bajar por la UI (✕, guardar) se retiran
 * con history.go; si bajó el usuario con el atrás, `alRetroceder` recibe la profundidad que quedó.
 * `alRetroceder` puede devolver false para vetar el atrás (p.ej. cambios sin guardar): se vuelve a empujar el nivel.
 */
export function useBotonAtras(nivel: number, alRetroceder: (nuevo: number) => boolean | void) {
  // Tras un reload la entrada con nuestra marca sigue ahí: se cuenta para retirarla en el primer efecto.
  const empujadas = useRef(nivelDe(history.state))
  const ignorar = useRef(0)
  const cb = useRef(alRetroceder)

  useEffect(() => {
    cb.current = alRetroceder
  })

  useEffect(() => {
    const n = empujadas.current
    if (n < nivel) {
      for (let i = n + 1; i <= nivel; i++) history.pushState({ [MARCA]: i }, '')
      empujadas.current = nivel
    } else if (n > nivel) {
      empujadas.current = nivel
      ignorar.current++
      history.go(nivel - n)
    }
  }, [nivel])

  useEffect(() => {
    const alPop = (e: PopStateEvent) => {
      const d = decidirPop(empujadas.current, ignorar.current, nivelDe(e.state))
      if (d.accion === 'ignorar') {
        ignorar.current = d.ignorar
        for (const i of d.reempujar) history.pushState({ [MARCA]: i }, '')
        if (d.ir !== 0) history.go(d.ir)
        return
      }
      const previo = empujadas.current
      empujadas.current = d.nuevo
      if (cb.current(d.nuevo) === false && d.nuevo < previo) {
        for (let i = d.nuevo + 1; i <= previo; i++) history.pushState({ [MARCA]: i }, '')
        empujadas.current = previo
      }
    }
    window.addEventListener('popstate', alPop)
    return () => window.removeEventListener('popstate', alPop)
  }, [])
}
