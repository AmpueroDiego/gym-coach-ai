import { useRef, useState } from 'react'
import { guardarRutina, type ProgramaPropuesto, type Rutina, type RutinaPropuesta } from '../api'
import { textos } from '../i18n'
import { mensajeFallo } from '../presentacion'
import TarjetaRutina from './TarjetaRutina'

interface Props {
  programa: ProgramaPropuesto
  nombre: (id: string) => string
  sub: string
  alEmpezar: (r: Rutina, guardada: boolean) => void
}

const t = textos(
  {
    aria: (n: string) => `Programa propuesto: ${n}`,
    sesiones: (k: number) => `${k} sesiones`,
    guardarTodas: 'Guardar todas',
    guardando: 'Guardando…',
    todas: 'Todas guardadas',
    parcial: (n: number, m: number) => `Guardadas ${n} de ${m}. `,
  },
  {
    aria: (n) => `Suggested program: ${n}`,
    sesiones: (k) => `${k} sessions`,
    guardarTodas: 'Save all',
    guardando: 'Saving…',
    todas: 'All saved',
    parcial: (n, m) => `Saved ${n} of ${m}. `,
  },
)

/**
 * Guarda en orden, saltando las ya guardadas; corta en el primer error. PUT idempotente con el id
 * del servidor, así que reintentar no duplica.
 */
export async function guardarEnOrden(
  rutinas: RutinaPropuesta[],
  yaGuardadas: Set<string>,
  guardar: (r: RutinaPropuesta) => Promise<unknown>,
  alGuardar: (id: string) => void,
): Promise<{ guardadas: number; error?: unknown }> {
  let guardadas = 0
  for (const r of rutinas) {
    if (yaGuardadas.has(r.id)) {
      guardadas++
      continue
    }
    try {
      await guardar(r)
    } catch (error) {
      return { guardadas, error }
    }
    alGuardar(r.id)
    guardadas++
  }
  return { guardadas }
}

/** Programa de varias sesiones: cada una se empieza o se guarda sola, o se guardan todas de una vez. */
export default function TarjetaPrograma(p: Props) {
  const [guardadas, setGuardadas] = useState<Set<string>>(new Set())
  const [guardando, setGuardando] = useState(false)
  const [error, setError] = useState<string | null>(null)
  // El estado llega tarde al segundo toque; la ref lo frena al instante.
  const ocupado = useRef(false)
  const total = p.programa.rutinas.length
  const todas = guardadas.size >= total

  const marcar = (id: string) => setGuardadas((g) => (g.has(id) ? g : new Set(g).add(id)))

  const guardarTodas = async () => {
    if (ocupado.current || todas) return
    ocupado.current = true
    setGuardando(true)
    setError(null)
    const r = await guardarEnOrden(
      p.programa.rutinas,
      guardadas,
      (x) => guardarRutina({ id: x.id, nombre: x.nombre, ejercicios: x.ejercicios }, p.sub),
      marcar,
    )
    if (r.error !== undefined) setError(t().parcial(r.guardadas, total) + mensajeFallo(r.error))
    setGuardando(false)
    ocupado.current = false
  }

  return (
    <section className="tarjeta-programa" aria-label={t().aria(p.programa.nombre)}>
      <header>
        <h3>{p.programa.nombre}</h3>
        <span className="tenue">{t().sesiones(total)}</span>
      </header>
      {p.programa.rutinas.map((r) => (
        <TarjetaRutina key={r.id} rutina={r} nombre={p.nombre} sub={p.sub} alEmpezar={p.alEmpezar} guardadaFuera={guardadas.has(r.id)} alGuardar={marcar} />
      ))}
      <button className="secundario" onClick={() => void guardarTodas()} disabled={guardando || todas}>
        {todas ? t().todas : guardando ? t().guardando : t().guardarTodas}
      </button>
      {error && <p className="error" role="alert">{error}</p>}
    </section>
  )
}
