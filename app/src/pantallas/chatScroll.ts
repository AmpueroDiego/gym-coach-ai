// Cálculos puros del scroll del chat (sin DOM) para poder probarlos.
export const UMBRAL_FINAL = 80 // px
export const MARGEN_ANCLA = 12 // px bajo el tope

/** "Final" = fin del contenido SIN el relleno. clientHeight 0 (oculto) => true. */
export function cercaDelFinal(scrollTop: number, clientHeight: number, altoContenido: number, umbral = UMBRAL_FINAL): boolean {
  if (clientHeight === 0) return true
  return altoContenido - (scrollTop + clientHeight) <= umbral
}

export function destinoFinal(altoContenido: number, clientHeight: number): number {
  return Math.max(0, altoContenido - clientHeight)
}

/** Relleno bajo el contenido para que el scroll pueda llegar a scrollTopAncla. */
export function rellenoParaAncla(scrollTopAncla: number, clientHeight: number, altoContenido: number): number {
  return Math.max(0, scrollTopAncla + clientHeight - altoContenido)
}

export function calcularAncla(e: { offsetTopUsuario: number; clientHeight: number; altoContenido: number; margen?: number }): { scrollTop: number; relleno: number } {
  const scrollTop = Math.max(0, e.offsetTopUsuario - (e.margen ?? MARGEN_ANCLA))
  return { scrollTop, relleno: rellenoParaAncla(scrollTop, e.clientHeight, e.altoContenido) }
}

/** Si estaba cerca del final, mantiene el final pegado cuando cambia la altura (teclado). */
export function ajusteAlRedimensionar(e: { scrollTop: number; alturaAntes: number; alturaAhora: number; cercaAntes: boolean }): number {
  return e.cercaAntes ? Math.max(0, e.scrollTop + (e.alturaAntes - e.alturaAhora)) : e.scrollTop
}

/** El inicio de la respuesta se ve (con 40px de holgura abajo). */
export function respuestaVisible(offsetTopRespuesta: number, scrollTop: number, clientHeight: number): boolean {
  return offsetTopRespuesta >= scrollTop && offsetTopRespuesta <= scrollTop + clientHeight - 40
}
