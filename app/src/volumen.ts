import type { Serie } from './api'
import { sumarDias } from './prefill'
import { cuentaComoTrabajo } from './records'
import { KG_POR_LB, type Unidad } from './unidades'

export interface PuntoVolumen {
  fecha: string
  valor: number
}

/**
 * Volumen por día (hoy-dias+1 .. hoy) de un ejercicio. Agrupa por s.fecha (el día del usuario, nunca por ts),
 * ignora las rechazadas y los calentamientos. Σ pesoKg×reps, o Σ reps con peso corporal; en lb se convierte la suma. Días sin series = 0.
 */
export function volumenUltimosDias(
  series: (Serie & { estado?: string })[],
  ejercicioId: string,
  hoy: string,
  unidad: Unidad,
  cargaCorporal = false,
  dias = 6,
): PuntoVolumen[] {
  const porDia = new Map<string, number>()
  for (const s of series) {
    if (s.ejercicioId !== ejercicioId || s.estado === 'rechazada' || !cuentaComoTrabajo(s)) continue
    porDia.set(s.fecha, (porDia.get(s.fecha) ?? 0) + (cargaCorporal ? s.reps : s.pesoKg * s.reps))
  }
  return Array.from({ length: dias }, (_, i) => {
    const fecha = sumarDias(hoy, i - (dias - 1))
    const suma = porDia.get(fecha) ?? 0
    return { fecha, valor: Math.round(unidad === 'lb' && !cargaCorporal ? suma / KG_POR_LB : suma) }
  })
}
