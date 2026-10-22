// Nombres de los tipos de serie para etiquetas y botones (la lógica del ciclo vive en records.ts).
import type { TipoSerie } from './api'
import { textos } from './i18n'

const t = textos(
  { normal: 'Normal', calentamiento: 'Calentamiento', fallo: 'Fallo', drop: 'Drop' },
  { normal: 'Normal', calentamiento: 'Warm-up', fallo: 'Failure', drop: 'Drop set' },
)

/** Función y no constante: el nombre sigue al idioma de la app. */
export const tiposSerie = (): { tipo: TipoSerie; nombre: string }[] => {
  const n = t()
  return [
    { tipo: 'normal', nombre: n.normal },
    { tipo: 'calentamiento', nombre: n.calentamiento },
    { tipo: 'fallo', nombre: n.fallo },
    { tipo: 'drop', nombre: n.drop },
  ]
}

export const nombreTipo = (tipo: TipoSerie | undefined): string => t()[tipo ?? 'normal'] ?? t().normal
