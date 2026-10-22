import { useEffect, useMemo, useRef, useState } from 'react'
import { guardarRutina, listarRutinas, type EjercicioRutina } from '../api'
import { textos, useIdioma } from '../i18n'
import type { Plan } from '../plan'
import { mensajeFallo } from '../presentacion'
import { abreviar, ariaRadar, resumenDeRutina } from '../resumenFinal'
import type { SerieLocal } from '../series'
import { rutinaConCambios, rutinaNuevaDePlan, type ErrorTope } from '../terminarRutina'
import type { Unidad } from '../unidades'

interface Props {
  sub: string
  plan: Plan
  /** Las mismas series en memoria que ve la pantalla. */
  series: SerieLocal[]
  hoy: string
  /** Agregados sobre la marcha con series hoy: se ofrece pasarlos a la rutina. */
  nuevos: EjercicioRutina[]
  /** El usuario reordenó los ejercicios de la rutina de hoy: se ofrece guardar el nuevo orden. */
  ordenCambiado: boolean
  corporales: Set<string>
  nombre: (id: string) => string
  unidadDe: (id: string) => Unidad
  /** Cierra sin terminar (toque sin querer en Terminar). */
  alSeguir: () => void
  /** Borra el plan; las series no se tocan. */
  alListo: () => void
}

type Fase = 'preguntar' | 'resumen'

/** Estado del error como clave estable: el texto se traduce al pintar, no al guardarlo. */
type ErrorUi = 'perdida' | ErrorTope | { texto: string }

const t = textos(
  {
    rutinaPerdida: 'La rutina ya no existe (¿la borraste?).',
    guardarRutina: 'Guardar rutina',
    guardarComo: (n: string) => `¿Guardar "${n}" como rutina?`,
    seguir: 'Seguir entrenando',
    propuesta: 'Es la que te propuso el coach; se guarda como está hoy, con lo que agregaste y el orden.',
    guardando: 'Guardando…',
    reintentar: 'Reintentar',
    guardarNueva: 'Guardar como rutina nueva',
    terminarSinGuardar: 'Terminar sin guardar',
    actualizarRutina: 'Actualizar rutina',
    actualizarPregunta: (n: string) => `¿Actualizar la rutina "${n}"?`,
    agregarNuevos: 'Agregar los ejercicios nuevos: ',
    demasiados: (n: number, max: number) => `La rutina quedaría con ${n} ejercicios; el máximo es ${max}.`,
    guardarOrden: 'Guardar el nuevo orden de los ejercicios',
    siActualizar: 'Sí, actualizar',
    terminarSinActualizar: 'Terminar sin actualizar',
    noSoloTerminar: 'No, solo terminar',
    nuevo: ' (nuevo)',
    resumen: 'Resumen de la rutina',
    duracion: 'Duración',
    series: 'Series',
    ejercicios: 'Ejercicios',
    volumen: 'Volumen',
    hoy: 'Hoy',
    promedio: 'Promedio del último mes',
    listo: 'Listo',
  },
  {
    rutinaPerdida: 'The routine no longer exists (did you delete it?).',
    guardarRutina: 'Save routine',
    guardarComo: (n) => `Save "${n}" as a routine?`,
    seguir: 'Keep training',
    propuesta: 'This is the one the coach suggested; it is saved as it is today, with what you added and the order.',
    guardando: 'Saving…',
    reintentar: 'Retry',
    guardarNueva: 'Save as new routine',
    terminarSinGuardar: 'Finish without saving',
    actualizarRutina: 'Update routine',
    actualizarPregunta: (n) => `Update the routine "${n}"?`,
    agregarNuevos: 'Add the new exercises: ',
    demasiados: (n, max) => `The routine would end up with ${n} exercises; the maximum is ${max}.`,
    guardarOrden: 'Save the new exercise order',
    siActualizar: 'Yes, update',
    terminarSinActualizar: 'Finish without updating',
    noSoloTerminar: 'No, just finish',
    nuevo: ' (new)',
    resumen: 'Routine summary',
    duracion: 'Duration',
    series: 'Sets',
    ejercicios: 'Exercises',
    volumen: 'Volume',
    hoy: 'Today',
    promedio: 'Average of the last month',
    listo: 'Done',
  },
)

/** Foco al diálogo al abrir y Esc para cerrar (solo si `puedeCerrar`). */
function useDialogo(alCerrar: () => void, puedeCerrar: boolean) {
  const ref = useRef<HTMLDivElement>(null)
  useEffect(() => ref.current?.focus(), [])
  useEffect(() => {
    if (!puedeCerrar) return
    const tecla = (e: KeyboardEvent) => e.key === 'Escape' && alCerrar()
    document.addEventListener('keydown', tecla)
    return () => document.removeEventListener('keydown', tecla)
  }, [alCerrar, puedeCerrar])
  return ref
}

export default function TerminarRutina(p: Props) {
  useIdioma()
  const [fase, setFase] = useState<Fase>(p.plan.propuesta || p.nuevos.length > 0 || p.ordenCambiado ? 'preguntar' : 'resumen')
  const [ocupado, setOcupado] = useState(false)
  // Agregar es lo de siempre (se acepta al pulsar «Sí»); el orden es opt-in: nunca se cambia sin marcarlo.
  const [agregar, setAgregar] = useState(true)
  const [ordenar, setOrdenar] = useState(false)
  const haceAlgo = (p.nuevos.length > 0 && agregar) || (p.ordenCambiado && ordenar)
  const [error, setError] = useState<ErrorUi | null>(null)
  // Fijo al abrir: el resumen no cambia mientras se lee.
  const [ahora] = useState(() => Date.now())
  // Congeladas al abrir: el radar no se recrea con cada cambio de estado de una serie.
  const [series] = useState(() => p.series)
  const dialogo = useDialogo(p.alSeguir, fase === 'preguntar' && !ocupado)

  const actualizar = async () => {
    setOcupado(true)
    setError(null)
    try {
      const original = (await listarRutinas(p.sub)).find((r) => r.id === p.plan.rutinaId)
      const nueva = original ? rutinaConCambios(
            original,
            agregar ? p.nuevos : [],
            ordenar ? p.plan.ejercicios.map((e) => e.id) : null,
          ) : { error: 'perdida' as const }
      if ('error' in nueva) {
        setError(nueva.error === 'perdida' ? 'perdida' : nueva)
        return
      }
      await guardarRutina(nueva, p.sub)
      setFase('resumen')
    } catch (e) {
      setError({ texto: mensajeFallo(e) })
    } finally {
      setOcupado(false)
    }
  }

  // Propuesta del coach: no hay rutina que actualizar; se ofrece guardar la de hoy como nueva (con lo agregado y el orden final).
  const guardarNueva = async () => {
    setOcupado(true)
    setError(null)
    try {
      await guardarRutina(rutinaNuevaDePlan(p.plan, crypto.randomUUID()), p.sub)
      setFase('resumen')
    } catch (e) {
      setError({ texto: mensajeFallo(e) })
    } finally {
      setOcupado(false)
    }
  }

  if (fase === 'preguntar' && p.plan.propuesta) {
    return (
      <div className="fondo" onClick={() => !ocupado && p.alSeguir()}>
        <div className="hoja" role="dialog" aria-label={t().guardarRutina} ref={dialogo} tabIndex={-1} onClick={(e) => e.stopPropagation()}>
          <header>
            <h2>{t().guardarComo(p.plan.nombre)}</h2>
            <button className="cerrar" onClick={p.alSeguir} aria-label={t().seguir}>
              ✕
            </button>
          </header>
          <p className="tenue">{t().propuesta}</p>
          {error && (
            <p className="error" role="alert">
              {textoError(error)}
            </p>
          )}
          <div className="acciones columna">
            <button className="primario ancho" disabled={ocupado} onClick={() => void guardarNueva()}>
              {ocupado ? t().guardando : error ? t().reintentar : t().guardarNueva}
            </button>
            <button className="ancho" disabled={ocupado} onClick={() => setFase('resumen')}>
              {t().terminarSinGuardar}
            </button>
          </div>
        </div>
      </div>
    )
  }

  if (fase === 'preguntar') {
    return (
      <div className="fondo" onClick={() => !ocupado && p.alSeguir()}>
        <div className="hoja" role="dialog" aria-label={t().actualizarRutina} ref={dialogo} tabIndex={-1} onClick={(e) => e.stopPropagation()}>
          <header>
            <h2>{t().actualizarPregunta(p.plan.nombre)}</h2>
            <button className="cerrar" onClick={p.alSeguir} aria-label={t().seguir}>
              ✕
            </button>
          </header>
          <fieldset className="cambios-rutina" disabled={ocupado}>
            {p.nuevos.length > 0 && (
              <label>
                <input type="checkbox" checked={agregar} onChange={(e) => setAgregar(e.target.checked)} />
                <span>{t().agregarNuevos}{p.nuevos.map((n) => p.nombre(n.id)).join(', ')}</span>
              </label>
            )}
            {p.ordenCambiado && (
              <label>
                <input type="checkbox" checked={ordenar} onChange={(e) => setOrdenar(e.target.checked)} />
                <span>{t().guardarOrden}</span>
              </label>
            )}
          </fieldset>
          <AccionesActualizar
            error={error}
            ocupado={ocupado}
            haceAlgo={haceAlgo}
            alActualizar={() => void actualizar()}
            alTerminar={() => setFase('resumen')}
          />
        </div>
      </div>
    )
  }

  return <Resumen {...p} series={series} ahora={ahora} />
}

const textoError = (e: ErrorUi): string =>
  e === 'perdida' ? t().rutinaPerdida : 'texto' in e ? e.texto : t().demasiados(e.n, e.max)

/** Con la rutina perdida no hay nada que actualizar: solo queda terminar, y es la acción principal. */
export function AccionesActualizar(p: {
  error: ErrorUi | null
  ocupado: boolean
  haceAlgo: boolean
  alActualizar: () => void
  alTerminar: () => void
}) {
  const perdida = p.error === 'perdida'
  return (
    <>
      {p.error && (
        <p className="error" role="alert">
          {textoError(p.error)}
        </p>
      )}
      <div className="acciones columna">
        {!perdida && (
          <button className="primario ancho" disabled={p.ocupado || !p.haceAlgo} onClick={p.alActualizar}>
            {p.ocupado ? t().guardando : p.error ? t().reintentar : t().siActualizar}
          </button>
        )}
        <button className={perdida ? 'primario ancho' : 'ancho'} disabled={p.ocupado} onClick={p.alTerminar}>
          {p.error ? t().terminarSinActualizar : t().noSoloTerminar}
        </button>
      </div>
    </>
  )
}

function Resumen(p: Props & { ahora: number }) {
  const { series, plan, hoy, unidadDe, ahora, corporales, nombre } = p
  // resumenDeRutina arma texto: cambia con el idioma.
  const idioma = useIdioma()
  const r = useMemo(
    () => {
      void idioma // resumenDeRutina lee el idioma por fuera: que cambie la memo
      return resumenDeRutina(series, plan, hoy, unidadDe, ahora, corporales, nombre)
    },
    [series, plan, hoy, unidadDe, ahora, corporales, nombre, idioma],
  )
  const lienzo = useRef<HTMLCanvasElement>(null)
  const [sinGrafico, setSinGrafico] = useState(false)
  const dialogo = useDialogo(p.alSeguir, true)

  useEffect(() => {
    if (!r.radar || !lienzo.current) return
    const canvas = lienzo.current
    let cancelado = false
    let grafico: { destroy: () => void } | null = null
    // Chart.js solo se descarga aquí; sin red, el resumen sigue con el texto.
    import('../graficoRadar')
      .then(({ dibujarRadar }) => {
        if (cancelado) return
        grafico = dibujarRadar(canvas, {
          etiquetas: r.ejes.map((e) => abreviar(nombre(e.ejercicioId)) + (e.pct === null ? t().nuevo : '')),
          pct: r.ejes.map((e) => e.pct),
        })
      })
      .catch(() => !cancelado && setSinGrafico(true))
    return () => {
      cancelado = true
      grafico?.destroy()
    }
  }, [r, nombre, idioma])

  return (
    <div className="fondo">
      <div className="hoja resumen" role="dialog" aria-label={t().resumen} ref={dialogo} tabIndex={-1}>
        <header>
          <h2>{t().resumen}</h2>
          {/* Un toque sin querer en Terminar no debe cerrar la rutina. */}
          <button className="cerrar" onClick={p.alSeguir} aria-label={t().seguir}>
            ✕
          </button>
        </header>
        <p className="tenue">{p.plan.nombre}</p>
        <dl className="cifras">
          <div>
            <dt>{t().duracion}</dt>
            <dd>{r.duracion}</dd>
          </div>
          <div>
            <dt>{t().series}</dt>
            <dd>{r.series}</dd>
          </div>
          <div>
            <dt>{t().ejercicios}</dt>
            <dd>{r.ejercicios}</dd>
          </div>
          <div>
            <dt>{t().volumen}</dt>
            <dd>{r.volumen}</dd>
          </div>
        </dl>
        <p>{r.avance}</p>
        {r.radar && !sinGrafico && (
          <figure className="radar">
            <div className="lienzo">
              <canvas ref={lienzo} role="img" aria-label={ariaRadar(r.ejes, p.nombre)} />
            </div>
            <figcaption className="leyenda">
              <span className="hoy">{t().hoy}</span>
              <span className="promedio">{t().promedio}</span>
            </figcaption>
          </figure>
        )}
        {/* Sin gráfico (sin red o menos de 3 ejes) el detalle va como texto visible. */}
        {(!r.radar || sinGrafico) && r.ejes.length > 0 && <p className="tenue">{ariaRadar(r.ejes, p.nombre)}</p>}
        <button className="primario ancho" onClick={p.alListo}>
          {t().listo}
        </button>
      </div>
    </div>
  )
}
