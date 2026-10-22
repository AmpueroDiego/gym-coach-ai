// Conversación con el coach: vive solo en este dispositivo, una por cuenta, y caduca al cambiar el día.
import { DemasiadasPeticiones, NoAutenticado, Rechazada, type MensajeParaCoach, type PlanDia, type ProgramaPropuesto, type RutinaPropuesta } from './api'
import { textos, type Idioma } from './i18n'
import { esEjercicioRutina } from './plan'
import { MAX_EJERCICIOS_RUTINA as MAX_EJERCICIOS, MAX_NOMBRE_RUTINA } from './presentacion'

export interface MensajeCoach {
  rol: 'usuario' | 'coach'
  texto: string
  /** ISO 8601; también identifica el mensaje dentro de la conversación. */
  ts: string
  /** Herramientas que consultó el coach para esta respuesta. */
  herramientas?: string[]
  /** Mensaje del usuario que no obtuvo respuesta: texto para mostrar junto a "Reintentar". */
  error?: string
  rutina?: RutinaPropuesta
  programa?: ProgramaPropuesto
}

export interface EstadoCoach {
  fecha: string
  mensajes: MensajeCoach[]
  /** Fecha (YYYY-MM-DD) en que se vio el plan del día en este dispositivo (solo apaga el punto de aviso). */
  asesoria?: string
  /** Idioma en que se pidió por POST el plan de `fecha`; sin dato = es (el default del backend). */
  idiomaPlan?: Idioma
}

export const claveCoach = (sub: string) => 'gc.coach:' + sub

type Almacen = Pick<Storage, 'getItem' | 'setItem'> | null

/** Hora local fija de la asesoría diaria. */
export const HORA_ASESORIA = '07:30'
const t = textos(
  {
    chipPlan: 'Tu entreno de hoy',
    banca: '¿Cómo voy en press de banca?',
    descuido: '¿Estoy descuidando algún grupo muscular?',
    tope: 'Ya usaste las 3 opciones de hoy.',
    noConfigurado: 'El coach aún no está disponible.',
    reconectaPlan: 'Reconecta para ver tu entreno de hoy.',
    falloPlan: 'No se pudo preparar tu entreno de hoy.',
    reconectaChat: 'Reconecta para hablar con el coach.',
    topeMensajes: 'Llegaste al tope de mensajes de hoy. Vuelve más tarde.',
    espera: (s: number) => `Espera ${s} s.`,
    noRespondio: 'El coach no respondió. Reintenta.',
    pensando: 'Pensando…',
    revisando: 'Revisando tus entrenos…',
    armando: 'Armando la respuesta…',
    herramientas: {
      ver_entrenos_recientes: 'entrenos recientes',
      progreso: 'progreso',
      volumen_por_grupo: 'volumen por grupo',
      recordar: 'perfil',
      proponer_rutina: 'rutina',
      proponer_programa: 'programa',
      mis_ejercicios: 'tus ejercicios',
    } as Record<string, string>,
  },
  {
    chipPlan: "Today's workout",
    banca: 'How is my bench press going?',
    descuido: 'Am I neglecting any muscle group?',
    tope: "You've used today's 3 options.",
    noConfigurado: 'The coach is not available yet.',
    reconectaPlan: "Reconnect to see today's workout.",
    falloPlan: "Couldn't prepare today's workout.",
    reconectaChat: 'Reconnect to talk to the coach.',
    topeMensajes: "You've reached today's message limit. Come back later.",
    espera: (s) => `Wait ${s} s.`,
    noRespondio: "The coach didn't respond. Try again.",
    pensando: 'Thinking…',
    revisando: 'Checking your workouts…',
    armando: 'Putting the answer together…',
    herramientas: {
      ver_entrenos_recientes: 'recent workouts',
      progreso: 'progress',
      volumen_por_grupo: 'volume by group',
      recordar: 'profile',
      proponer_rutina: 'routine',
      proponer_programa: 'program',
      mis_ejercicios: 'your exercises',
    },
  },
)

/** Por id y no por texto: el texto cambia con el idioma. 'plan' no manda un mensaje de chat, pide el plan del día. */
export const SUGERENCIAS: { id: 'plan' | 'banca' | 'descuido'; texto: () => string }[] = [
  { id: 'plan', texto: () => t().chipPlan },
  { id: 'banca', texto: () => t().banca },
  { id: 'descuido', texto: () => t().descuido },
]
/** Tope del backend para el texto del usuario. */
export const MAX_TEXTO = 1000
const MAX_TEXTO_COACH = 2000
const MAX_GUARDADOS = 40
const MAX_ENVIADOS = 10
const MAX_HERRAMIENTAS = 20
const MAX_ID = 100

const esObjeto = (x: unknown): x is Record<string, unknown> => typeof x === 'object' && x !== null && !Array.isArray(x)
const esFecha = (x: unknown): x is string => typeof x === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(x)
const texto = (x: unknown, max: number): x is string => typeof x === 'string' && x.length <= max
const numero = (x: unknown): x is number => typeof x === 'number' && Number.isFinite(x)

/** Rutina propuesta con la forma que la tarjeta necesita; cualquier otra cosa da null. */
export function rutinaValida(x: unknown): RutinaPropuesta | null {
  if (!esObjeto(x) || !texto(x.id, MAX_ID) || !x.id || !texto(x.nombre, MAX_NOMBRE_RUTINA) || !x.nombre.trim()) return null
  if (!numero(x.minutosEstimados) || x.minutosEstimados < 0) return null
  if (!Array.isArray(x.ejercicios) || x.ejercicios.length < 1 || x.ejercicios.length > MAX_EJERCICIOS) return null
  // Mismos rangos que el plan del día y sin ids repetidos, como valida el backend.
  const ejercicios: RutinaPropuesta['ejercicios'] = []
  const ids = new Set<string>()
  for (const e of x.ejercicios as unknown[]) {
    if (!esEjercicioRutina(e) || !e.id || e.id.length > MAX_ID || ids.has(e.id)) return null
    ids.add(e.id)
    ejercicios.push({ id: e.id, series: e.series, reps: e.reps, pesoKg: e.pesoKg, descansoS: e.descansoS })
  }
  return {
    id: x.id,
    nombre: x.nombre,
    ejercicios,
    actualizada: typeof x.actualizada === 'string' ? x.actualizada : '',
    minutosEstimados: x.minutosEstimados,
  }
}

/** Programa propuesto: 2 a 4 rutinas válidas con ids distintos; cualquier otra cosa da null. */
export function programaValido(x: unknown): ProgramaPropuesto | null {
  if (!esObjeto(x) || !texto(x.nombre, MAX_NOMBRE_RUTINA) || !x.nombre.trim()) return null
  if (!Array.isArray(x.rutinas) || x.rutinas.length < 2 || x.rutinas.length > 4) return null
  const rutinas: RutinaPropuesta[] = []
  const ids = new Set<string>()
  for (const item of x.rutinas as unknown[]) {
    const r = rutinaValida(item)
    if (!r || ids.has(r.id)) return null
    ids.add(r.id)
    rutinas.push(r)
  }
  return { nombre: x.nombre, rutinas }
}

const MAX_TEXTO_PLAN = 4000

/**
 * Plan del día con la forma que el bloque necesita; cualquier otra cosa da null. Una rutina rota
 * no tira el plan: se pierde solo la tarjeta.
 */
export function planValido(x: unknown): PlanDia | null {
  if (!esObjeto(x) || !esFecha(x.fecha) || typeof x.generando !== 'boolean') return null
  if (!Number.isInteger(x.numero) || !Number.isInteger(x.restantes)) return null
  const numero = x.numero as number
  const restantes = x.restantes as number
  if (numero < 0 || numero > 3 || restantes < 0 || restantes > 3) return null
  if (x.actual === null) return { fecha: x.fecha, actual: null, numero, restantes, generando: x.generando }
  const a = x.actual
  if (!esObjeto(a) || !texto(a.texto, MAX_TEXTO_PLAN) || !a.texto.trim() || !texto(a.creado, 40)) return null
  const rutina = a.rutina === null || a.rutina === undefined ? null : rutinaValida(a.rutina)
  return {
    fecha: x.fecha,
    actual: { texto: a.texto, herramientas: herramientasValidas(a.herramientas), rutina, creado: a.creado },
    numero,
    restantes,
    generando: x.generando,
  }
}

/** Sondeo del plan que genera otro dispositivo: cada 4 s, hasta 60 s. */
export const SONDEO_MS = 4000
export const SONDEO_MAX_MS = 60_000

export interface EntradaAutoPlan {
  /** App: ya pasó la hora de la asesoría, hoy no se vio el plan y la pestaña está a la vista. */
  auto: boolean
  conSesion: boolean
  /** El GET inicial terminó bien: sin él no se sabe si ya hay plan. */
  leido: boolean
  hayPlan: boolean
  generando: boolean
  pidiendo: boolean
  yaPedido: boolean
}

/** POST automático del plan: solo uno por cuenta y día, y nunca si el GET falló. */
export const debePedirPlanSolo = (e: EntradaAutoPlan): boolean =>
  e.auto && e.conSesion && e.leido && !e.hayPlan && !e.generando && !e.pidiendo && !e.yaPedido

export interface EntradaRepedirIdioma {
  conSesion: boolean
  leido: boolean
  hayPlan: boolean
  generando: boolean
  pidiendo: boolean
  /** Idioma en que se pidió el plan de hoy; sin dato = es (el default del backend). */
  idiomaPlan: Idioma | undefined
  idioma: Idioma
}

/**
 * POST del plan al cambiar de idioma: solo si hay plan pedido en otro. Quien pide fija idiomaPlan = idioma
 * antes de enviar, así que un fallo o el plan viejo de vuelta no lo repiten (uno por cambio).
 */
export const debeRepedirPorIdioma = (e: EntradaRepedirIdioma): boolean =>
  e.conSesion && e.leido && e.hayPlan && !e.generando && !e.pidiendo && (e.idiomaPlan ?? 'es') !== e.idioma

/**
 * El resultado de un GET solo vale si sigue siendo el último GET, no hubo un POST desde que salió
 * (el POST es más nuevo) y la cuenta/día es la misma.
 */
export const getVigente = (
  inicio: { get: number; post: number; clave: string },
  ahora: { get: number; post: number; clave: string },
): boolean => inicio.get === ahora.get && inicio.post === ahora.post && inicio.clave === ahora.clave

/** Mensaje de un fallo al leer o pedir el plan; `reintentable` false cuando repetir no cambia nada. */
export function falloPlan(e: unknown): { texto: string; reintentable: boolean } {
  if (e instanceof Rechazada && e.message === 'tope_variaciones') return { texto: t().tope, reintentable: false }
  if (e instanceof Error && e.message === 'coach_no_configurado') return { texto: t().noConfigurado, reintentable: false }
  if (e instanceof NoAutenticado) return { texto: t().reconectaPlan, reintentable: false }
  if (e instanceof DemasiadasPeticiones) return { texto: textoError(e), reintentable: e.esperaMs <= 60_000 }
  return { texto: t().falloPlan, reintentable: true }
}

/** Texto del fallo genérico del plan (respuesta con forma rara). */
export const textoFalloPlan = (): string => t().falloPlan

/** Nombres de herramientas: solo strings cortos y pocos (vienen del servidor o del almacenamiento). */
export const herramientasValidas = (x: unknown): string[] =>
  Array.isArray(x) ? x.filter((h): h is string => texto(h, 50) && h.length > 0).slice(0, MAX_HERRAMIENTAS) : []

/** Nombre legible; hasOwn para que 'constructor' o '__proto__' no saquen algo del prototipo. */
export const etiquetaHerramienta = (h: string): string => {
  const nombres = t().herramientas
  return Object.hasOwn(nombres, h) ? nombres[h] : h.slice(0, 30)
}

function leerMensaje(x: unknown): MensajeCoach | null {
  if (!esObjeto(x) || (x.rol !== 'usuario' && x.rol !== 'coach') || !texto(x.texto, 4000) || !texto(x.ts, 40)) return null
  const m: MensajeCoach = { rol: x.rol, texto: x.texto, ts: x.ts }
  if (Array.isArray(x.herramientas)) m.herramientas = herramientasValidas(x.herramientas)
  if (texto(x.error, 200) && x.error) m.error = x.error
  // Una rutina rota no tira el mensaje: se pierde solo la tarjeta.
  const r = x.rutina === undefined ? null : rutinaValida(x.rutina)
  if (r) m.rutina = r
  // Igual: un programa roto no tira el mensaje.
  const g = x.programa == null ? null : programaValido(x.programa)
  if (g) m.programa = g
  return m
}

/** Conversación de hoy; la de otro día se descarta, pero la fecha de la última asesoría se conserva. */
export function leerConversacion(a: Almacen, sub: string, hoy: string): EstadoCoach {
  try {
    const c = JSON.parse(a?.getItem(claveCoach(sub)) ?? 'null') as unknown
    if (!esObjeto(c)) return { fecha: hoy, mensajes: [] }
    const estado: EstadoCoach = { fecha: hoy, mensajes: [] }
    if (esFecha(c.asesoria)) estado.asesoria = c.asesoria
    if (c.fecha === hoy && (c.idiomaPlan === 'es' || c.idiomaPlan === 'en')) estado.idiomaPlan = c.idiomaPlan
    if (c.fecha === hoy && Array.isArray(c.mensajes)) {
      for (const x of c.mensajes.slice(-MAX_GUARDADOS)) {
        const m = leerMensaje(x)
        if (m) estado.mensajes.push(m)
      }
    }
    return estado
  } catch {
    return { fecha: hoy, mensajes: [] }
  }
}

export function guardarConversacion(a: Almacen, sub: string, estado: EstadoCoach): void {
  try {
    // La marca de asesoría puede haberse escrito aparte (marcarAsesoria): nunca retrocede.
    const previa = leerConversacion(a, sub, estado.fecha).asesoria
    const asesoria = [estado.asesoria, previa].filter(esFecha).sort().pop()
    const guardado: EstadoCoach = { fecha: estado.fecha, mensajes: estado.mensajes.slice(-MAX_GUARDADOS) }
    if (asesoria) guardado.asesoria = asesoria
    if (estado.idiomaPlan) guardado.idiomaPlan = estado.idiomaPlan
    a?.setItem(claveCoach(sub), JSON.stringify(guardado))
  } catch {
    // Sin almacenamiento: la conversación dura solo la sesión.
  }
}

/** Deja constancia de que hoy se vio el plan (apaga el punto de aviso); se marca al mostrarlo, no al pedirlo. */
export function marcarAsesoria(a: Almacen, sub: string, hoy: string): void {
  guardarConversacion(a, sub, { ...leerConversacion(a, sub, hoy), asesoria: hoy })
}

/** Ya pasó la hora de la asesoría (hora local) y hoy no se hizo. */
export function tocaAsesoria(ahora: Date, hoy: string, ultima: string | null | undefined): boolean {
  if (ultima === hoy) return false
  const [h, m] = HORA_ASESORIA.split(':').map(Number)
  return ahora.getHours() * 60 + ahora.getMinutes() >= h * 60 + m
}

// Los mismos caracteres que rechaza el backend (salvo \n \r \t): el modelo puede devolver un ancho cero
// y, reenviado en el historial, dejaría la conversación atascada en 400.
const PROHIBIDOS = /[\p{Cc}\p{Co}\p{Cn}\p{Cs}​‎‏‪-‮⁦-⁩﻿]/gu
export const limpiarTexto = (t: string) => t.replace(PROHIBIDOS, (c) => ('\n\r\t'.includes(c) ? c : ''))

/** Historial para el backend: últimos 10 sin los fallidos, textos dentro de los topes y terminando en el usuario. */
export function paraEnviar(mensajes: MensajeCoach[]): MensajeParaCoach[] {
  const salida: MensajeParaCoach[] = []
  for (const m of mensajes) {
    if (m.error) continue
    // Limpiar después de cortar: el corte puede partir un emoji y dejar un surrogate suelto.
    const t = limpiarTexto(m.texto.trim().slice(0, m.rol === 'usuario' ? MAX_TEXTO : MAX_TEXTO_COACH)).trim()
    if (t) salida.push({ rol: m.rol, texto: t })
  }
  while (salida.length > 0 && salida[salida.length - 1].rol !== 'usuario') salida.pop()
  return salida.slice(-MAX_ENVIADOS)
}

export function textoError(e: unknown): string {
  if (e instanceof NoAutenticado) return t().reconectaChat
  if (e instanceof DemasiadasPeticiones) {
    // El cupo por minuto pide esperar ≤ 60 s; más es el tope del día.
    if (e.esperaMs > 60_000) return t().topeMensajes
    return t().espera(Math.max(1, Math.ceil(e.esperaMs / 1000)))
  }
  if (e instanceof Error && e.message === 'coach_no_configurado') return t().noConfigurado
  return t().noRespondio
}

/** Texto del indicador mientras el coach responde; cambia con la espera para que no parezca colgado. */
export function textoEspera(segundos: number): string {
  if (segundos < 3) return t().pensando
  if (segundos < 7) return t().revisando
  return t().armando
}
