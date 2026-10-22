import { useEffect, useRef, useState, type ChangeEvent } from 'react'
import { importarSeries, type Serie } from './api'
import { FormatoHevyInvalido, analizarHevy, diasEnConflicto, enLotes, type AnalisisHevy } from './hevy'
import { textos } from './i18n'
import { importarPorLotes, seriesAImportar } from './importarLotes'

const MAX_SERIES = 20_000
const MAX_ARCHIVO_BYTES = 10 * 1024 * 1024

export type Fase =
  | { f: 'inicio'; error?: string }
  | { f: 'previa'; a: AnalisisHevy; dias: string[] }
  | { f: 'importando'; hechas: number; total: number }
  | { f: 'pausado'; hechas: number; total: number; motivo: string }
  | { f: 'listo'; hechas: number; guardadas: number; existentes: number; rechazadas: number; pendientes: number }

interface Trabajo {
  lotes: Serie[][]
  siguiente: number
  hechas: number
  a: AnalisisHevy
  guardadas: number
  existentes: number
  rechazadas: number
  actualizados: number
  pendientes: number
}

const tr = textos(
  {
    archivoGrande: 'El archivo es demasiado grande para ser una exportación de Hevy.',
    demasiadasSeries: 'El archivo es demasiado grande para importarlo de una vez.',
    noEsHevy: 'Ese archivo no parece una exportación de Hevy.',
    noSeLee: 'No se pudo leer el archivo.',
    sinConexion: 'Sin conexión. Conéctate y continúa.',
  },
  {
    archivoGrande: 'The file is too large to be a Hevy export.',
    demasiadasSeries: 'The file is too large to import at once.',
    noEsHevy: "That file doesn't look like a Hevy export.",
    noSeLee: "Couldn't read the file.",
    sinConexion: 'No connection. Reconnect and continue.',
  },
)

const total = (lotes: Serie[][]) => lotes.reduce((n, l) => n + l.length, 0)

/**
 * Estado y bucle de la importación. Vive en Principal (no en la pantalla Más) para que
 * sobreviva al cambio de pestaña; al desmontarse (cambio de cuenta) se cancela.
 */
export function useImportacionHevy(
  sub: string,
  cache: Serie[],
  alTerminar: (r: { unidad: 'lb' | 'kg'; ejerciciosImportados: string[] }) => void,
) {
  const [fase, setFase] = useState<Fase>({ f: 'inicio' })
  const [importarIgual, setImportarIgual] = useState(false)
  const [cancelando, setCancelando] = useState(false)
  const [esperandoServidor, setEsperandoServidor] = useState(false)
  const trabajo = useRef<Trabajo | null>(null)
  // Token de corrida: una corrida vieja (cancelada, reemplazada o desmontada) ya no toca el estado.
  const corrida = useRef(0)
  // Dos toques antes del re-render lanzarían dos corridas: la guarda de estado no basta.
  const corriendo = useRef(false)
  const cancelPedido = useRef(false)
  const despertar = useRef<(() => void) | null>(null)
  const avisado = useRef(false)
  const aviso = useRef(alTerminar)
  useEffect(() => {
    aviso.current = alTerminar
  })
  useEffect(
    () => () => {
      corrida.current++
      despertar.current?.()
    },
    [],
  )

  // Se interrumpe al cancelar para no quedar horas esperando un Retry-After.
  const esperar = (ms: number) =>
    new Promise<void>((resolver) => {
      const t = setTimeout(fin, ms)
      function fin() {
        clearTimeout(t)
        despertar.current = null
        resolver()
      }
      despertar.current = fin
    })

  const avisarUnaVez = (t: Trabajo) => {
    if (avisado.current) return
    avisado.current = true
    aviso.current({ unidad: t.a.unidad, ejerciciosImportados: t.a.ejerciciosImportados })
  }

  const elegirArchivo = async (e: ChangeEvent<HTMLInputElement>) => {
    const archivo = e.target.files?.[0]
    e.target.value = '' // permite elegir el mismo archivo otra vez
    if (!archivo) return
    if (archivo.size > MAX_ARCHIVO_BYTES) {
      setFase({ f: 'inicio', error: tr().archivoGrande })
      return
    }
    try {
      const a = await analizarHevy(await archivo.text(), sub)
      if (a.series.length > MAX_SERIES) {
        setFase({ f: 'inicio', error: tr().demasiadasSeries })
        return
      }
      setImportarIgual(false)
      setFase({ f: 'previa', a, dias: diasEnConflicto(a, cache) })
    } catch (err) {
      setFase({
        f: 'inicio',
        error: err instanceof FormatoHevyInvalido ? tr().noEsHevy : tr().noSeLee,
      })
    }
  }

  const correr = async () => {
    const t = trabajo.current
    if (!t || cancelando || corriendo.current) return
    corriendo.current = true
    try {
      await correrTrabajo(t)
    } finally {
      corriendo.current = false
    }
  }

  const correrTrabajo = async (t: NonNullable<typeof trabajo.current>) => {
    if (navigator.onLine === false) {
      setFase({ f: 'pausado', hechas: t.hechas, total: total(t.lotes), motivo: tr().sinConexion })
      return
    }
    const id = ++corrida.current
    cancelPedido.current = false
    setFase({ f: 'importando', hechas: t.hechas, total: total(t.lotes) })
    const fin = await importarPorLotes(
      t.lotes,
      (lote) => importarSeries(lote, sub),
      esperar,
      (hechas) => {
        t.hechas = hechas
        if (id === corrida.current) setFase({ f: 'importando', hechas, total: total(t.lotes) })
      },
      {
        desde: t.siguiente,
        hechasInicial: t.hechas,
        cancelado: () => id !== corrida.current || cancelPedido.current,
        alEspera: (s) => id === corrida.current && setEsperandoServidor(s),
      },
    )
    if (id !== corrida.current) return
    // Un Cancelar que llegó con el último lote en vuelo (o antes de agotar reintentos) termina en listo/pausado.
    cancelPedido.current = false
    setCancelando(false)
    setEsperandoServidor(false)
    t.guardadas += fin.guardadas
    t.existentes += fin.existentes
    t.rechazadas += fin.rechazadas
    t.actualizados += fin.recordsActualizados
    t.pendientes += fin.recordsPendientes
    if (fin.estado === 'listo') {
      setFase({ f: 'listo', hechas: t.hechas, guardadas: t.guardadas, existentes: t.existentes, rechazadas: t.rechazadas, pendientes: t.pendientes })
      avisarUnaVez(t)
    } else if (fin.estado === 'pausado') {
      t.siguiente = fin.siguiente
      setFase({ f: 'pausado', hechas: t.hechas, total: total(t.lotes), motivo: fin.motivo })
    } else {
      // Lo ya guardado queda (ids idempotentes): volver a importar no duplica.
      trabajo.current = null
      cancelPedido.current = false
      setCancelando(false)
      setFase({ f: 'inicio' })
      if (t.hechas > 0) avisarUnaVez(t)
    }
  }

  const empezar = () => {
    if (fase.f !== 'previa') return
    const series = seriesAImportar(fase.a, fase.dias, importarIgual)
    if (series.length === 0) return
    avisado.current = false
    trabajo.current = { lotes: enLotes(series), siguiente: 0, hechas: 0, a: fase.a, guardadas: 0, existentes: 0, rechazadas: 0, actualizados: 0, pendientes: 0 }
    void correr()
  }

  const cancelar = () => {
    const t = trabajo.current
    if (fase.f === 'importando') {
      cancelPedido.current = true
      setCancelando(true)
      despertar.current?.()
    } else if (fase.f === 'pausado') {
      trabajo.current = null
      setFase({ f: 'inicio' })
      if (t && t.hechas > 0) avisarUnaVez(t)
    }
  }

  return {
    fase,
    importarIgual,
    cancelando,
    esperandoServidor,
    setImportarIgual,
    elegirArchivo,
    empezar,
    continuar: () => void correr(),
    cancelar,
    descartarPrevia: () => setFase({ f: 'inicio' }),
    cerrarListo: () => setFase({ f: 'inicio' }),
  }
}

export type Importacion = ReturnType<typeof useImportacionHevy>
