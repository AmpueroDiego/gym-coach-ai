import { localeApp, textos } from '../i18n'
import { etiquetaFecha } from '../presentacion'

const t = textos(
  {
    cargando: 'Cargando…',
    vacio: 'Aún no tienes récords: registra series normales, al fallo o drop.',
    pesoMax: 'Peso máximo',
    e1rm: '1RM estimado',
    masReps: 'Más reps',
    reps: (n: number) => `${n} reps`,
  },
  {
    cargando: 'Loading…',
    vacio: 'No records yet: log regular, failure or drop sets.',
    pesoMax: 'Max weight',
    e1rm: 'Estimated 1RM',
    masReps: 'Most reps',
    reps: (n) => `${n} ${n === 1 ? 'rep' : 'reps'}`,
  },
)

/** `texto` ya viene formateado en kg/lb por quien llama. */
export interface RecordVista {
  ejercicioId: string
  nombre: string
  pesoMax: { texto: string; reps: number; fecha: string }
  e1rm: { texto: string } | null
  repsMax: { reps: number; texto: string; fecha: string }
}

interface Props {
  records: RecordVista[]
  cargando: boolean
  error?: string | null
}

export default function Records({ records, cargando, error }: Props) {
  if (error) return <p className="vacio" role="alert">{error}</p>
  if (cargando) return <p className="vacio">{t().cargando}</p>
  if (records.length === 0) return <p className="vacio">{t().vacio}</p>

  const orden = [...records].sort((a, b) => a.nombre.localeCompare(b.nombre, localeApp()))
  return (
    <ul className="lista-records">
      {orden.map((r) => (
        <li key={r.ejercicioId}>
          <strong>{r.nombre}</strong>
          <dl>
            <dt>{t().pesoMax}</dt>
            <dd>
              {r.pesoMax.texto} × {r.pesoMax.reps} · {etiquetaFecha(r.pesoMax.fecha)}
            </dd>
            {r.e1rm && (
              <>
                <dt>{t().e1rm}</dt>
                <dd>{r.e1rm.texto}</dd>
              </>
            )}
            <dt>{t().masReps}</dt>
            <dd>
              {t().reps(r.repsMax.reps)} ({r.repsMax.texto}) · {etiquetaFecha(r.repsMax.fecha)}
            </dd>
          </dl>
        </li>
      ))}
    </ul>
  )
}
