import { useEffect, useState } from 'react'

/** Con `interactive-widget=resizes-content` el teclado achica el viewport: más de `umbral` px menos = abierto. */
export function tecladoAbierto(altoBase: number, altoActual: number, umbral = 120): boolean {
  return altoBase - altoActual > umbral
}

const TIPOS_TEXTO = ['text', 'search', 'number', 'tel', 'email', 'url', 'password']

export function esCampoDeTexto(el: { tagName?: string; type?: string; readOnly?: boolean; isContentEditable?: boolean } | null): boolean {
  if (!el) return false
  if (el.isContentEditable) return true
  const tag = el.tagName?.toUpperCase()
  if (tag === 'TEXTAREA') return !el.readOnly
  if (tag === 'INPUT') return !el.readOnly && (!el.type || TIPOS_TEXTO.includes(el.type.toLowerCase()))
  return false
}

/** "atrás" cierra el teclado sin quitar el foco: al verlo abierto y luego cerrado, hay que hacer blur. */
export function transicionTeclado(prev: { visto: boolean }, altoBase: number, altoActual: number): { visto: boolean; cerrar: boolean } {
  if (tecladoAbierto(altoBase, altoActual)) return { visto: true, cerrar: false }
  if (prev.visto) return { visto: false, cerrar: true }
  return { visto: prev.visto, cerrar: false }
}

const altoViewport = () => window.visualViewport?.height ?? window.innerHeight

/** true mientras hay un campo de texto enfocado; se enciende al instante del foco, sin esperar el resize. */
export function useTeclado(): boolean {
  const [abierto, setAbierto] = useState(false)

  useEffect(() => {
    let base = altoViewport()
    let estado: { visto: boolean; cerrar: boolean } = { visto: false, cerrar: false }

    const alFoco = (e: FocusEvent) => setAbierto(esCampoDeTexto(e.target as HTMLElement | null))
    const alPerderFoco = (e: FocusEvent) => {
      if (!esCampoDeTexto(e.relatedTarget as HTMLElement | null)) setAbierto(false)
    }
    let ancho = window.innerWidth
    const alRedimensionar = () => {
      const alto = altoViewport()
      // Rotar cambia el ancho: la base vieja ya no sirve (en horizontal parecería teclado abierto).
      if (window.innerWidth !== ancho) {
        ancho = window.innerWidth
        base = alto
        estado = { visto: false, cerrar: false }
        return
      }
      if (alto > base) base = alto
      estado = transicionTeclado(estado, base, alto)
      const activo = document.activeElement as HTMLElement | null
      if (estado.cerrar && esCampoDeTexto(activo)) activo?.blur()
    }

    document.addEventListener('focusin', alFoco)
    document.addEventListener('focusout', alPerderFoco)
    window.addEventListener('resize', alRedimensionar)
    window.visualViewport?.addEventListener('resize', alRedimensionar)
    return () => {
      document.removeEventListener('focusin', alFoco)
      document.removeEventListener('focusout', alPerderFoco)
      window.removeEventListener('resize', alRedimensionar)
      window.visualViewport?.removeEventListener('resize', alRedimensionar)
    }
  }, [])

  return abierto
}
