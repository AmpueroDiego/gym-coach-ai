import { useEffect } from 'react'
import type { Ejercicio, TipoSerie } from '../api'
import { filasSugeridas, objetivoParaSugerir, sesionAnterior } from '../anterior'
import { progresoPlan } from '../contador'
import type { BorradorFila, BorradoresEjercicio } from '../estadoUi'
import { bloquesDelDia, type Plan } from '../plan'
import { formatoSerie } from '../prefill'
import { etiquetaEjercicio, letraTipo } from '../presentacion'
import { textos } from '../i18n'
import type { SerieLocal } from '../series'
import { nombreTipo } from '../tiposSerie'
import type { Unidad } from '../unidades'
import { volumenUltimosDias } from '../volumen'
import BloquePlan from './BloquePlan'
import BotonFicha from './BotonFicha'
import { RelojEntreno } from './Relojes'

interface Props {
  /** Series de hoy. */
  series: SerieLocal[]
  /** Todas las series en memoria: gráfico y "la vez pasada". */
  historico: SerieLocal[]
  ejercicios: Map<string, Ejercicio>
  nombre: (id: string) => string
  unidadDe: (id: string) => Unidad
  alCambiarUnidad: (id: string, u: Unidad) => void
  plan: Plan | null
  /** Día del entreno: el del plan si hay uno (sobrevive a la medianoche), si no hoy. */
  hoy: string
  /** Bloque al que llevar la vista (p. ej. al elegir un ejercicio que ya está en el plan). */
  enfocar: string | null
  alEnfocado?: () => void
  alRepetir: (bloque: SerieLocal[]) => void
  alAgregarSerie: (ejercicioId: string) => void
  alGuardarFila: (ejercicioId: string, pesoKg: number, reps: number, tipoSerie: TipoSerie) => void
  /** Reordena solo el plan del día. */
  alMover: (ejercicioId: string, dir: -1 | 1) => void
  alEditar: (s: SerieLocal) => void
  alAgregarEjercicio: () => void
  alTerminar: () => void
  /** Lo escrito sin guardar en las filas, por ejercicio. */
  borradores: Record<string, BorradoresEjercicio>
  alBorrador: (ejercicioId: string, indice: number, b: BorradorFila | null) => void
}

const t = textos(
  {
    series: 'Series',
    ejercicios: 'Ejercicios',
    terminar: 'Terminar',
    vacio: 'Sin series hoy. Agrega tu primer ejercicio.',
    masSerie: '+ Serie',
    repetir: 'Repetir',
    agregarEjercicio: 'Agregar ejercicio',
    sinGuardar: 'sin guardar',
    rechazada: 'rechazada',
  },
  {
    series: 'Sets',
    ejercicios: 'Exercises',
    terminar: 'Finish',
    vacio: 'No sets today. Add your first exercise.',
    masSerie: '+ Set',
    repetir: 'Repeat',
    agregarEjercicio: 'Add exercise',
    sinGuardar: 'not saved',
    rechazada: 'rejected',
  },
)

export default function Hoy(p: Props) {
  const bloques = bloquesDelDia(p.series, p.plan?.ejercicios ?? [])
  const idsPlan = bloques.filter((b) => b.enPlan).map((b) => b.ejercicioId)
  const progreso = p.plan ? progresoPlan(p.series, p.plan.ejercicios) : null

  const { enfocar, alEnfocado } = p
  useEffect(() => {
    if (!enfocar) return
    document.getElementById(`bloque-${enfocar}`)?.scrollIntoView({ block: 'center', behavior: 'smooth' })
    // Limpio: volver a elegir el mismo ejercicio vuelve a llevar la vista.
    alEnfocado?.()
  }, [enfocar, alEnfocado])

  return (
    <div className="hoy">
      {p.plan && progreso && (
        <section className="encabezado-plan">
          <div>
            <h2>{p.plan.nombre}</h2>
            <p>
              <RelojEntreno inicio={p.plan.inicio} /> · {t().series} {progreso.seriesHechas}/{progreso.seriesObjetivo} · {t().ejercicios}{' '}
              {progreso.ejerciciosHechos}/{progreso.ejerciciosTotal}
            </p>
          </div>
          <button onClick={p.alTerminar}>{t().terminar}</button>
        </section>
      )}

      {bloques.length === 0 && <p className="vacio">{t().vacio}</p>}

      {bloques.map((b) => {
        const e = p.ejercicios.get(b.ejercicioId)
        const unidad = p.unidadDe(b.ejercicioId)
        const titulo = etiquetaEjercicio(b.ejercicioId, p.nombre(b.ejercicioId), e !== undefined)
        if (b.enPlan && b.objetivo) {
          const corporal = e?.cargaCorporal === true
          const anterior = sesionAnterior(p.historico, b.ejercicioId, p.hoy)
          return (
            <BloquePlan
              key={b.ejercicioId}
              ejercicioId={b.ejercicioId}
              nombre={titulo}
              unidad={unidad}
              alCambiarUnidad={(u) => p.alCambiarUnidad(b.ejercicioId, u)}
              objetivo={b.objetivo}
              corporal={corporal}
              hechas={b.series}
              anterior={anterior}
              filas={filasSugeridas(anterior, objetivoParaSugerir(b.objetivo, corporal))}
              volumen={volumenUltimosDias(p.historico, b.ejercicioId, p.hoy, unidad, corporal)}
              etiquetaVolumen={corporal ? 'reps' : unidad}
              alGuardar={(pesoKg, reps, tipo) => p.alGuardarFila(b.ejercicioId, pesoKg, reps, tipo)}
              alMover={(dir) => p.alMover(b.ejercicioId, dir)}
              esPrimero={idsPlan[0] === b.ejercicioId}
              esUltimo={idsPlan[idsPlan.length - 1] === b.ejercicioId}
              alEditar={p.alEditar}
              borradores={Object.hasOwn(p.borradores, b.ejercicioId) ? p.borradores[b.ejercicioId] : {}}
              alBorrador={(indice, borrador) => p.alBorrador(b.ejercicioId, indice, borrador)}
            />
          )
        }
        return (
          <section key={b.ejercicioId} className="bloque" id={`bloque-${b.ejercicioId}`}>
            <header className="cabeza-bloque">
              <h2>{titulo}</h2>
              <BotonFicha ejercicioId={b.ejercicioId} nombre={titulo} conSilueta />
            </header>
            <ListaSeries series={b.series} unidad={unidad} alEditar={p.alEditar} />
            <div className="acciones">
              <button className="secundario" onClick={() => p.alAgregarSerie(b.ejercicioId)}>
                {t().masSerie}
              </button>
              <button className="primario" onClick={() => p.alRepetir(b.series)}>
                {t().repetir} {formatoSerie(b.series[b.series.length - 1], unidad)}
              </button>
            </div>
          </section>
        )
      })}

      <div className="pie">
        <button className="primario ancho" onClick={p.alAgregarEjercicio}>
          {t().agregarEjercicio}
        </button>
      </div>
    </div>
  )
}

/** Fichas de series; tocar abre la Hoja en edición (en error/rechazada, ahí se reintenta o se borra). */
export function ListaSeries(p: { series: SerieLocal[]; unidad: Unidad; alEditar: (s: SerieLocal) => void }) {
  const etiqueta: Partial<Record<SerieLocal['estado'], string>> = { error: t().sinGuardar, rechazada: t().rechazada }
  return (
    <ol className="series">
      {p.series.map((s) => (
        <li key={s.id}>
          <button className={`ficha ${s.estado}`} onClick={() => p.alEditar(s)} title={s.motivo}>
            {formatoSerie(s, p.unidad)}
            {letraTipo(s.tipoSerie) && (
              <small className="letra-tipo" title={nombreTipo(s.tipoSerie)}>
                {' '}
                {letraTipo(s.tipoSerie)}
              </small>
            )}
            {etiqueta[s.estado] && <small> {etiqueta[s.estado]}</small>}
          </button>
        </li>
      ))}
    </ol>
  )
}
