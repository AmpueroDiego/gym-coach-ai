import type { CSSProperties } from 'react'
import Model from 'react-body-highlighter'
import { datosSilueta, vistaPrincipal } from '../referencia/musculos'
import { textos } from '../i18n'
import { nombreMusculo, type MusculoRef } from '../referencia/referencia'
import './silueta.css'

const COLORES = ['#ef9a9a', '#e53935']
const CUERPO = '#4a525c'

const t = textos(
  { ninguno: 'ninguno', principales: 'Principales', secundarios: 'Secundarios' },
  { ninguno: 'none', principales: 'Primary', secundarios: 'Secondary' },
)

const nombres = (l: MusculoRef[]) => l.map(nombreMusculo).join(', ') || t().ninguno

export default function Silueta({ principales, secundarios, mini }: { principales: MusculoRef[]; secundarios: MusculoRef[]; mini?: boolean }) {
  const x = t()
  const data = datosSilueta(principales, secundarios)
  if (mini) {
    return (
      <span className="silueta-mini" aria-hidden="true">
        <Model data={data} type={vistaPrincipal(principales)} highlightedColors={COLORES} bodyColor={CUERPO} />
      </span>
    )
  }
  return (
    <div className="silueta">
      <div className="silueta-vistas" role="img" aria-label={`${x.principales}: ${nombres(principales)}; ${x.secundarios.toLowerCase()}: ${nombres(secundarios)}`}>
        <div className="silueta-vista"><Model data={data} type="anterior" highlightedColors={COLORES} bodyColor={CUERPO} /></div>
        <div className="silueta-vista"><Model data={data} type="posterior" highlightedColors={COLORES} bodyColor={CUERPO} /></div>
      </div>
      <div className="silueta-leyenda">
        <span style={{ '--c': COLORES[1] } as CSSProperties}>{x.principales}: {nombres(principales)}</span>
        <span style={{ '--c': COLORES[0] } as CSSProperties}>{x.secundarios}: {nombres(secundarios)}</span>
      </div>
    </div>
  )
}
