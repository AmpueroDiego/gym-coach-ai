import { useCallback, useLayoutEffect, useRef, useState, type RefObject } from 'react'
import { ajusteAlRedimensionar, calcularAncla, cercaDelFinal, destinoFinal, rellenoParaAncla, respuestaVisible } from './chatScroll'

// clave: cuenta|día; al cambiar, la conversación es otra y se reposiciona como al entrar.
type Opciones = { activo: boolean; nMensajes: number; pensando: boolean; ultimoRol: 'usuario' | 'coach' | null; clave?: string }

const reducida = () => typeof window.matchMedia === 'function' && window.matchMedia('(prefers-reduced-motion: reduce)').matches

// jsdom y navegadores viejos no traen Element.scrollTo.
const irA = (el: HTMLElement, top: number, behavior: ScrollBehavior) => {
  if (typeof el.scrollTo === 'function') el.scrollTo({ top, behavior })
  else el.scrollTop = top
}

const ultimoLi = (el: HTMLElement, clase: 'usuario' | 'coach') => {
  const li = el.querySelectorAll<HTMLElement>(`li.mensaje.${clase}`)
  return li[li.length - 1] as HTMLElement | undefined
}

// Scroll del chat: ancla tu mensaje arriba al enviar, no te arrastra si estás leyendo más arriba
// y aguanta el teclado. Las medidas viven en refs; solo `lejos`/`hayNuevo` re-renderizan.
export function useScrollChat(o: Opciones) {
  const lista = useRef<HTMLDivElement | null>(null)
  const relleno = useRef<HTMLDivElement | null>(null)
  const [lejos, setLejos] = useState(false)
  const [hayNuevo, setHayNuevo] = useState(false)

  const pendiente = useRef(false) // enviar() pidió anclar el próximo mensaje del usuario
  const ancla = useRef<number | null>(null) // scrollTop de la ancla activa
  const gesto = useRef(false) // el usuario tocó el scroll: no reajustar la ancla
  const altura = useRef(0) // último clientHeight medido
  const cerca = useRef(true) // último "cerca del final" medido
  const nRef = useRef(o.nMensajes)
  const previo = useRef({ n: o.nMensajes, pensando: o.pensando })
  useLayoutEffect(() => {
    nRef.current = o.nMensajes
  })

  const ponerRelleno = (px: number) => {
    if (relleno.current) relleno.current.style.height = `${px}px`
  }

  const medirLejos = () => {
    const el = lista.current
    const r = relleno.current
    if (!el || !r) return
    cerca.current = cercaDelFinal(el.scrollTop, el.clientHeight, r.offsetTop)
    setLejos(!cerca.current)
    if (cerca.current) setHayNuevo(false)
  }

  // Entrada / hidden->visible y cambios de alto de la lista.
  useLayoutEffect(() => {
    const el = lista.current
    if (!o.activo || !el || typeof ResizeObserver === 'undefined') return
    const posicionar = () => {
      const r = relleno.current
      ponerRelleno(0)
      ancla.current = null
      pendiente.current = false
      irA(el, nRef.current === 0 || !r ? 0 : destinoFinal(r.offsetTop, el.clientHeight), 'auto')
      cerca.current = true
      setLejos(false)
      setHayNuevo(false)
    }
    altura.current = el.clientHeight
    if (altura.current > 0) posicionar()
    const obs = new ResizeObserver(() => {
      const ahora = el.clientHeight
      const antes = altura.current
      altura.current = ahora
      const r = relleno.current
      if (ahora === 0 || !r) return
      if (antes === 0) {
        posicionar()
        return
      }
      if (ancla.current !== null) {
        const ultimo = ultimoLi(el, 'usuario')
        if (!gesto.current && ultimo) {
          const a = calcularAncla({ offsetTopUsuario: ultimo.offsetTop, clientHeight: ahora, altoContenido: r.offsetTop })
          ponerRelleno(a.relleno)
          ancla.current = a.scrollTop
          irA(el, a.scrollTop, 'auto')
        }
      } else {
        const top = ajusteAlRedimensionar({ scrollTop: el.scrollTop, alturaAntes: antes, alturaAhora: ahora, cercaAntes: cerca.current })
        if (top !== el.scrollTop) irA(el, top, 'auto')
      }
      medirLejos()
    })
    obs.observe(el)
    return () => obs.disconnect()
  }, [o.activo, o.clave])

  // Ancla del mensaje nuevo, encogimiento del relleno y aviso de respuesta nueva.
  useLayoutEffect(() => {
    const el = lista.current
    const r = relleno.current
    if (!o.activo || !el || !r) return
    const antes = previo.current
    previo.current = { n: o.nMensajes, pensando: o.pensando }

    const mio = ultimoLi(el, 'usuario')
    if (pendiente.current && o.ultimoRol === 'usuario' && mio) {
      pendiente.current = false
      const a = calcularAncla({ offsetTopUsuario: mio.offsetTop, clientHeight: el.clientHeight, altoContenido: r.offsetTop })
      ponerRelleno(a.relleno)
      ancla.current = a.scrollTop
      irA(el, a.scrollTop, reducida() ? 'auto' : 'smooth')
    } else if (ancla.current !== null) {
      // Solo se encoge: el contenido crece y el relleno cede su lugar.
      const nuevo = rellenoParaAncla(ancla.current, el.clientHeight, r.offsetTop)
      if (nuevo < r.offsetHeight) ponerRelleno(nuevo)
    }

    if (o.ultimoRol === 'coach' && !o.pensando && (o.nMensajes > antes.n || antes.pensando)) {
      const resp = ultimoLi(el, 'coach')
      if (resp && !respuestaVisible(resp.offsetTop, el.scrollTop, el.clientHeight)) setHayNuevo(true)
    }
    medirLejos()
  }, [o.activo, o.nMensajes, o.pensando, o.ultimoRol])

  const anclarProximo = useCallback(() => {
    pendiente.current = true
    gesto.current = false
  }, [])
  const alGesto = useCallback(() => {
    gesto.current = true
  }, [])
  const alScroll = useCallback(() => medirLejos(), [])
  const irAlFinal = useCallback(() => {
    const el = lista.current
    const r = relleno.current
    if (!el || !r) return
    irA(el, destinoFinal(r.offsetTop, el.clientHeight), reducida() ? 'auto' : 'smooth')
    setHayNuevo(false)
  }, [])
  const irAlInicio = useCallback(() => {
    if (lista.current) irA(lista.current, 0, 'auto')
  }, [])
  const reiniciar = useCallback(() => {
    ponerRelleno(0)
    ancla.current = null
    pendiente.current = false
    setHayNuevo(false)
  }, [])

  return {
    lista: lista as RefObject<HTMLDivElement | null>,
    relleno: relleno as RefObject<HTMLDivElement | null>,
    anclarProximo,
    alGesto,
    alScroll,
    irAlFinal,
    irAlInicio,
    reiniciar,
    lejos,
    hayNuevo,
  }
}
