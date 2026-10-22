import { textos } from '../i18n'
import { ALTO_GRAFICO, ANCHO_GRAFICO, BASE_GRAFICO, inicialDia, puntosGrafico } from '../presentacion'
import type { PuntoVolumen } from '../volumen'

const t = textos(
  { aria: (n: number, datos: string) => `Volumen últimos ${n} días: ${datos}` },
  { aria: (n, datos) => `Volume last ${n} days: ${datos}` },
)

interface Props {
  puntos: PuntoVolumen[]
  /** 'kg', 'lb' o 'reps' (carga corporal). */
  etiqueta: string
}

/** Volumen de los últimos días: SVG propio, sin librerías. */
export default function GraficoVolumen(p: Props) {
  const g = puntosGrafico(p.puntos)
  const aria = t().aria(p.puntos.length, p.puntos.map((x) => `${inicialDia(x.fecha)} ${x.valor} ${p.etiqueta}`).join(', '))
  return (
    <svg className="grafico" viewBox={`0 0 ${ANCHO_GRAFICO} ${ALTO_GRAFICO}`} width="100%" role="img" aria-label={aria}>
      <line className="base" x1={0} x2={ANCHO_GRAFICO} y1={BASE_GRAFICO} y2={BASE_GRAFICO} />
      <polyline className="linea" points={g.puntos.map((q) => `${q.x},${q.y}`).join(' ')} />
      {g.puntos.map((q, i) => (
        <circle key={p.puntos[i].fecha} className="punto" cx={q.x} cy={q.y} r={3.5} />
      ))}
      {g.puntos.map((q, i) => (
        <text key={'d' + p.puntos[i].fecha} className="dia-graf" x={q.x} y={ALTO_GRAFICO - 3} textAnchor="middle">
          {g.iniciales[i]}
        </text>
      ))}
      <text className="max" x={ANCHO_GRAFICO - 2} y={11} textAnchor="end">
        {g.max} {p.etiqueta}
      </text>
    </svg>
  )
}
