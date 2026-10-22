import { useEffect, useRef, useState } from 'react'
import { iniciarCiclo, prefiereMenosMovimiento, siguienteCuadro } from '../animacion'
import { textos } from '../i18n'
import { equipoDe, nombreMusculo, pasosDe, urlImagen, type FichaReferencia } from '../referencia/referencia'
import Silueta from './Silueta'
import './ficha.css'

const t = textos(
  {
    inicio: 'Inicio del movimiento',
    fin: 'Fin del movimiento',
    cerrar: 'Cerrar',
    sinFotos: 'Las fotos necesitan conexión.',
    aproximada: 'Foto aproximada: la máquina de tu gym puede verse distinta.',
    equipo: 'Equipo',
    principales: 'Principales',
    secundarios: 'Secundarios',
    fotos: 'Fotos: free-exercise-db',
    pausar: 'Pausar animación',
    reanudar: 'Reanudar animación',
  },
  {
    inicio: 'Start of the movement',
    fin: 'End of the movement',
    cerrar: 'Close',
    sinFotos: 'Photos need a connection.',
    aproximada: 'Approximate photo: the machine at your gym may look different.',
    equipo: 'Equipment',
    principales: 'Primary',
    secundarios: 'Secondary',
    fotos: 'Photos: free-exercise-db',
    pausar: 'Pause animation',
    reanudar: 'Resume animation',
  },
)

interface Props {
  nombre: string
  ficha: FichaReferencia
  alCerrar: () => void
}

/** Foco al diálogo al abrir y Esc para cerrar solo la ficha (puede abrirse sobre otro diálogo). */
function useDialogo(alCerrar: () => void) {
  const ref = useRef<HTMLDivElement>(null)
  const cerrar = useRef(alCerrar)
  useEffect(() => {
    cerrar.current = alCerrar
  })
  useEffect(() => ref.current?.focus(), [])
  useEffect(() => {
    const tecla = (e: KeyboardEvent) => {
      if (e.key !== 'Escape') return
      e.stopImmediatePropagation()
      cerrar.current()
    }
    window.addEventListener('keydown', tecla, true)
    return () => window.removeEventListener('keydown', tecla, true)
  }, [])
  return ref
}

export default function FichaEjercicio({ nombre, ficha, alCerrar }: Props) {
  const x = t()
  const dialogo = useDialogo(alCerrar)
  const [sinFotos, setSinFotos] = useState(false)
  const [cuadro, setCuadro] = useState<0 | 1>(0)
  const [quieto] = useState(prefiereMenosMovimiento)
  const [pausado, setPausado] = useState(false)

  // Pausar o desmontar detiene el ciclo; reanudar lo reinicia desde el cuadro actual.
  useEffect(() => {
    if (quieto || sinFotos || pausado) return
    return iniciarCiclo(() => setCuadro(siguienteCuadro))
  }, [quieto, sinFotos, pausado])

  // Al reanudar cambia la foto en el acto: así se nota que volvió a moverse.
  const alternarPausa = () => {
    if (pausado) setCuadro(siguienteCuadro)
    setPausado(!pausado)
  }

  const foto = (i: 0 | 1) => (
    <img
      key={i}
      src={urlImagen(ficha.imagenes[i])}
      // Animadas, el aria-label del botón ya las describe: las fotos son decorativas.
      alt={quieto ? (i === 0 ? x.inicio : x.fin) : ''}
      crossOrigin="anonymous"
      loading="lazy"
      className={quieto || cuadro === i ? 'activa' : ''}
      aria-hidden={!quieto}
      onError={() => setSinFotos(true)}
    />
  )

  const nombres = (ms: FichaReferencia['principales']) => ms.map((m) => nombreMusculo(m)).join(', ')

  return (
    <div className="fondo" onClick={alCerrar}>
      <div className="hoja ficha" role="dialog" aria-label={nombre} ref={dialogo} tabIndex={-1} onClick={(e) => e.stopPropagation()}>
        <header>
          <h2>{nombre}</h2>
          <button type="button" className="cerrar" onClick={alCerrar} aria-label={x.cerrar}>
            ✕
          </button>
        </header>
        {sinFotos ? (
          <p className="tenue">{x.sinFotos}</p>
        ) : (
          <div className={quieto ? 'fotos lado' : 'fotos animada'}>
            {quieto ? (
              [foto(0), foto(1)]
            ) : (
              <button type="button" className="marco" aria-pressed={pausado} aria-label={pausado ? x.reanudar : x.pausar} onClick={alternarPausa}>
                {[foto(0), foto(1)]}
                {pausado && (
                  <span className="play" aria-hidden="true">
                    ▶
                  </span>
                )}
              </button>
            )}
          </div>
        )}
        {ficha.aproximada && <p className="tenue">{x.aproximada}</p>}
        <p>
          {x.equipo}: {equipoDe(ficha)}
        </p>
        <Silueta principales={ficha.principales} secundarios={ficha.secundarios} />
        <p className="tenue">
          {x.principales}: {nombres(ficha.principales) || '—'}
          {ficha.secundarios.length > 0 && `. ${x.secundarios}: ${nombres(ficha.secundarios)}`}
        </p>
        <ol className="pasos">
          {pasosDe(ficha).map((p, i) => (
            <li key={i}>{p}</li>
          ))}
        </ol>
        <p className="tenue pie-ficha">{x.fotos}</p>
      </div>
    </div>
  )
}
