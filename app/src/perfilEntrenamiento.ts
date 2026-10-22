// Perfil de entrenamiento que calcula el servidor: validación defensiva de la respuesta y textos para pintarla.
import { useEffect, useState } from 'react'
import {
  clasificarFallo,
  leerPerfilEntrenamiento,
  type DivisionFrecuente,
  type EjercicioHistorial,
  type EquipoPreferido,
  type EstadoTendencia,
  type HorarioHabitual,
  type MarcaSerie,
  type ParaCoach,
  type PerfilEntrenamiento,
  type RespuestaPerfilEntrenamiento,
  type TendenciaEjercicio,
} from './api'
import { etiquetaGrupo } from './ejercicios'
import { textos } from './i18n'
import { fechaLocal } from './prefill'
import { etiquetaFecha, mensajeFallo } from './presentacion'
import { pesoEnTexto, type Unidad } from './unidades'

const t = textos(
  {
    frecuencia: (n: number) => `${n} ${n === 1 ? 'día' : 'días'} por semana`,
    sinGrupo: 'Sin grupo',
    veces: (n: number) => `${n} ${n === 1 ? 'vez' : 'veces'}`,
    franja: { manana: 'Mañana', mediodia: 'Mediodía', tarde: 'Tarde', noche: 'Noche' } as Record<HorarioHabitual['franja'], string>,
    horario: (franja: string, hora: string, n: number) => `${franja}, hacia las ${hora} (${n} ${n === 1 ? 'sesión' : 'sesiones'})`,
    estado: { progreso: 'Progreso', estancado: 'Estancado', baja: 'A la baja', sin_comparar: 'Sin comparar' } as Record<EstadoTendencia, string>,
    reps: (n: number) => `${n} reps`,
    equipo: { maquina: 'Máquinas', libre: 'Peso libre', mixto: 'Mixto' } as Record<EquipoPreferido, string>,
    sesionExpirada: 'Tu sesión expiró: vuelve a entrar.',
  },
  {
    frecuencia: (n) => `${n} ${n === 1 ? 'day' : 'days'} per week`,
    sinGrupo: 'No group',
    veces: (n) => `${n} ${n === 1 ? 'time' : 'times'}`,
    franja: { manana: 'Morning', mediodia: 'Midday', tarde: 'Afternoon', noche: 'Evening' },
    horario: (franja, hora, n) => `${franja}, around ${hora} (${n} ${n === 1 ? 'session' : 'sessions'})`,
    estado: { progreso: 'Progressing', estancado: 'Stalled', baja: 'Declining', sin_comparar: 'Not comparable' },
    reps: (n) => `${n} ${n === 1 ? 'rep' : 'reps'}`,
    equipo: { maquina: 'Machines', libre: 'Free weights', mixto: 'Mixed' },
    sesionExpirada: 'Your session expired: sign in again.',
  },
)

// Topes de la PWA: el servidor manda menos, esto solo acota una respuesta rara.
const MAX_EJERCICIOS = 500
const MAX_LISTA = 10
const MAX_TEXTO = 200
const FRANJAS = ['manana', 'mediodia', 'tarde', 'noche']
const ESTADOS = ['progreso', 'estancado', 'baja', 'sin_comparar']
const EQUIPOS = ['maquina', 'libre', 'mixto']

const esObjeto = (x: unknown): x is Record<string, unknown> => typeof x === 'object' && x !== null && !Array.isArray(x)
const esFecha = (x: unknown): x is string => typeof x === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(x)
const texto = (x: unknown, max = MAX_TEXTO): x is string => typeof x === 'string' && x.length <= max
const numero = (x: unknown): x is number => typeof x === 'number' && Number.isFinite(x)
const entero = (x: unknown): x is number => numero(x) && Number.isInteger(x) && x >= 0

/** Un campo roto tira la validación entera: el que llama la convierte en null. */
function exigir(c: unknown): asserts c {
  if (!c) throw new Error('forma inválida')
}

const nulable = <T>(x: unknown, f: (v: unknown) => T): T | null => (x === null ? null : f(x))
const lista = (x: unknown, max: number): unknown[] => {
  exigir(Array.isArray(x))
  return x.slice(0, max)
}

function marca(x: unknown): MarcaSerie {
  exigir(esObjeto(x) && numero(x.pesoKg) && x.pesoKg >= 0 && entero(x.reps) && esFecha(x.fecha))
  return { pesoKg: x.pesoKg, reps: x.reps, fecha: x.fecha }
}

function tendencia(x: unknown): TendenciaEjercicio {
  exigir(esObjeto(x) && texto(x.ejercicioId, 100) && texto(x.nombre) && ESTADOS.includes(x.estado as string))
  exigir(x.cambioPct === null || numero(x.cambioPct))
  return {
    ejercicioId: x.ejercicioId,
    nombre: x.nombre,
    estado: x.estado as EstadoTendencia,
    reciente: nulable(x.reciente, marca),
    anterior: nulable(x.anterior, marca),
    cambioPct: x.cambioPct,
  }
}

function division(x: unknown): DivisionFrecuente {
  exigir(esObjeto(x) && entero(x.veces))
  const grupos = lista(x.grupos, 3)
  exigir(grupos.every((g) => texto(g, 50)))
  return { grupos: grupos as string[], veces: x.veces }
}

function rutinaFrecuente(x: unknown) {
  exigir(esObjeto(x) && texto(x.nombre) && entero(x.veces))
  return { nombre: x.nombre, veces: x.veces }
}

function horario(x: unknown): HorarioHabitual {
  exigir(esObjeto(x) && FRANJAS.includes(x.franja as string) && texto(x.horaTipica, 5) && /^\d{2}:\d{2}$/.test(x.horaTipica) && entero(x.sesiones))
  return { franja: x.franja as HorarioHabitual['franja'], horaTipica: x.horaTipica, sesiones: x.sesiones }
}

function ejercicio(x: unknown): EjercicioHistorial {
  exigir(esObjeto(x) && texto(x.ejercicioId, 100) && x.ejercicioId && texto(x.nombre) && texto(x.grupo, 50))
  exigir((x.equipo === 'maquina' || x.equipo === 'libre') && typeof x.cargaCorporal === 'boolean')
  exigir(entero(x.sesiones) && entero(x.series) && esFecha(x.primeraFecha) && esFecha(x.ultimaFecha))
  exigir(x.e1rmMaxKg === null || numero(x.e1rmMaxKg))
  return {
    ejercicioId: x.ejercicioId,
    nombre: x.nombre,
    grupo: x.grupo,
    equipo: x.equipo,
    cargaCorporal: x.cargaCorporal,
    sesiones: x.sesiones,
    series: x.series,
    primeraFecha: x.primeraFecha,
    ultimaFecha: x.ultimaFecha,
    ultimaSerie: nulable(x.ultimaSerie, marca),
    mejorSerie: nulable(x.mejorSerie, marca),
    e1rmMaxKg: x.e1rmMaxKg,
  }
}

const equipoPreferido = (x: unknown): EquipoPreferido | null => {
  exigir(x === null || EQUIPOS.includes(x as string))
  return x as EquipoPreferido | null
}

function leerPerfil(x: unknown): PerfilEntrenamiento {
  exigir(esObjeto(x) && entero(x.version) && texto(x.calculadoEl, 40) && esFecha(x.hoy))
  exigir(entero(x.seriesAnalizadas) && typeof x.truncado === 'boolean' && entero(x.diasEntrenados))
  exigir(esObjeto(x.equipo) && entero(x.equipo.seriesContadas))
  exigir(x.equipo.pctMaquina === null || numero(x.equipo.pctMaquina))
  const h = nulable(x.habitos, (v) => {
    exigir(esObjeto(v) && esFecha(v.desde) && esFecha(v.hasta) && entero(v.dias) && numero(v.diasPorSemana))
    exigir((v.duracionTipicaMin === null || numero(v.duracionTipicaMin)) && entero(v.duracionSesionesMedidas) && numero(v.seriesPorSesion))
    return {
      desde: v.desde,
      hasta: v.hasta,
      dias: v.dias,
      diasPorSemana: v.diasPorSemana,
      division: lista(v.division, 4).map(division),
      rutinasFrecuentes: lista(v.rutinasFrecuentes, 5).map(rutinaFrecuente),
      horario: nulable(v.horario, horario),
      duracionTipicaMin: v.duracionTipicaMin as number | null,
      duracionSesionesMedidas: v.duracionSesionesMedidas,
      seriesPorSesion: v.seriesPorSesion,
    }
  })
  const principales = lista(x.principales, 6)
  exigir(principales.every((p) => texto(p, 100)))
  exigir(x.primerEntreno === null || esFecha(x.primerEntreno))
  exigir(x.ultimoEntreno === null || esFecha(x.ultimoEntreno))
  return {
    version: x.version,
    calculadoEl: x.calculadoEl,
    hoy: x.hoy,
    seriesAnalizadas: x.seriesAnalizadas,
    truncado: x.truncado,
    primerEntreno: x.primerEntreno,
    ultimoEntreno: x.ultimoEntreno,
    diasEntrenados: x.diasEntrenados,
    habitos: h,
    equipo: { inferido: equipoPreferido(x.equipo.inferido), pctMaquina: x.equipo.pctMaquina, seriesContadas: x.equipo.seriesContadas },
    principales: principales as string[],
    tendencias: lista(x.tendencias, MAX_LISTA).map(tendencia),
    ejercicios: lista(x.ejercicios, MAX_EJERCICIOS).map(ejercicio),
  }
}

function leerParaCoach(x: unknown): ParaCoach {
  exigir(esObjeto(x) && texto(x.calculadoEl, 40) && entero(x.seriesAnalizadas) && typeof x.truncado === 'boolean')
  exigir(x.primerEntreno === null || esFecha(x.primerEntreno))
  exigir(x.ultimoEntreno === null || esFecha(x.ultimoEntreno))
  exigir(x.diasDesdeUltimo === null || numero(x.diasDesdeUltimo))
  exigir(entero(x.diasEntrenados) && typeof x.historialSuficiente === 'boolean')
  exigir(x.equipoOrigen === null || x.equipoOrigen === 'perfil' || x.equipoOrigen === 'historial')
  exigir(x.pctSeriesMaquina === null || numero(x.pctSeriesMaquina))
  const h = nulable(x.habitos, (v) => {
    exigir(esObjeto(v) && numero(v.diasPorSemana) && numero(v.seriesPorSesion))
    exigir(v.duracionTipicaMin === null || numero(v.duracionTipicaMin))
    return {
      diasPorSemana: v.diasPorSemana,
      division: lista(v.division, 4).map(division),
      rutinasFrecuentes: lista(v.rutinasFrecuentes, 5).map(rutinaFrecuente),
      horario: nulable(v.horario, horario),
      duracionTipicaMin: v.duracionTipicaMin,
      seriesPorSesion: v.seriesPorSesion,
    }
  })
  const principales = lista(x.principales, 6).map((p) => {
    exigir(esObjeto(p) && texto(p.ejercicioId, 100) && texto(p.nombre) && entero(p.sesiones))
    return { ejercicioId: p.ejercicioId, nombre: p.nombre, sesiones: p.sesiones }
  })
  return {
    calculadoEl: x.calculadoEl,
    seriesAnalizadas: x.seriesAnalizadas,
    truncado: x.truncado,
    primerEntreno: x.primerEntreno,
    ultimoEntreno: x.ultimoEntreno,
    diasDesdeUltimo: x.diasDesdeUltimo,
    diasEntrenados: x.diasEntrenados,
    historialSuficiente: x.historialSuficiente,
    habitos: h,
    equipoPreferido: equipoPreferido(x.equipoPreferido),
    equipoOrigen: x.equipoOrigen,
    pctSeriesMaquina: x.pctSeriesMaquina,
    principales,
    tendencias: lista(x.tendencias, MAX_LISTA).map(tendencia),
  }
}

/** Perfil con la forma que las pantallas necesitan (listas recortadas, claves de más fuera); cualquier otra cosa da null. */
export function perfilEntrenamientoValido(x: unknown): PerfilEntrenamiento | null {
  try {
    return leerPerfil(x)
  } catch {
    return null
  }
}

/** Lo mismo para el objeto que ve el coach. */
export function paraCoachValido(x: unknown): ParaCoach | null {
  try {
    return leerParaCoach(x)
  } catch {
    return null
  }
}

/** 3 -> '3 días por semana'; 1 -> '1 día por semana'; 3.5 -> '3.5 días por semana'. */
export const textoFrecuencia = (diasPorSemana: number): string => t().frecuencia(diasPorSemana)

/** 'Pecho + Tríceps · 5 veces'. */
export const textoDivision = (d: DivisionFrecuente): string =>
  `${d.grupos.map(etiquetaGrupo).join(' + ') || t().sinGrupo} · ${t().veces(d.veces)}`

/** 'Tarde, hacia las 18:00 (12 sesiones)'. */
export const textoHorario = (h: HorarioHabitual): string =>
  t().horario(t().franja[h.franja], h.horaTipica, h.sesiones)

/** 'Progreso +5.2 %'; sin porcentaje, solo el estado. */
export function textoTendencia(tn: Pick<TendenciaEjercicio, 'estado' | 'cambioPct'>): string {
  const base = t().estado[tn.estado]
  if (tn.cambioPct === null) return base
  return `${base} ${tn.cambioPct > 0 ? '+' : ''}${tn.cambioPct} %`
}

/** '80 kg × 8'; en carga corporal sin lastre, '8 reps'. */
export function textoMarca(m: MarcaSerie, u: Unidad, corporal = false): string {
  if (corporal && m.pesoKg === 0) return t().reps(m.reps)
  return `${pesoEnTexto(m.pesoKg, u)} ${u} × ${m.reps}`
}

export const textoEquipo = (e: EquipoPreferido): string => t().equipo[e]

export type EstadoLectura = 'sinSesion' | 'cargando' | 'error' | 'listo'

/**
 * Lee GET perfil-entrenamiento. Sin sesión no pide nada; al recuperarla (o cambiar de cuenta o de día) vuelve a leer.
 * El resultado lleva la clave de la lectura que lo produjo, así una respuesta vieja nunca se pinta como actual.
 */
export function usePerfilEntrenamiento(sub: string, conSesion: boolean, hoy: string) {
  const [intento, setIntento] = useState(0)
  // Al recuperar la sesión la clave vuelve a ser la de antes: el contador evita pintar datos viejos.
  const [reconexiones, setReconexiones] = useState(0)
  const [sesionPrevia, setSesionPrevia] = useState(conSesion)
  if (sesionPrevia !== conSesion) {
    setSesionPrevia(conSesion)
    if (conSesion) setReconexiones((n) => n + 1)
  }
  const [recibido, setRecibido] = useState<{ clave: string; datos?: RespuestaPerfilEntrenamiento; error?: string }>()
  const clave = `${sub}|${conSesion}|${hoy}|${intento}|${reconexiones}`

  useEffect(() => {
    if (!conSesion) return
    let vivo = true
    leerPerfilEntrenamiento(hoy, -new Date().getTimezoneOffset(), sub)
      .then((datos) => vivo && setRecibido({ clave, datos }))
      .catch((e: unknown) => {
        if (vivo) setRecibido({ clave, error: clasificarFallo(e).tipo === 'noAutenticado' ? t().sesionExpirada : mensajeFallo(e) })
      })
    return () => {
      vivo = false
    }
  }, [conSesion, sub, hoy, intento, clave])

  const actual = recibido?.clave === clave ? recibido : undefined
  const estado: EstadoLectura = !conSesion ? 'sinSesion' : !actual ? 'cargando' : actual.datos ? 'listo' : 'error'
  return { estado, datos: actual?.datos, error: actual?.error ?? '', reintentar: () => setIntento((n) => n + 1) }
}

const dos = (n: number) => String(n).padStart(2, '0')

/** ISO del servidor -> 'mar 6 oct, 18:05' en hora local; si no se entiende, vacío. */
export function textoCalculado(iso: string): string {
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return ''
  return `${etiquetaFecha(fechaLocal(d))}, ${dos(d.getHours())}:${dos(d.getMinutes())}`
}
