/** Cuánto se queda cada foto quieta antes de empezar el fundido. */
export const MS_PAUSA = 1100
/** Duración del fundido cruzado; debe coincidir con la transición de ficha.css. */
export const MS_FUNDIDO = 400
/** Cada cuánto cambia la foto de inicio a fin del movimiento: pausa + fundido. */
export const MS_CUADRO = MS_PAUSA + MS_FUNDIDO

export function siguienteCuadro(i: 0 | 1): 0 | 1 {
  return i === 0 ? 1 : 0
}

/** Llama a `alCambiar` cada MS_CUADRO (setTimeout encadenado). Devuelve la función que lo detiene. */
export function iniciarCiclo(alCambiar: () => void): () => void {
  let id: ReturnType<typeof setTimeout>
  const paso = () => {
    id = setTimeout(() => {
      alCambiar()
      paso()
    }, MS_CUADRO)
  }
  paso()
  return () => clearTimeout(id)
}

/** Sin ventana o sin matchMedia se asume movimiento reducido: mejor fotos quietas que animar a ciegas. */
export function prefiereMenosMovimiento(): boolean {
  if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') return true
  return window.matchMedia('(prefers-reduced-motion: reduce)').matches
}
