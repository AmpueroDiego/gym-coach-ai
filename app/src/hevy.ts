// Importador del CSV de Hevy: parseo, mapeo al catálogo y series con ids deterministas (reimportar no duplica).
// Sin red: la pantalla decide qué subir y en qué lotes.
import type { Serie, TipoSerie } from './api'
import { textos } from './i18n'
import { lbAKg } from './unidades'

/** Nombre de Hevy (exercise_title) -> id del catálogo. Lo que no está aquí queda en sinEmparejar. */
export const HEVY_A_CATALOGO: Record<string, string> = {
  'Bicep Curl (Machine)': 'curl-biceps-maquina',
  'Butterfly (Pec Deck)': 'pec-deck',
  'Calf Extension (Machine)': 'elevacion-talones-maquina',
  'Chest Press (Machine)': 'press-pecho-maquina',
  'Hammer Curl (Dumbbell)': 'curl-martillo',
  'Hip Adduction (Machine)': 'aduccion-cadera',
  'Iso-Lateral Row (Machine)': 'remo-iso-lateral',
  'Lat Pulldown (Cable)': 'jalon-pecho',
  'Lat Pulldown (Machine)': 'jalon-pecho-maquina',
  'Lateral Raise (Dumbbell)': 'elevaciones-laterales',
  'Leg Extension (Machine)': 'extension-cuadriceps',
  'Leg Press (Machine)': 'prensa',
  'Leg Press Horizontal (Machine)': 'prensa-horizontal',
  'Lying Leg Curl (Machine)': 'curl-femoral',
  'Preacher Curl (Machine)': 'curl-predicador-maquina',
  'Seated Leg Curl (Machine)': 'curl-femoral-sentado',
  'Shoulder Press (Machine Plates)': 'press-hombro-maquina',
  'Triceps Extension (Machine)': 'extension-triceps-maquina',
  'Crunch (Machine)': 'crunch-maquina',
  'Iso-Lateral Chest Press (Machine)': 'press-pecho-iso-lateral',
  'Iso-Lateral Low Row': 'remo-bajo-iso-lateral',
  'Reverse Hyperextension': 'hiperextension-inversa',
  'Seated Dip Machine': 'fondos-maquina',
}

const tr = textos(
  { faltanColumnas: 'Falta la cabecera o columnas de Hevy.', faltaPeso: 'Falta la columna de peso.' },
  { faltanColumnas: 'The Hevy header or columns are missing.', faltaPeso: 'The weight column is missing.' },
)

/** El archivo no parece un export de Hevy (sin cabecera, columnas clave o columna de peso). */
export class FormatoHevyInvalido extends Error {}

/** RFC 4180: comillas, "" escapadas, comas y saltos de línea dentro de campos, BOM y \r\n. */
export function parsearCsv(texto: string): string[][] {
  const t = texto.charCodeAt(0) === 0xfeff ? texto.slice(1) : texto
  const filas: string[][] = []
  let fila: string[] = []
  let campo = ''
  let enComillas = false
  for (let i = 0; i < t.length; i++) {
    const c = t[i]
    if (enComillas) {
      if (c !== '"') campo += c
      else if (t[i + 1] === '"') { campo += '"'; i++ }
      else enComillas = false
    } else if (c === '"') enComillas = true
    else if (c === ',') { fila.push(campo); campo = '' }
    else if (c === '\n' || c === '\r') {
      if (c === '\r' && t[i + 1] === '\n') i++
      fila.push(campo)
      filas.push(fila)
      fila = []
      campo = ''
    } else campo += c
  }
  // Última fila sin salto final; un salto final no agrega una fila vacía.
  if (campo !== '' || fila.length > 0) { fila.push(campo); filas.push(fila) }
  return filas
}

const MESES: Record<string, number> = {
  jan: 1, feb: 2, mar: 3, apr: 4, may: 5, jun: 6, jul: 7, aug: 8, sep: 9, oct: 10, nov: 11, dec: 12,
}
const FECHA_HEVY = /^(\d{1,2}) ([A-Za-z]{3}) (\d{4}), (\d{1,2}):(\d{2})(?: ?([AaPp][Mm]))?$/

/** "7 Oct 2026, 20:33" o "7 Oct 2026, 8:33 PM" (hora local, sin zona). m es 1–12. null si no es una fecha real. */
export function fechaHevy(s: string): { y: number; m: number; d: number; hh: number; mm: number } | null {
  const r = FECHA_HEVY.exec(s.trim())
  if (!r) return null
  const m = MESES[r[2].toLowerCase()]
  const [y, d, mm] = [Number(r[3]), Number(r[1]), Number(r[5])]
  let hh = Number(r[4])
  if (r[6]) {
    if (hh < 1 || hh > 12) return null
    hh = (hh % 12) + (r[6].toLowerCase() === 'pm' ? 12 : 0)
  }
  if (!m || hh > 23 || mm > 59) return null
  // 31 Feb y similares: Date los desborda al mes siguiente.
  const f = new Date(y, m - 1, d)
  if (f.getFullYear() !== y || f.getMonth() !== m - 1 || f.getDate() !== d) return null
  return { y, m, d, hh, mm }
}

/** UUID con formato v5 a partir de SHA-256 (16 primeros bytes): mismo sub y partes, mismo id. */
export async function idHevy(sub: string, partes: string[]): Promise<string> {
  // JSON evita ambigüedades al concatenar (["a,b"] vs ["a","b"]).
  const datos = new TextEncoder().encode(JSON.stringify([sub, ...partes]))
  const b = new Uint8Array(await crypto.subtle.digest('SHA-256', datos)).slice(0, 16)
  b[6] = (b[6] & 0x0f) | 0x50
  b[8] = (b[8] & 0x3f) | 0x80
  const h = Array.from(b, (x) => x.toString(16).padStart(2, '0')).join('')
  return `${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20)}`
}

export interface AnalisisHevy {
  series: Serie[]
  entrenos: number
  ejercicios: number
  /** YYYY-MM-DD; '' si no hay series. */
  desde: string
  hasta: string
  unidad: 'lb' | 'kg'
  sinEmparejar: { nombre: string; series: number }[]
  omitidas: { reps: number; peso: number; fecha: number }
  /** Ejercicios (de un entreno) con notas: Hevy repite la nota en cada serie, se cuenta una vez. */
  notasDescartadas: number
  /** ids del catálogo con al menos una serie importada. */
  ejerciciosImportados: string[]
}

const COLUMNAS = ['title', 'start_time', 'exercise_title', 'exercise_notes', 'set_index', 'set_type', 'reps']
const TIPOS: Record<string, TipoSerie> = { warmup: 'calentamiento', dropset: 'drop', failure: 'fallo' }
const MAX_KG = 500
// Mismos límites que api/Validacion.cs: un lote es todo o nada, una fecha rechazada lo tumbaría entero.
const FECHA_MINIMA = '2000-01-01'
const MAX_TS_FUTURO_MS = 60 * 60_000
const MAX_RUTINA = 40
// Mismo criterio que el backend (control, uso privado, sin asignar, bidi, ancho cero salvo ZWNJ/ZWJ de los emojis) más sustitutos sueltos.
const PROHIBIDO = /[\p{Cc}\p{Co}\p{Cn}\p{Cs}​‎‏‪-‮⁦-⁩﻿]/u

/** title -> rutina: ≤40 caracteres sin partir emojis y ≤40 unidades UTF-16 (así cuenta el backend). */
function rutinaDe(title: string): string | undefined {
  const t = title.trim()
  if (!t || PROHIBIDO.test(t)) return undefined
  const cps = Array.from(t).slice(0, MAX_RUTINA)
  while (cps.join('').length > MAX_RUTINA) cps.pop()
  return cps.join('').trimEnd() || undefined
}

const pad = (n: number, w = 2) => String(n).padStart(w, '0')

export async function analizarHevy(texto: string, sub: string): Promise<AnalisisHevy> {
  const filas = parsearCsv(texto)
  const cab = (filas[0] ?? []).map((c) => c.trim())
  const col = (n: string) => cab.indexOf(n)
  if (COLUMNAS.some((c) => col(c) < 0)) throw new FormatoHevyInvalido(tr().faltanColumnas)
  const iKg = col('weight_kg')
  const iLb = col('weight_lbs')
  if (iKg < 0 && iLb < 0) throw new FormatoHevyInvalido(tr().faltaPeso)
  const unidad = iKg >= 0 ? 'kg' : 'lb'

  const series: Serie[] = []
  const ids = new Set<string>()
  const sinEmparejar = new Map<string, number>()
  const omitidas = { reps: 0, peso: 0, fecha: 0 }
  let notasDescartadas = 0
  // Por entreno (start_time crudo): filas vistas, último ejercicio y apariciones de cada ejercicio.
  const entrenos = new Map<string, { filas: number; ultimo: string; bloques: Map<string, number> }>()
  const entrenosConSeries = new Set<string>()
  // El backend compara con el día UTC (no el local) más uno, y ts con ahora + 1 h.
  const ahora = Date.now()
  const fechaMaxima = new Date(ahora + 86_400_000).toISOString().slice(0, 10)

  for (const fila of filas.slice(1)) {
    if (fila.length === 1 && fila[0].trim() === '') continue
    const v = (i: number) => (i >= 0 ? (fila[i] ?? '') : '')
    const inicio = v(col('start_time'))
    const nombre = v(col('exercise_title'))
    let e = entrenos.get(inicio)
    if (!e) entrenos.set(inicio, (e = { filas: 0, ultimo: '', bloques: new Map() }))
    const i = e.filas++
    // Hevy reinicia set_index si el ejercicio vuelve a aparecer: el bloque separa esas series.
    if (nombre !== e.ultimo) {
      e.bloques.set(nombre, (e.bloques.get(nombre) ?? 0) + 1)
      e.ultimo = nombre
      if (v(col('exercise_notes')).trim()) notasDescartadas++
    }
    const bloque = e.bloques.get(nombre)!

    const ejercicioId = HEVY_A_CATALOGO[nombre.trim()]
    if (!ejercicioId) {
      sinEmparejar.set(nombre, (sinEmparejar.get(nombre) ?? 0) + 1)
      continue
    }

    const f = fechaHevy(inicio)
    const fecha = f ? `${pad(f.y, 4)}-${pad(f.m)}-${pad(f.d)}` : ''
    const ts = f ? new Date(f.y, f.m - 1, f.d, f.hh, f.mm, i) : null
    if (!f || !ts || fecha < FECHA_MINIMA || fecha > fechaMaxima || ts.getTime() > ahora + MAX_TS_FUTURO_MS) {
      omitidas.fecha++
      continue
    }

    const repsTxt = v(col('reps')).trim()
    const reps = /^\d+$/.test(repsTxt) ? Number(repsTxt) : NaN
    if (!(reps >= 1 && reps <= 100)) { omitidas.reps++; continue }

    const pesoTxt = v(unidad === 'kg' ? iKg : iLb).trim()
    const n = pesoTxt === '' ? 0 : Number(pesoTxt)
    const pesoKg = unidad === 'kg' ? Math.round(n * 100) / 100 : lbAKg(n)
    if (!(pesoKg >= 0 && pesoKg <= MAX_KG)) { omitidas.peso++; continue }

    const id = await idHevy(sub, ['hevy', inicio, nombre, String(bloque), v(col('set_index'))])
    if (ids.has(id)) continue // fila duplicada en el CSV
    ids.add(id)
    entrenosConSeries.add(inicio)

    const tipoSerie = TIPOS[v(col('set_type')).trim().toLowerCase()]
    const rutina = rutinaDe(v(col('title')))
    series.push({
      id, ejercicioId, pesoKg, reps,
      fecha,
      // i segundos: orden estable dentro del entreno aunque todas las filas compartan la hora de inicio.
      ts: ts.toISOString(),
      ...(rutina ? { rutina } : {}),
      ...(tipoSerie ? { tipoSerie } : {}),
    })
  }

  const fechas = series.map((s) => s.fecha).sort()
  const ejerciciosImportados = [...new Set(series.map((s) => s.ejercicioId))].sort()
  return {
    series,
    entrenos: entrenosConSeries.size,
    ejercicios: ejerciciosImportados.length,
    desde: fechas[0] ?? '',
    hasta: fechas[fechas.length - 1] ?? '',
    unidad,
    sinEmparejar: [...sinEmparejar].map(([nombre, n]) => ({ nombre, series: n })).sort((a, b) => b.series - a.series),
    omitidas,
    notasDescartadas,
    ejerciciosImportados,
  }
}

/** Días de la importación donde el caché ya tiene series que no vienen de este CSV (registradas a mano u otro import). */
export function diasEnConflicto(a: AnalisisHevy, cache: Serie[]): string[] {
  const ids = new Set(a.series.map((s) => s.id))
  const dias = new Set(a.series.map((s) => s.fecha))
  return [...new Set(cache.filter((s) => dias.has(s.fecha) && !ids.has(s.id)).map((s) => s.fecha))].sort()
}

export function sinDias(a: AnalisisHevy, dias: string[]): Serie[] {
  const fuera = new Set(dias)
  return a.series.filter((s) => !fuera.has(s.fecha))
}

export function enLotes<T>(xs: T[], n = 10): T[][] {
  const lotes: T[][] = []
  for (let i = 0; i < xs.length; i += n) lotes.push(xs.slice(i, i + n))
  return lotes
}
