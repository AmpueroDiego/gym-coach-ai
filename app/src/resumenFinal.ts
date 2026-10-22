// Resumen al terminar la rutina: hoy contra el promedio del último mes, solo con los números reales.
import type { Serie } from './api'
import { formatoReloj, progresoPlan, tiempoEntrenoS } from './contador'
import { textos } from './i18n'
import type { Plan } from './plan'
import { sumarDias } from './prefill'
import { cuentaComoTrabajo } from './records'
import { textoVolumenSesion } from './presentacion'
import type { Unidad } from './unidades'

const t = textos(
  {
    sinSeries: 'Sin series en esta rutina hoy.',
    primeraVez: 'Primera vez con estos ejercicios: ya tienes tu línea base.',
    enLinea: 'En línea con tu promedio del mes (±5%).',
    contra: (partes: string) => `Contra tu promedio del mes: ${partes}.`,
    en: (pct: string, nombre: string) => `${pct} en ${nombre}`,
    texto: (dur: string, series: string, ejs: string, vol: string, avance: string) =>
      `${dur} · Series ${series} · Ejercicios ${ejs} · Volumen ${vol}. ${avance}`,
    nuevo: (n: string) => `${n}: nuevo`,
    delPromedio: (n: string, pct: number) => `${n}: ${pct}% del promedio`,
    ariaRadar: (v: string) => `Hoy contra tu promedio del último mes. ${v}.`,
  },
  {
    sinSeries: 'No sets in this routine today.',
    primeraVez: "First time with these exercises: you now have your baseline.",
    enLinea: 'In line with your monthly average (±5%).',
    contra: (partes) => `Versus your monthly average: ${partes}.`,
    en: (pct, nombre) => `${pct} on ${nombre}`,
    texto: (dur, series, ejs, vol, avance) => `${dur} · Sets ${series} · Exercises ${ejs} · Volume ${vol}. ${avance}`,
    nuevo: (n) => `${n}: new`,
    delPromedio: (n, pct) => `${n}: ${pct}% of average`,
    ariaRadar: (v) => `Today versus your average for the last month. ${v}.`,
  },
)

export const MIN_EJES_RADAR = 3
export const MAX_EJES_RADAR = 15
/** Solo visual: el pct real va sin recortar en datos y aria-label. */
export const TOPE_VISUAL_PCT = 150
const UMBRAL_PCT = 5
const MAX_FRASES = 3
const DIAS_MES = 30

export interface Eje {
  ejercicioId: string
  /** Volumen de hoy: Σ pesoKg×reps (kg canónico) o Σ reps en carga corporal. */
  hoy: number
  /** Media por día de entreno en hoy-30..hoy-1; null sin historial. */
  promedioMes: number | null
  /** hoy / promedio × 100, sin recortar; null si no hay promedio (eje 'nuevo'). */
  pct: number | null
}

export interface ResumenRutina {
  ejes: Eje[]
  /** ≥3 ejes: hay radar; si no, solo texto. */
  radar: boolean
  duracion: string
  series: string
  ejercicios: string
  volumen: string
  avance: string
  texto: string
}

type SerieConEstado = Serie & { estado?: string }

const volumen = (ss: Serie[], corporal: boolean) => ss.reduce((t, s) => t + (corporal ? s.reps : s.pesoKg * s.reps), 0)

const porcentaje = (pct: number) => {
  const d = Math.round(pct - 100)
  return d >= 0 ? `+${d}%` : `−${-d}%`
}

export function resumenDeRutina(
  series: SerieConEstado[],
  plan: Plan,
  hoy: string,
  unidadDe: (id: string) => Unidad,
  ahora: number,
  corporales: Set<string> = new Set(),
  nombre: (id: string) => string = (id) => id,
): ResumenRutina {
  // El calentamiento no es trabajo: no suma volumen ni crea eje ni día de promedio.
  const validas = series.filter((s) => s.estado !== 'rechazada' && cuentaComoTrabajo(s))
  const deHoy = validas.filter((s) => s.fecha === hoy)
  const desde = sumarDias(hoy, -DIAS_MES)
  const ayer = sumarDias(hoy, -1)
  // Ids repetidos en el plan = un eje.
  const ids = [...new Set(plan.ejercicios.map((e) => e.id))]

  const ejes: Eje[] = []
  for (const id of ids) {
    const corporal = corporales.has(id)
    const hoyEj = deHoy.filter((s) => s.ejercicioId === id)
    if (hoyEj.length === 0) continue
    const porDia = new Map<string, Serie[]>()
    for (const s of validas) {
      if (s.ejercicioId !== id || s.fecha < desde || s.fecha > ayer) continue
      porDia.set(s.fecha, [...(porDia.get(s.fecha) ?? []), s])
    }
    const dias = [...porDia.values()].map((ss) => volumen(ss, corporal))
    const promedioMes = dias.length === 0 ? null : dias.reduce((t, v) => t + v, 0) / dias.length
    const v = volumen(hoyEj, corporal)
    const pct = promedioMes === null || promedioMes <= 0 ? null : (v * 100) / promedioMes
    ejes.push({ ejercicioId: id, hoy: v, promedioMes, pct })
  }
  const cortados = ejes.slice(0, MAX_EJES_RADAR)

  const p = progresoPlan(deHoy, plan.ejercicios)
  const duracion = formatoReloj(tiempoEntrenoS(plan, ahora))
  const seriesTxt = `${p.seriesHechas}/${p.seriesObjetivo}`
  const ejerciciosTxt = `${p.ejerciciosHechos}/${p.ejerciciosTotal}`
  const resumenes = ejes.map((e) => ({
    ejercicioId: e.ejercicioId,
    series: [],
    volumenKg: corporales.has(e.ejercicioId) ? 0 : e.hoy,
    reps: deHoy.filter((s) => s.ejercicioId === e.ejercicioId).reduce((t, s) => t + s.reps, 0),
  }))
  const volumenTxt = ejes.length > 0 ? textoVolumenSesion(resumenes, unidadDe, corporales) : '0 kg'

  // sort es estable: en empates queda el orden del plan.
  const conPct = ejes.filter((e): e is Eje & { pct: number } => e.pct !== null)
  const mejoras = conPct.filter((e) => e.pct - 100 >= UMBRAL_PCT).sort((a, b) => b.pct - a.pct).slice(0, MAX_FRASES)
  const bajadas = conPct.filter((e) => e.pct - 100 <= -UMBRAL_PCT).sort((a, b) => a.pct - b.pct).slice(0, MAX_FRASES)
  const lista = (es: (Eje & { pct: number })[]) => es.map((e) => t().en(porcentaje(e.pct), nombre(e.ejercicioId))).join(', ')

  let avance: string
  if (ejes.length === 0) avance = t().sinSeries
  else if (conPct.length === 0) avance = t().primeraVez
  else if (mejoras.length === 0 && bajadas.length === 0) avance = t().enLinea
  else {
    const partes = [mejoras.length > 0 ? lista(mejoras) : '', bajadas.length > 0 ? lista(bajadas) : ''].filter(Boolean)
    avance = t().contra(partes.join('; '))
  }

  return {
    ejes: cortados,
    radar: cortados.length >= MIN_EJES_RADAR,
    duracion,
    series: seriesTxt,
    ejercicios: ejerciciosTxt,
    volumen: volumenTxt,
    avance,
    texto: t().texto(duracion, seriesTxt, ejerciciosTxt, volumenTxt, avance),
  }
}

/** Abrevia etiquetas largas del radar. */
export const abreviar = (s: string, max = 16) => (s.length <= max ? s : s.slice(0, max - 1).trimEnd() + '…')

/** Texto accesible con los valores reales (sin recortar al 150%). */
export function ariaRadar(ejes: Eje[], nombre: (id: string) => string): string {
  const v = ejes.map((e) => (e.pct === null ? t().nuevo(nombre(e.ejercicioId)) : t().delPromedio(nombre(e.ejercicioId), Math.round(e.pct))))
  return t().ariaRadar(v.join('; '))
}
