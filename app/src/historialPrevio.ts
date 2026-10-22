import { sumarDias } from './prefill'

/** Tope de ventanas hacia atrás (12 × 30 días ≈ un año). */
export const MAX_VENTANAS = 12

/** Ventana k (1 = la inmediatamente anterior a los últimos `dias`): `dias` días justo antes de la anterior. */
export function ventanaPrevia(hoy: string, dias: number, k: number): { desde: string; hasta: string } {
  return { desde: sumarDias(hoy, -(dias * (k + 1) - 1)), hasta: sumarDias(hoy, -(dias * k)) }
}

/** Sigue habiendo ventanas que pedir: la última trajo series y no se llegó al tope. Una ventana vacía corta. */
export function hayMasVentanas(k: number, seriesDeLaVentana: number): boolean {
  return k < MAX_VENTANAS && seriesDeLaVentana > 0
}
