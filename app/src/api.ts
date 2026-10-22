import { estadoSesion, marcarSinSesion } from './auth'
import { idiomaApp } from './i18n'

export interface Ejercicio {
  id: string
  nombre: string
  nombreEn: string
  grupo: string
  equipo: 'maquina' | 'libre'
  /** pesoKg = lastre extra (0 si nada). */
  cargaCorporal?: boolean
}

/** Ausente = normal. El calentamiento no cuenta como trabajo (volumen, récords, series objetivo). */
export type TipoSerie = 'calentamiento' | 'normal' | 'fallo' | 'drop'

export interface Serie {
  id: string
  ejercicioId: string
  /** Mancuernas: peso de una mancuerna. */
  pesoKg: number
  reps: number
  /** Día local del usuario, YYYY-MM-DD. */
  fecha: string
  /** ISO 8601. */
  ts: string
  /** Nombre de la rutina con la que se hizo (≤40), si venía de una. */
  rutina?: string
  tipoSerie?: TipoSerie
}

/** Sin sesión (cookie vencida, ausente o de otra cuenta): la app vuelve a pedir login. */
export class NoAutenticado extends Error {}
export const EVENTO_NO_AUTENTICADO = 'gc:no-autenticado'

/** 429: transitorio, pero no volver a intentar antes de esperaMs. */
export class DemasiadasPeticiones extends Error {
  readonly esperaMs: number
  constructor(esperaMs: number) {
    super('demasiadas_peticiones')
    this.esperaMs = esperaMs
  }
}

/** 4xx permanente (validación, etc.): reintentar igual no sirve, hay que corregir o borrar. */
export class Rechazada extends Error {}

/** Clasificación para la cola de series: decide si se reenvía sola, espera o queda para el usuario. */
export type TipoFallo =
  | { tipo: 'noAutenticado' }
  | { tipo: 'limite'; esperaMs: number }
  | { tipo: 'rechazada'; motivo: string }
  | { tipo: 'transitorio' }

export function clasificarFallo(e: unknown): TipoFallo {
  if (e instanceof NoAutenticado) return { tipo: 'noAutenticado' }
  if (e instanceof DemasiadasPeticiones) return { tipo: 'limite', esperaMs: e.esperaMs }
  if (e instanceof Rechazada) return { tipo: 'rechazada', motivo: e.message }
  // Red caída, 5xx, respuesta rara: vale la pena reintentar.
  return { tipo: 'transitorio' }
}

// Un Retry-After enorme (o una fecha lejana) no debe congelar la cola durante horas.
const MAX_ESPERA_MS = 5 * 60_000

/** Retry-After en segundos o como fecha HTTP, con tope de 5 minutos; sin header o inválido, un minuto. */
export function esperaRetryAfter(valor: string | null, ahora = Date.now()): number {
  if (valor) {
    const segundos = Number(valor)
    if (Number.isFinite(segundos) && segundos >= 0) return Math.min(segundos * 1000, MAX_ESPERA_MS)
    // Solo si no es número: V8 lee '-5' como una fecha y daría reintento inmediato.
    const fecha = Number.isNaN(segundos) ? Date.parse(valor) : NaN
    if (Number.isFinite(fecha)) return Math.min(Math.max(0, fecha - ahora), MAX_ESPERA_MS)
  }
  return 60_000
}

async function motivoDe(res: Response): Promise<string> {
  try {
    const cuerpo = (await res.json()) as { error?: string; detalles?: { campo: string; mensaje: string }[] }
    if (cuerpo.detalles?.length) return cuerpo.detalles.map((d) => `${d.campo}: ${d.mensaje}`).join('; ')
    if (cuerpo.error) return cuerpo.error
  } catch {
    // cuerpo no JSON
  }
  return `HTTP ${res.status}`
}

/**
 * `sub`: la cuenta dueña de la operación. Si la sesión es de otra, no se envía; y va en X-Gc-Sub para que la API
 * la rechace si la cookie (compartida por todo el navegador) cambió de cuenta en otra pestaña.
 */
async function pedir(ruta: string, init: RequestInit = {}, sub?: string): Promise<Response> {
  const { estado, sub: actual, generacion } = estadoSesion()
  // Sin sesión no vale gastar la red: la app muestra Reconectar.
  if (estado === 'ausente') {
    window.dispatchEvent(new Event(EVENTO_NO_AUTENTICADO))
    throw new NoAutenticado()
  }
  if (sub !== undefined && actual !== null && actual !== sub) throw new NoAutenticado()
  // Sin timeout, una red colgada (portal cautivo) dejaría la cola de ese id abierta durante minutos.
  const res = await fetch(`/api/${ruta}`, {
    signal: AbortSignal.timeout(15_000),
    ...init,
    // La cookie HttpOnly es la credencial; X-Requested-With en todo (la API lo exige al escribir: CSRF).
    credentials: 'same-origin',
    headers: {
      ...init.headers,
      'X-Requested-With': 'gym-coach',
      ...(sub !== undefined ? { 'X-Gc-Sub': sub } : {}),
    },
  })
  if (res.status === 401) {
    // Un 401 tardío de antes de un login no debe tapar con el Login la sesión que entró después.
    if (marcarSinSesion(generacion)) window.dispatchEvent(new Event(EVENTO_NO_AUTENTICADO))
    throw new NoAutenticado()
  }
  if (res.status === 429) throw new DemasiadasPeticiones(esperaRetryAfter(res.headers.get('Retry-After')))
  return res
}

/** Lanza el error que corresponde a una respuesta no-ok (401 y 429 ya salieron en pedir). */
async function fallar(res: Response, permitir404 = false): Promise<void> {
  if (res.ok || (permitir404 && res.status === 404)) return
  if (res.status >= 400 && res.status < 500) throw new Rechazada(await motivoDe(res))
  throw new Error(`HTTP ${res.status}`)
}

async function json<T>(ruta: string, sub?: string): Promise<T> {
  const res = await pedir(ruta, {}, sub)
  await fallar(res)
  return res.json() as Promise<T>
}

export const listarEjercicios = () => json<Ejercicio[]>('ejercicios')

export const historial = (ejercicioId: string, limite = 20, sub?: string) =>
  json<Serie[]>(`ejercicios/${encodeURIComponent(ejercicioId)}/historial?limite=${limite}`, sub)

export const listarSeries = (desde: string, hasta: string, sub?: string) =>
  json<Serie[]>(`series?desde=${desde}&hasta=${hasta}`, sub)

/** Mejores marcas de un ejercicio según el servidor (sin calentamientos). e1rm null si ninguna serie lo admite. */
export interface RecordEjercicio {
  ejercicioId: string
  pesoMax: { pesoKg: number; reps: number; fecha: string }
  e1rm: { kg: number; pesoKg: number; reps: number; fecha: string } | null
  repsMax: { reps: number; pesoKg: number; fecha: string }
}

export const listarRecords = (sub?: string) => json<RecordEjercicio[]>('records', sub)

/** PUT idempotente: reintentar con la misma serie no duplica. */
export async function guardarSerie(s: Serie, sub?: string): Promise<Serie> {
  const { id, ...cuerpo } = s
  const res = await pedir(`series/${encodeURIComponent(id)}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(cuerpo),
  }, sub)
  await fallar(res)
  return res.json() as Promise<Serie>
}

export interface ResultadoImportacion {
  guardadas: number
  /** Ya estaban (la importación solo crea, nunca pisa). */
  existentes: number
  recordsActualizados: number
  recordsPendientes: number
}

/** Un lote de 1-25 series del importador; ids idempotentes, así que reintentar es seguro. */
export async function importarSeries(series: Serie[], sub: string): Promise<ResultadoImportacion> {
  const res = await pedir('importar/series', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ series }),
    signal: AbortSignal.timeout(30_000),
  }, sub)
  // 503 = no se escribió nada: se trata como el 429 (esperar y reintentar).
  if (res.status === 503) throw new DemasiadasPeticiones(esperaRetryAfter(res.headers.get('Retry-After')))
  await fallar(res)
  return res.json() as Promise<ResultadoImportacion>
}

/** 404 cuenta como borrado (p. ej. Deshacer de un PUT que nunca llegó). */
export async function borrarSerie(id: string, sub?: string): Promise<void> {
  await fallar(await pedir(`series/${encodeURIComponent(id)}`, { method: 'DELETE' }, sub), true)
}

/** Un ejercicio dentro de una rutina: el objetivo a cumplir. descansoS 0 = sin descanso. */
export interface EjercicioRutina {
  id: string
  series: number
  reps: number
  pesoKg: number | null
  descansoS: number
}

export interface Rutina {
  id: string
  nombre: string
  ejercicios: EjercicioRutina[]
  actualizada: string
}

export const listarRutinas = (sub?: string) => json<Rutina[]>('rutinas', sub)

/** PUT idempotente; el id va en la ruta, no en el cuerpo. */
export async function guardarRutina(r: Pick<Rutina, 'id' | 'nombre' | 'ejercicios'>, sub?: string): Promise<Rutina> {
  const { id, ...cuerpo } = r
  const res = await pedir(`rutinas/${encodeURIComponent(id)}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(cuerpo),
  }, sub)
  await fallar(res)
  return res.json() as Promise<Rutina>
}

/** 404 cuenta como borrada. */
export async function borrarRutina(id: string, sub?: string): Promise<void> {
  await fallar(await pedir(`rutinas/${encodeURIComponent(id)}`, { method: 'DELETE' }, sub), true)
}

/** Rutina que propone el coach: aún no está guardada; minutosEstimados lo calcula el servidor. */
export type RutinaPropuesta = Rutina & { minutosEstimados: number }

export type EquipoPreferido = 'maquina' | 'libre' | 'mixto'

export type Experiencia = 'nunca' | 'menos6m' | 'mas6m'

/** Lo que el coach recuerda del usuario (la hora de la asesoría es fija, no va aquí). */
export interface PerfilCoach {
  minutosDisponibles: number | null
  objetivo: string | null
  notas: string[]
  alturaCm: number | null
  pesoCorporalKg: number | null
  /** Ausente en el PUT = el servidor conserva lo guardado; null borra (el coach lo deduce del historial). */
  equipo?: EquipoPreferido | null
  /** Ausente en el PUT = el servidor conserva lo guardado; null borra. */
  experiencia?: Experiencia | null
  /** Ausente en el PUT = el servidor conserva lo guardado; null borra. */
  diasPorSemana?: number | null
}

/** Programa de 2 a 4 rutinas que propone el coach; aún no guardadas (ids del servidor). */
export interface ProgramaPropuesto {
  nombre: string
  rutinas: RutinaPropuesta[]
}

export interface MensajeParaCoach {
  rol: 'usuario' | 'coach'
  texto: string
}

export interface RespuestaCoach {
  respuesta: string
  herramientas: string[]
  rutina?: RutinaPropuesta
  programa?: ProgramaPropuesto | null
  /** Solo si el coach cambió algo del perfil en este turno. */
  perfil?: PerfilCoach
}

/** Un turno del coach. El modelo puede tardar varias rondas: timeout propio de 45 s en vez de los 15 s de siempre. */
export async function preguntarCoach(mensajes: MensajeParaCoach[], hoy: string, sub: string, offsetMin?: number): Promise<RespuestaCoach> {
  const res = await pedir('coach', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    // offsetMin: zona horaria del dispositivo, para el horario habitual del perfil de entrenamiento.
    // idioma: en qué responde el coach (el backend usa es si falta).
    body: JSON.stringify(offsetMin === undefined ? { hoy, mensajes, idioma: idiomaApp() } : { hoy, mensajes, offsetMin, idioma: idiomaApp() }),
    signal: AbortSignal.timeout(45_000),
  }, sub)
  // Sin settings del modelo el servidor responde 503 sin gastar cupo: distinto de "no disponible" transitorio.
  if (res.status === 503) {
    const cuerpo = (await res.json().catch(() => null)) as { error?: string } | null
    if (cuerpo?.error === 'coach_no_configurado') throw new Error('coach_no_configurado')
  }
  await fallar(res)
  return res.json() as Promise<RespuestaCoach>
}

export const leerPerfilCoach = (sub: string) => json<PerfilCoach>('coach/perfil', sub)

/** PUT del perfil completo. */
export async function guardarPerfilCoach(p: PerfilCoach, sub: string): Promise<PerfilCoach> {
  const res = await pedir('coach/perfil', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(p),
  }, sub)
  await fallar(res)
  return res.json() as Promise<PerfilCoach>
}

/** {pesoKg, reps, fecha}: la misma marca que pesoMax de RecordEjercicio. */
export interface MarcaSerie {
  pesoKg: number
  reps: number
  fecha: string
}

export type EstadoTendencia = 'progreso' | 'estancado' | 'baja' | 'sin_comparar'

export interface TendenciaEjercicio {
  ejercicioId: string
  nombre: string
  estado: EstadoTendencia
  reciente: MarcaSerie | null
  anterior: MarcaSerie | null
  cambioPct: number | null
}

export interface DivisionFrecuente {
  /** Orden alfabético, como mucho 3. */
  grupos: string[]
  veces: number
}

/** nombre es texto del usuario: se pinta como texto, nunca como HTML. */
export interface RutinaFrecuente {
  nombre: string
  veces: number
}

export interface HorarioHabitual {
  franja: 'manana' | 'mediodia' | 'tarde' | 'noche'
  /** "HH:mm", hora local redondeada a 30 min. */
  horaTipica: string
  sesiones: number
}

export interface EjercicioHistorial {
  ejercicioId: string
  nombre: string
  grupo: string
  equipo: 'maquina' | 'libre'
  cargaCorporal: boolean
  sesiones: number
  series: number
  primeraFecha: string
  ultimaFecha: string
  ultimaSerie: MarcaSerie | null
  mejorSerie: MarcaSerie | null
  e1rmMaxKg: number | null
}

/** Calculado por el servidor con las series reales (sin calentamientos). Fechas YYYY-MM-DD; calculadoEl ISO. */
export interface PerfilEntrenamiento {
  version: number
  calculadoEl: string
  /** Día con el que se calculó. */
  hoy: string
  seriesAnalizadas: number
  /** Llegó al tope de series: faltan las más viejas. */
  truncado: boolean
  primerEntreno: string | null
  ultimoEntreno: string | null
  diasEntrenados: number
  /** Últimas 8 semanas hasta el último entreno; null sin series que cuenten. */
  habitos: {
    desde: string
    hasta: string
    dias: number
    diasPorSemana: number
    division: DivisionFrecuente[]
    rutinasFrecuentes: RutinaFrecuente[]
    horario: HorarioHabitual | null
    duracionTipicaMin: number | null
    duracionSesionesMedidas: number
    seriesPorSesion: number
  } | null
  equipo: { inferido: EquipoPreferido | null; pctMaquina: number | null; seriesContadas: number }
  /** Ids de catálogo, hasta 6. */
  principales: string[]
  tendencias: TendenciaEjercicio[]
  /** Todos los del historial: sesiones desc, series desc, ultimaFecha desc. */
  ejercicios: EjercicioHistorial[]
}

/** Lo que ve el coach en el prompt, tal cual. */
export interface ParaCoach {
  calculadoEl: string
  seriesAnalizadas: number
  truncado: boolean
  primerEntreno: string | null
  ultimoEntreno: string | null
  diasDesdeUltimo: number | null
  diasEntrenados: number
  historialSuficiente: boolean
  habitos: {
    diasPorSemana: number
    division: DivisionFrecuente[]
    rutinasFrecuentes: RutinaFrecuente[]
    horario: HorarioHabitual | null
    duracionTipicaMin: number | null
    seriesPorSesion: number
  } | null
  equipoPreferido: EquipoPreferido | null
  /** perfil = lo eligió el usuario; historial = inferido de sus series. */
  equipoOrigen: 'perfil' | 'historial' | null
  pctSeriesMaquina: number | null
  principales: { ejercicioId: string; nombre: string; sesiones: number }[]
  tendencias: TendenciaEjercicio[]
}

export interface RespuestaPerfilEntrenamiento {
  perfil: PerfilEntrenamiento
  paraCoach: ParaCoach
}

/** offsetMin = -new Date().getTimezoneOffset() (el horario habitual sale de ahí). */
export const leerPerfilEntrenamiento = (hoy: string, offsetMin: number, sub: string) =>
  json<RespuestaPerfilEntrenamiento>(`perfil-entrenamiento?hoy=${hoy}&offsetMin=${offsetMin}`, sub)

/** Una generación del plan; rutina null si el coach no propuso una. creado ISO. */
export interface VariacionPlan {
  texto: string
  herramientas: string[]
  rutina: RutinaPropuesta | null
  creado: string
}

/** Plan del día: uno por usuario y día, el mismo en todos los dispositivos. */
export interface PlanDia {
  fecha: string
  /** La última variación; null si aún no hay plan. */
  actual: VariacionPlan | null
  /** Variaciones hechas y las que quedan (máximo 3 al día). */
  numero: number
  restantes: number
  /** Otro dispositivo o petición lo está generando: volver a pedir el GET en unos segundos. */
  generando: boolean
}

export const leerPlanDia = (hoy: string, sub: string) => json<PlanDia>(`coach/plan-dia?hoy=${hoy}`, sub)

/**
 * Pide el plan de hoy; con plan y sin otraOpcion lo devuelve sin llamar al modelo. Es un turno del coach: 45 s.
 * Errores: Rechazada('tope_variaciones') con 409, Error('coach_no_configurado'), DemasiadasPeticiones con 429.
 */
export async function pedirPlanDia(
  p: { hoy: string; offsetMin: number; otraOpcion?: boolean; numero?: number },
  sub: string,
): Promise<PlanDia> {
  // idioma: si el plan de hoy está en otro, el backend decide si lo regenera (máx. 1 vez al día).
  const cuerpo: Record<string, unknown> = { hoy: p.hoy, offsetMin: p.offsetMin, otraOpcion: p.otraOpcion ?? false, idioma: idiomaApp() }
  if (p.numero !== undefined) cuerpo.numero = p.numero
  const res = await pedir('coach/plan-dia', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(cuerpo),
    signal: AbortSignal.timeout(45_000),
  }, sub)
  if (res.status === 503) {
    const cuerpo = (await res.json().catch(() => null)) as { error?: string } | null
    if (cuerpo?.error === 'coach_no_configurado') throw new Error('coach_no_configurado')
  }
  await fallar(res)
  return res.json() as Promise<PlanDia>
}
