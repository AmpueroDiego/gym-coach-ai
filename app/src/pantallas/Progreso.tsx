import { useState, type ReactNode } from 'react'
import { textos } from '../i18n'

const t = textos(
  { historial: 'Historial', records: 'Récords', aria: 'Vista de progreso' },
  { historial: 'History', records: 'Records', aria: 'Progress view' },
)

export interface Vista {
  clave: string
  titulo: string
  nodo: ReactNode
}

interface Props {
  historial: ReactNode
  records: ReactNode
  /** Vistas extra (otros bloques): se suman después de Historial y Récords. */
  extra?: Vista[]
}

export default function Progreso(p: Props) {
  const vistas: Vista[] = [
    { clave: 'historial', titulo: t().historial, nodo: p.historial },
    { clave: 'records', titulo: t().records, nodo: p.records },
    ...(p.extra ?? []),
  ]
  const [clave, setClave] = useState(vistas[0].clave)
  const actual = vistas.find((v) => v.clave === clave) ?? vistas[0]

  return (
    <div className="progreso">
      <div className="selector-vista" role="tablist" aria-label={t().aria}>
        {vistas.map((v) => (
          <button
            key={v.clave}
            role="tab"
            id={`tab-${v.clave}`}
            aria-selected={v.clave === actual.clave}
            aria-controls={`panel-${v.clave}`}
            onClick={() => setClave(v.clave)}
          >
            {v.titulo}
          </button>
        ))}
      </div>
      <div role="tabpanel" id={`panel-${actual.clave}`} aria-labelledby={`tab-${actual.clave}`}>
        {actual.nodo}
      </div>
    </div>
  )
}
