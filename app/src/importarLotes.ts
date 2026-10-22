import { DemasiadasPeticiones, NoAutenticado, Rechazada, type ResultadoImportacion, type Serie } from './api'
import { sinDias, type AnalisisHevy } from './hevy'
import { idiomaApp, textos } from './i18n'

// El límite del servidor es 15 lotes por minuto.
const PAUSA_ENTRE_LOTES_MS = 2500
const REINTENTOS = 5
// Fallo sin Retry-After (red, 5xx): una espera corta antes de reintentar el mismo lote.
const ESPERA_TRANSITORIO_MS = 3000
// Un Retry-After mayor es el tope diario: esperar no sirve, se pausa.
const MAX_ESPERA_UTIL_MS = 120_000

const t = textos(
  {
    limiteDiario: 'Llegaste al límite de importación de hoy. Continúa mañana: lo ya importado queda guardado.',
    sinSesion: 'Sin sesión. Reconecta y continúa.',
    sinRespuesta: 'El servidor no responde.',
  },
  {
    limiteDiario: "You've hit today's import limit. Continue tomorrow: what's already imported is saved.",
    sinSesion: 'Signed out. Reconnect and continue.',
    sinRespuesta: 'The server is not responding.',
  },
)

export interface Totales {
  guardadas: number
  existentes: number
  rechazadas: number
  recordsActualizados: number
  recordsPendientes: number
}

export type FinLotes =
  | ({ estado: 'listo' } & Totales)
  | ({ estado: 'pausado'; siguiente: number; motivo: string } & Totales)
  | ({ estado: 'cancelado'; siguiente: number } & Totales)

interface OpcionesLotes {
  /** Lote desde el que seguir (Continuar tras una pausa). */
  desde?: number
  /** Series ya enviadas antes de `desde`, para el contador. */
  hechasInicial?: number
  cancelado?: () => boolean
  ahora?: () => number
  reintentos?: number
  /** true mientras espera por un 429/503 (el servidor pidió esperar), false al terminar. */
  alEspera?: (servidor: boolean) => void
}

type Intento =
  | { r: ResultadoImportacion }
  | { salida: 'cancelado' }
  | { salida: 'pausado'; motivo: string }
  | { salida: 'rechazado' }

const sinRutina = (s: Serie): Serie => {
  const { rutina: _rutina, ...resto } = s
  return resto
}

const vacio = (): Totales => ({ guardadas: 0, existentes: 0, rechazadas: 0, recordsActualizados: 0, recordsPendientes: 0 })

/**
 * Envía los lotes en orden, ~1 cada 2,5 s (el backend admite 25 por minuto); ante 429/503 espera y repite el MISMO lote.
 * Un 400 reintenta sin `rutina` y, si persiste, serie por serie omitiendo las rechazadas.
 * Pura: sin red ni reloj propios.
 */
export async function importarPorLotes(
  lotes: Serie[][],
  enviar: (lote: Serie[]) => Promise<ResultadoImportacion>,
  esperar: (ms: number) => Promise<void>,
  alProgreso: (hechas: number) => void,
  o: OpcionesLotes = {},
): Promise<FinLotes> {
  const ahora = o.ahora ?? Date.now
  const maxIntentos = o.reintentos ?? REINTENTOS
  let hechas = o.hechasInicial ?? 0
  // Solo suma lotes completos: si se pausa a mitad de uno, Continuar lo repite entero (ids idempotentes) sin contar doble.
  const acum = vacio()

  const intentar = async (lote: Serie[]): Promise<Intento> => {
    for (let intento = 1; ; intento++) {
      try {
        return { r: await enviar(lote) }
      } catch (e) {
        if (e instanceof NoAutenticado) return { salida: 'pausado', motivo: t().sinSesion }
        if (e instanceof Rechazada) return { salida: 'rechazado' }
        if (e instanceof DemasiadasPeticiones && e.esperaMs > MAX_ESPERA_UTIL_MS) return { salida: 'pausado', motivo: t().limiteDiario }
        if (intento >= maxIntentos) return { salida: 'pausado', motivo: t().sinRespuesta }
        o.alEspera?.(true)
        await esperar(e instanceof DemasiadasPeticiones ? e.esperaMs : ESPERA_TRANSITORIO_MS)
        o.alEspera?.(false)
        if (o.cancelado?.()) return { salida: 'cancelado' }
      }
    }
  }

  const sumar = (t: Totales, r: ResultadoImportacion) => {
    t.guardadas += r.guardadas
    t.existentes += r.existentes ?? 0
    t.recordsActualizados += r.recordsActualizados
    t.recordsPendientes += r.recordsPendientes
  }

  for (let i = o.desde ?? 0; i < lotes.length; i++) {
    if (o.cancelado?.()) return { estado: 'cancelado', siguiente: i, ...acum }
    const inicio = ahora()
    const lote = lotes[i]
    const parcial = vacio()
    let res = await intentar(lote)
    if ('r' in res) sumar(parcial, res.r)
    else if (res.salida === 'rechazado') {
      // Un dato que el navegador acepta y el servidor no (p. ej. un emoji en la rutina): primero sin rutina.
      res = await intentar(lote.map(sinRutina))
      if ('r' in res) sumar(parcial, res.r)
      else if (res.salida === 'rechazado') {
        for (const s of lote) {
          const una = await intentar([s])
          if ('r' in una) sumar(parcial, una.r)
          else if (una.salida === 'rechazado') parcial.rechazadas++
          else {
            res = una
            break
          }
          await esperar(PAUSA_ENTRE_LOTES_MS)
          if (o.cancelado?.()) {
            res = { salida: 'cancelado' }
            break
          }
        }
      }
    }
    if ('salida' in res && res.salida !== 'rechazado') {
      return res.salida === 'cancelado'
        ? { estado: 'cancelado', siguiente: i, ...acum }
        : { estado: 'pausado', siguiente: i, motivo: res.motivo, ...acum }
    }
    for (const k of Object.keys(acum) as (keyof Totales)[]) acum[k] += parcial[k]
    hechas += lote.length
    alProgreso(hechas)
    if (i < lotes.length - 1) await esperar(Math.max(0, PAUSA_ENTRE_LOTES_MS - (ahora() - inicio)))
  }
  return { estado: 'listo', ...acum }
}

/** Con "importar igual" va todo; si no, se omiten los días que ya tienen series en gym-coach. */
export function seriesAImportar(a: AnalisisHevy, dias: string[], importarIgual: boolean): Serie[] {
  return importarIgual || dias.length === 0 ? a.series : sinDias(a, dias)
}

const MESES = {
  es: ['ene', 'feb', 'mar', 'abr', 'may', 'jun', 'jul', 'ago', 'sep', 'oct', 'nov', 'dic'],
  en: ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'],
}

/** 'jun–oct 2026', 'oct 2026' o 'dic 2025–feb 2026' a partir de dos YYYY-MM-DD. */
export function rangoTexto(desde: string, hasta: string): string {
  const [ya, ma] = desde.split('-').map(Number)
  const [yb, mb] = hasta.split('-').map(Number)
  const MES = MESES[idiomaApp()]
  if (ya === yb && ma === mb) return `${MES[ma - 1]} ${ya}`
  if (ya === yb) return `${MES[ma - 1]}–${MES[mb - 1]} ${ya}`
  return `${MES[ma - 1]} ${ya}–${MES[mb - 1]} ${yb}`
}
