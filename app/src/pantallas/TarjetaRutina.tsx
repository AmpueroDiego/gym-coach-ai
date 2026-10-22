import { useRef, useState } from 'react'
import { guardarRutina, type Rutina, type RutinaPropuesta } from '../api'
import { textos } from '../i18n'
import { etiquetaDescanso, mensajeFallo, textoObjetivo } from '../presentacion'

interface Props {
  rutina: RutinaPropuesta
  nombre: (id: string) => string
  sub: string
  /** guardada: el usuario ya la guardó desde esta tarjeta (App la empieza como rutina normal, sin ofrecer guardar una copia). */
  alEmpezar: (r: Rutina, guardada: boolean) => void
  /** Ya guardada por otro camino (p. ej. "Guardar todas" del programa): se comporta como guardada. */
  guardadaFuera?: boolean
  /** Se llama tras un PUT ok desde esta tarjeta. */
  alGuardar?: (id: string) => void
}

const t = textos(
  {
    aria: (n: string) => `Rutina propuesta: ${n}`,
    empezar: 'Empezar',
    guardada: 'Guardada',
    guardando: 'Guardando…',
    guardar: 'Guardar como rutina',
    sinPeso: 'Sin peso: elige uno con el que hagas todas las repeticiones y te queden 2 o 3 en reserva. La primera sesión sirve para calibrar.',
  },
  {
    aria: (n) => `Suggested routine: ${n}`,
    empezar: 'Start',
    guardada: 'Saved',
    guardando: 'Saving…',
    guardar: 'Save as routine',
    sinPeso: 'No weight: pick one you can do all the reps with and still have 2 or 3 left in the tank. The first session is for calibrating.',
  },
)

type Guardado = { estado: 'nada' } | { estado: 'guardando' } | { estado: 'guardada' } | { estado: 'error'; motivo: string }

/** Rutina que propuso el coach: se puede empezar tal cual o guardar entre las rutinas. */
export default function TarjetaRutina(p: Props) {
  const [guardado, setGuardado] = useState<Guardado>({ estado: 'nada' })
  // Doble toque: el segundo llegaría con el plan ya arrancado y lo reiniciaría.
  const ultimoEmpezar = useRef(-Infinity)
  const { minutosEstimados, ...rutina } = p.rutina
  const estaGuardada = guardado.estado === 'guardada' || !!p.guardadaFuera

  const guardar = async () => {
    if (guardado.estado === 'guardando' || estaGuardada) return
    setGuardado({ estado: 'guardando' })
    try {
      // PUT idempotente con el id de la propuesta: guardar dos veces no duplica.
      await guardarRutina({ id: rutina.id, nombre: rutina.nombre, ejercicios: rutina.ejercicios }, p.sub)
      setGuardado({ estado: 'guardada' })
      p.alGuardar?.(rutina.id)
    } catch (e) {
      setGuardado({ estado: 'error', motivo: mensajeFallo(e) })
    }
  }

  const empezar = (ahora: number) => {
    if (ahora - ultimoEmpezar.current < 1500) return
    ultimoEmpezar.current = ahora
    p.alEmpezar(rutina, estaGuardada)
  }

  return (
    <section className="tarjeta-rutina" aria-label={t().aria(rutina.nombre)}>
      <header>
        <h3>{rutina.nombre}</h3>
        <span className="tenue">~{minutosEstimados} min</span>
      </header>
      <ul>
        {rutina.ejercicios.map((e, i) => (
          <li key={`${i}-${e.id}`}>
            <span className="ejercicio">{p.nombre(e.id)}</span>
            <span className="objetivo-coach">
              {textoObjetivo(e, 'kg')}
              {e.descansoS > 0 && <small> · {etiquetaDescanso(e.descansoS)}</small>}
            </span>
          </li>
        ))}
      </ul>
      {rutina.ejercicios.some((e) => e.pesoKg === null) && <p className="tenue aviso-peso">{t().sinPeso}</p>}
      <div className="acciones">
        {/* Mientras se guarda no se sabe si quedará guardada: esperar evita empezar una copia. */}
        <button className="primario" onClick={(ev) => empezar(ev.timeStamp)} disabled={guardado.estado === 'guardando'}>{t().empezar}</button>
        <button className="secundario" onClick={() => void guardar()} disabled={guardado.estado === 'guardando' || estaGuardada}>
          {estaGuardada ? t().guardada : guardado.estado === 'guardando' ? t().guardando : t().guardar}
        </button>
      </div>
      {guardado.estado === 'error' && <p className="error" role="alert">{guardado.motivo}</p>}
    </section>
  )
}
