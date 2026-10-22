// Aviso de fin de descanso: 3 pitidos (Web Audio) + vibración. Tolerante: sin soporte no hace nada.
export interface DepsAvisador {
  crearContexto?: () => AudioContext | null
  vibrar?: (patron: number[]) => void
}

const PITIDOS = 3
const FRECUENCIA = 880
const DURACION = 0.15
const SEPARACION = 0.1
const VOLUMEN = 0.3

const contextoPorDefecto = (): AudioContext | null =>
  typeof AudioContext === 'undefined' ? null : new AudioContext()

const vibrarPorDefecto = (patron: number[]) => {
  if (typeof navigator !== 'undefined' && typeof navigator.vibrate === 'function') navigator.vibrate(patron)
}

export function crearAvisador(deps: DepsAvisador = {}) {
  const crear = deps.crearContexto ?? contextoPorDefecto
  const vibrar = deps.vibrar ?? vibrarPorDefecto
  let ctx: AudioContext | null = null

  const contexto = () => {
    // Un contexto cerrado ya no sirve: se vuelve a crear.
    if (ctx?.state === 'closed') ctx = null
    if (!ctx) {
      try {
        ctx = crear()
      } catch {
        ctx = null
      }
    }
    return ctx
  }

  return {
    /** Llamar dentro del toque de guardar serie: el navegador exige un gesto para activar el audio. */
    desbloquear(): void {
      try {
        void contexto()?.resume()?.catch(() => {})
      } catch {
        // Sin audio: el aviso queda solo en vibración.
      }
    },
    sonar(): void {
      try {
        const c = contexto()
        if (c) {
          // iOS lo deja 'interrupted' (llamada, bloqueo), no solo 'suspended'.
          if (c.state !== 'running') {
            try {
              void c.resume()?.catch(() => {})
            } catch {
              // Sigue: los pitidos se programan igual y la vibración no depende de esto.
            }
          }
          const t0 = c.currentTime
          for (let i = 0; i < PITIDOS; i++) {
            const inicio = t0 + i * (DURACION + SEPARACION)
            const osc = c.createOscillator()
            const ganancia = c.createGain()
            osc.frequency.value = FRECUENCIA
            // Envolvente corta para que no haya clic al arrancar ni al cortar.
            ganancia.gain.setValueAtTime(0, inicio)
            ganancia.gain.linearRampToValueAtTime(VOLUMEN, inicio + 0.01)
            ganancia.gain.linearRampToValueAtTime(0, inicio + DURACION)
            osc.connect(ganancia)
            ganancia.connect(c.destination)
            osc.start(inicio)
            osc.stop(inicio + DURACION)
          }
        }
      } catch {
        // Audio no disponible: se sigue con la vibración.
      }
      try {
        vibrar([200, 100, 200])
      } catch {
        // Sin vibración.
      }
    },
  }
}
