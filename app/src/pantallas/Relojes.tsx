import { useEffect, useRef, useState } from 'react'
import { formatoReloj, restanteS, tiempoEntrenoS } from '../contador'
import {
  ajustar, debeAvisar, elegirPreset, PASO_S, PRESETS_DESCANSO, reiniciar, type DescansoActivo,
} from '../descanso'
import { textos } from '../i18n'
import './descanso.css'

const t = textos(
  {
    descanso: 'Descanso',
    restar: (s: number) => `Restar ${s} segundos`,
    quedan: (r: string) => `Quedan ${r}. Elegir otro tiempo`,
    sumar: (s: number) => `Sumar ${s} segundos`,
    reiniciar: 'Reiniciar descanso',
    saltarAria: 'Saltar descanso',
    saltar: 'Saltar',
    tiempos: 'Tiempos de descanso',
    descansar: (s: number) => `Descansar ${s} segundos`,
  },
  {
    descanso: 'Rest',
    restar: (s) => `Subtract ${s} seconds`,
    quedan: (r) => `${r} left. Pick another time`,
    sumar: (s) => `Add ${s} seconds`,
    reiniciar: 'Restart rest',
    saltarAria: 'Skip rest',
    saltar: 'Skip',
    tiempos: 'Rest times',
    descansar: (s) => `Rest ${s} seconds`,
  },
)

// El tick vive aquí: solo se re-renderiza el reloj, no la app entera.
function useAhora(cadaMs: number): number {
  const [ahora, setAhora] = useState(() => Date.now())
  useEffect(() => {
    const tic = () => setAhora(Date.now())
    tic()
    const t = setInterval(tic, cadaMs)
    // En segundo plano el intervalo se frena: al volver se recalcula con timestamps.
    const alVolver = () => document.visibilityState === 'visible' && tic()
    document.addEventListener('visibilitychange', alVolver)
    return () => {
      clearInterval(t)
      document.removeEventListener('visibilitychange', alVolver)
    }
  }, [cadaMs])
  return ahora
}

interface PropsDescanso {
  descanso: DescansoActivo
  alCambiar: (d: DescansoActivo) => void
  alSaltar: () => void
  /** Llega a 0: avisar=true suena (una vez); false si ya pasó la gracia y se descarta en silencio. */
  alFin: (avisar: boolean) => void
}

export function BarraDescanso(p: PropsDescanso) {
  const ahora = useAhora(250)
  const [abierto, setAbierto] = useState(false)
  const resta = restanteS(p.descanso, ahora)
  const termino = useRef(false)
  const { alFin, descanso } = p

  useEffect(() => {
    if (resta > 0) {
      termino.current = false  // se alargó o reinició: puede volver a terminar
      return
    }
    if (termino.current) return
    termino.current = true
    alFin(debeAvisar(descanso, Date.now()))
  }, [resta, alFin, descanso])

  const cambiar = (d: DescansoActivo) => p.alCambiar(d)

  return (
    <div className="descanso barra-descanso" role="timer" aria-live="off" aria-label={t().descanso}>
      <div className="fila-descanso">
        <button aria-label={t().restar(PASO_S)} onClick={() => cambiar(ajustar(descanso, -PASO_S, Date.now()))}>
          −{PASO_S}
        </button>
        <button
          className="reloj secundario"
          aria-label={t().quedan(formatoReloj(resta))}
          aria-expanded={abierto}
          onClick={() => setAbierto((a) => !a)}
        >
          {formatoReloj(resta)}
        </button>
        <button aria-label={t().sumar(PASO_S)} onClick={() => cambiar(ajustar(descanso, PASO_S, Date.now()))}>
          +{PASO_S}
        </button>
        <button aria-label={t().reiniciar} onClick={() => cambiar(reiniciar(descanso, Date.now()))}>
          ↺
        </button>
        <button aria-label={t().saltarAria} onClick={p.alSaltar}>{t().saltar}</button>
      </div>
      {abierto && (
        <div className="presets-descanso" role="group" aria-label={t().tiempos}>
          {PRESETS_DESCANSO.map((s) => (
            <button
              key={s}
              aria-label={t().descansar(s)}
              aria-pressed={descanso.duracionS === s}
              onClick={() => {
                cambiar(elegirPreset(descanso, s, Date.now()))
                setAbierto(false)
              }}
            >
              {s} s
            </button>
          ))}
        </div>
      )}
    </div>
  )
}

export function RelojEntreno({ inicio }: { inicio: number }) {
  const ahora = useAhora(1000)
  return <span className="reloj">{formatoReloj(tiempoEntrenoS({ inicio }, ahora))}</span>
}
