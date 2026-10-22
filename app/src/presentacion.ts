// Formatos y validaciones de pantalla, puras para poder probarlas sin DOM.
import { clasificarFallo, type EjercicioRutina, type TipoSerie } from './api'
import { idiomaApp, localeApp, textos } from './i18n'
import { cuentaComoTrabajo } from './records'
import type { PuntoVolumen } from './volumen'
import type { EjercicioResumen } from './resumen'
import { KG_POR_LB, pesoEnTexto, resolverPesoKg, type Unidad } from './unidades'

const t = textos(
  {
    dias: ['dom', 'lun', 'mar', 'mié', 'jue', 'vie', 'sáb'],
    meses: ['ene', 'feb', 'mar', 'abr', 'may', 'jun', 'jul', 'ago', 'sep', 'oct', 'nov', 'dic'],
    iniciales: ['D', 'L', 'M', 'X', 'J', 'V', 'S'],
    sinDescanso: 'Sin descanso',
    reps: 'reps',
    yaNoExiste: 'ya no existe',
    ponleNombre: 'Ponle nombre a la rutina.',
    nombreLargo: (n: number) => `El nombre tiene máximo ${n} caracteres.`,
    sinEjercicios: 'Agrega al menos un ejercicio.',
    demasiados: (n: number) => `Máximo ${n} ejercicios por rutina.`,
    repetido: 'Hay un ejercicio repetido.',
    series: (i: number) => `Ejercicio ${i}: series 1–10.`,
    repsMal: (i: number) => `Ejercicio ${i}: reps 1–100.`,
    pesoMal: (i: number, u: string) => `Ejercicio ${i}: peso inválido (en ${u}).`,
    descansoMal: (i: number) => `Ejercicio ${i}: descanso inválido.`,
    rechazada: (m: string) => `El servidor la rechazó: ${m}`,
    limite: 'Demasiadas peticiones seguidas; espera un momento.',
    sesionVencida: 'Tu sesión venció; vuelve a entrar.',
    sinConexion: 'No se pudo conectar. Revisa tu conexión.',
  },
  {
    dias: ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'],
    meses: ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'],
    iniciales: ['S', 'M', 'T', 'W', 'T', 'F', 'S'],
    sinDescanso: 'No rest',
    reps: 'reps',
    yaNoExiste: 'no longer exists',
    ponleNombre: 'Give the routine a name.',
    nombreLargo: (n) => `The name can have up to ${n} characters.`,
    sinEjercicios: 'Add at least one exercise.',
    demasiados: (n) => `Up to ${n} exercises per routine.`,
    repetido: 'An exercise is repeated.',
    series: (i) => `Exercise ${i}: sets 1–10.`,
    repsMal: (i) => `Exercise ${i}: reps 1–100.`,
    pesoMal: (i, u) => `Exercise ${i}: invalid weight (in ${u}).`,
    descansoMal: (i) => `Exercise ${i}: invalid rest.`,
    rechazada: (m) => `The server rejected it: ${m}`,
    limite: 'Too many requests in a row; wait a moment.',
    sesionVencida: 'Your session expired; sign in again.',
    sinConexion: "Couldn't connect. Check your connection.",
  },
)

const fechaDe = (f: string) => {
  const [a, m, d] = f.split('-').map(Number)
  return new Date(a, m - 1, d)
}

/** '2026-10-06' -> 'mar 6 oct' (en: 'Tue Oct 6'). Arrays fijos: no depende del ICU del navegador. */
export function etiquetaFecha(f: string): string {
  const d = fechaDe(f)
  const { dias, meses } = t()
  return idiomaApp() === 'en'
    ? `${dias[d.getDay()]} ${meses[d.getMonth()]} ${d.getDate()}`
    : `${dias[d.getDay()]} ${d.getDate()} ${meses[d.getMonth()]}`
}

/** Inicial del día de la semana: L M X J V S D (en: S M T W T F S). */
export const inicialDia = (f: string): string => t().iniciales[fechaDe(f).getDay()]

/** 0 -> 'Sin descanso', 90 -> '90 s', 120 -> '2 min'. */
export function etiquetaDescanso(s: number): string {
  if (s === 0) return t().sinDescanso
  return s < 120 ? `${s} s` : `${s / 60} min`
}

/** Opciones del editor de rutinas (más amplio que DESCANSOS del descanso por defecto). */
export const DESCANSOS_RUTINA = [0, 30, 45, 60, 90, 120, 180, 300] as const

/** '4 × 8 · 80 kg' o '4 × 8' sin peso objetivo. */
export function textoObjetivo(o: Pick<EjercicioRutina, 'series' | 'reps' | 'pesoKg'>, u: Unidad): string {
  const base = `${o.series} × ${o.reps}`
  return o.pesoKg === null ? base : `${base} · ${pesoEnTexto(o.pesoKg, u)} ${u}`
}

const miles = (n: number) => Math.round(n).toLocaleString(localeApp())

/** Volumen de un ejercicio en su unidad; carga corporal en reps. */
export function textoVolumenEjercicio(e: EjercicioResumen, u: Unidad, corporal: boolean): string {
  if (corporal) return `${e.reps} ${t().reps}`
  return `${miles(u === 'lb' ? e.volumenKg / KG_POR_LB : e.volumenKg)} ${u}`
}

/**
 * Total de una sesión. Sumar kg con lb no tiene sentido: va en lb solo si todos los ejercicios con carga están en lb,
 * si no en kg. Los de carga corporal suman aparte en reps.
 */
export function textoVolumenSesion(
  ejercicios: EjercicioResumen[],
  unidadDe: (id: string) => Unidad,
  corporales: Set<string>,
): string {
  const conCarga = ejercicios.filter((e) => !corporales.has(e.ejercicioId))
  const reps = ejercicios.filter((e) => corporales.has(e.ejercicioId)).reduce((t, e) => t + e.reps, 0)
  const partes: string[] = []
  if (conCarga.length > 0) {
    const u: Unidad = conCarga.every((e) => unidadDe(e.ejercicioId) === 'lb') ? 'lb' : 'kg'
    const kg = conCarga.reduce((t, e) => t + e.volumenKg, 0)
    partes.push(`${miles(u === 'lb' ? kg / KG_POR_LB : kg)} ${u}`)
  }
  if (reps > 0) partes.push(`${reps} ${t().reps}`)
  return partes.join(' · ') || '0 kg'
}

export interface Grafico {
  /** Coordenadas en el viewBox, una por día. */
  puntos: { x: number; y: number }[]
  max: number
  iniciales: string[]
}

export const ANCHO_GRAFICO = 300
export const ALTO_GRAFICO = 72
const MARGEN_X = 14
const ARRIBA = 16
const BASE = 54

/** Coordenadas en el viewBox 300×72: día sin series cae a la base; todo en 0 queda plano en la base. */
export function puntosGrafico(serie: PuntoVolumen[]): Grafico {
  const max = Math.max(0, ...serie.map((p) => p.valor))
  const paso = serie.length > 1 ? (ANCHO_GRAFICO - 2 * MARGEN_X) / (serie.length - 1) : 0
  return {
    puntos: serie.map((p, i) => ({
      x: Math.round((MARGEN_X + i * paso) * 10) / 10,
      y: max === 0 ? BASE : Math.round((BASE - (p.valor / max) * (BASE - ARRIBA)) * 10) / 10,
    })),
    max,
    iniciales: serie.map((p) => inicialDia(p.fecha)),
  }
}

export const BASE_GRAFICO = BASE

export const MAX_RUTINAS = 20
export const MAX_EJERCICIOS_RUTINA = 15
export const MAX_NOMBRE_RUTINA = 40

/** Fila del editor: reps y peso como texto del input. baseKg = peso guardado antes (evita deriva kg/lb). */
export interface FilaRutina {
  id: string
  series: number
  reps: string
  pesoTexto: string
  baseKg: number | null
  descansoS: number
}

export const filaNueva = (id: string): FilaRutina => ({
  id,
  series: 3,
  reps: '10',
  pesoTexto: '',
  baseKg: null,
  // Descanso opcional: por defecto ninguno, nunca obliga a elegir.
  descansoS: 0,
})

/** Mismo criterio que progresoPlan (sin rechazadas ni calentamientos): 'hecho' al llegar a las series objetivo. */
export function estadoEjercicio(
  hechas: { estado?: string; tipoSerie?: TipoSerie }[],
  objetivoSeries: number,
): 'nada' | 'en curso' | 'hecho' {
  const n = hechas.filter((s) => s.estado !== 'rechazada' && cuentaComoTrabajo(s)).length
  return n === 0 ? 'nada' : n >= objetivoSeries ? 'hecho' : 'en curso'
}

/** Marca corta junto a la serie; normal (o ausente) no lleva marca. */
export function letraTipo(t: TipoSerie | undefined): 'C' | 'F' | 'D' | null {
  return t === 'calentamiento' ? 'C' : t === 'fallo' ? 'F' : t === 'drop' ? 'D' : null
}

export interface FilaResuelta {
  pesoKg: number
  reps: number
  pesoOk: boolean
  repsOk: boolean
  /** Rojo solo si hay texto y es inválido (vacío sin sugerencia no se pinta). */
  pesoMal: boolean
  repsMal: boolean
}

/** Fila del plan: campo vacío = su sugerido; escrito = lo escrito (anti-deriva kg/lb sobre el sugerido). */
export function resolverFila(
  peso: string,
  reps: string,
  unidad: Unidad,
  sugerida: { pesoKg: number | null; reps: number | null },
): FilaResuelta {
  const pesoVacio = peso.trim() === ''
  const repsVacio = reps.trim() === ''
  const pesoKg = pesoVacio ? (sugerida.pesoKg ?? NaN) : resolverPesoKg(peso, unidad, sugerida.pesoKg)
  // Number('') = 0 y Number('1e1') = 10: reps solo con dígitos.
  const repsN = repsVacio ? (sugerida.reps ?? NaN) : /^\d+$/.test(reps.trim()) ? Number(reps.trim()) : NaN
  const pesoOk = Number.isFinite(pesoKg)
  const repsOk = Number.isInteger(repsN) && repsN >= 1 && repsN <= 100
  return { pesoKg, reps: repsN, pesoOk, repsOk, pesoMal: !pesoVacio && !pesoOk, repsMal: !repsVacio && !repsOk }
}

/** Ejercicio sin entrada en el catálogo: se avisa en vez de mostrar el id pelado. */
export const etiquetaEjercicio = (id: string, nombre: string, existe: boolean): string =>
  existe ? nombre : `${id} (${t().yaNoExiste})`

export const filaDe =(e: EjercicioRutina, u: Unidad): FilaRutina => ({
  id: e.id,
  series: e.series,
  reps: String(e.reps),
  pesoTexto: e.pesoKg === null ? '' : pesoEnTexto(e.pesoKg, u),
  baseKg: e.pesoKg,
  descansoS: e.descansoS,
})

/** Refleja los topes del servidor (que es la autoridad). Devuelve el primer error o la rutina lista para el PUT. */
export function armarRutina(
  nombre: string,
  filas: FilaRutina[],
  unidadDe: (id: string) => Unidad,
): { error: string } | { nombre: string; ejercicios: EjercicioRutina[] } {
  const n = nombre.trim()
  if (n === '') return { error: t().ponleNombre }
  if (n.length > MAX_NOMBRE_RUTINA) return { error: t().nombreLargo(MAX_NOMBRE_RUTINA) }
  if (filas.length === 0) return { error: t().sinEjercicios }
  if (filas.length > MAX_EJERCICIOS_RUTINA) return { error: t().demasiados(MAX_EJERCICIOS_RUTINA) }
  if (new Set(filas.map((f) => f.id)).size !== filas.length) return { error: t().repetido }
  const ejercicios: EjercicioRutina[] = []
  for (const [i, f] of filas.entries()) {
    const reps = Number(f.reps.trim())
    if (!Number.isInteger(f.series) || f.series < 1 || f.series > 10) return { error: t().series(i + 1) }
    if (f.reps.trim() === '' || !Number.isInteger(reps) || reps < 1 || reps > 100)
      return { error: t().repsMal(i + 1) }
    let pesoKg: number | null = null
    if (f.pesoTexto.trim() !== '') {
      const u = unidadDe(f.id)
      pesoKg = resolverPesoKg(f.pesoTexto, u, f.baseKg)
      if (!Number.isFinite(pesoKg)) return { error: t().pesoMal(i + 1, u) }
    }
    if (!Number.isInteger(f.descansoS) || f.descansoS < 0 || f.descansoS > 600)
      return { error: t().descansoMal(i + 1) }
    ejercicios.push({ id: f.id, series: f.series, reps, pesoKg, descansoS: f.descansoS })
  }
  return { nombre: n, ejercicios }
}

/** Texto para el usuario cuando falla una llamada a la API (400: el motivo del servidor). */
export function mensajeFallo(e: unknown): string {
  const f = clasificarFallo(e)
  if (f.tipo === 'rechazada') return t().rechazada(f.motivo)
  if (f.tipo === 'limite') return t().limite
  if (f.tipo === 'noAutenticado') return t().sesionVencida
  return t().sinConexion
}

/** Mueve el elemento i en dir (-1 sube, 1 baja); fuera de rango no cambia nada. */
export function mover<T>(lista: T[], i: number, dir: -1 | 1): T[] {
  const j = i + dir
  if (i < 0 || i >= lista.length || j < 0 || j >= lista.length) return lista
  const copia = [...lista]
  ;[copia[i], copia[j]] = [copia[j], copia[i]]
  return copia
}
