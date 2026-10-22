import { useState } from 'react'
import type { Ejercicio, Serie } from '../api'
import { textos } from '../i18n'
import { hayMasVentanas, ventanaPrevia } from '../historialPrevio'
import { agruparPorEjercicio, sumarDias } from '../prefill'
import { etiquetaFecha, textoVolumenEjercicio, textoVolumenSesion } from '../presentacion'
import { sesionesDe, ultimaSesionConRutina, type Sesion } from '../resumen'
import type { SerieLocal } from '../series'
import type { Unidad } from '../unidades'
import { ListaSeries } from './Hoy'

const t = textos(
  {
    rechazadas: 'Rechazadas por el servidor',
    corrigelas: 'Corrígelas o bórralas.',
    cargando: 'Cargando…',
    sinEntrenos: (d: number) => `Sin entrenos en los últimos ${d} días.`,
    sinRutina: 'Sin rutina',
    volumen: (v: string) => `Volumen ${v}`,
    errorCarga: 'No se pudo cargar. Intenta de nuevo.',
    nadaAntiguo: 'No hay entrenos más antiguos.',
    verAnteriores: 'Ver 30 días anteriores',
  },
  {
    rechazadas: 'Rejected by the server',
    corrigelas: 'Fix or delete them.',
    cargando: 'Loading…',
    sinEntrenos: (d) => `No workouts in the last ${d} days.`,
    sinRutina: 'No routine',
    volumen: (v) => `Volume ${v}`,
    errorCarga: "Couldn't load. Try again.",
    nadaAntiguo: 'No older workouts.',
    verAnteriores: 'View previous 30 days',
  },
)

interface Props {
  series: SerieLocal[]
  ejercicios: Map<string, Ejercicio>
  nombre: (id: string) => string
  unidadDe: (id: string) => Unidad
  hoy: string
  dias: number
  alEditar: (s: SerieLocal) => void
  /** Aún bajando el historial: no afirmar que no hay entrenos. */
  cargando?: boolean
  /** Pide series de un rango al servidor; sin él (sin sesión) no se ofrece ver más atrás. */
  cargarVentana?: (desde: string, hasta: string) => Promise<Serie[]>
}

export default function Historial(p: Props) {
  const desde = sumarDias(p.hoy, -(p.dias - 1))
  // Ventanas anteriores: solo en memoria mientras la pantalla está montada (no van al almacén ni a localStorage).
  const [previas, setPrevias] = useState<SerieLocal[]>([])
  const [ventanas, setVentanas] = useState(0)
  const [fin, setFin] = useState(false)
  const [pidiendo, setPidiendo] = useState(false)
  const [errorPrevias, setErrorPrevias] = useState(false)
  const verAnteriores = async () => {
    if (!p.cargarVentana || pidiendo) return
    const k = ventanas + 1
    const v = ventanaPrevia(p.hoy, p.dias, k)
    setPidiendo(true)
    setErrorPrevias(false)
    try {
      const nuevas = await p.cargarVentana(v.desde, v.hasta)
      setPrevias((ant) => [...ant, ...nuevas.map((s): SerieLocal => ({ ...s, estado: 'ok' }))])
      setVentanas(k)
      setFin(!hayMasVentanas(k, nuevas.length))
    } catch {
      setErrorPrevias(true)
    } finally {
      setPidiendo(false)
    }
  }
  const idsLocales = new Set(p.series.map((s) => s.id))
  const recientes = [...p.series.filter((s) => s.fecha >= desde), ...previas.filter((s) => !idsLocales.has(s.id))]
  const corporales = new Set([...p.ejercicios.values()].filter((e) => e.cargaCorporal).map((e) => e.id))
  const sesiones = sesionesDe(recientes, corporales)
  // Arriba la última rutina; si nunca hubo, la sesión más reciente.
  const destacada = ultimaSesionConRutina(sesiones) ?? sesiones[0] ?? null
  const resto = sesiones.filter((s) => s !== destacada)
  // sesionesDe deja fuera las rechazadas: van aparte para que sigan corregibles o borrables.
  const rechazadas = agruparPorEjercicio(recientes.filter((s) => s.estado === 'rechazada'))

  const total = (s: Sesion) => textoVolumenSesion(s.ejercicios, p.unidadDe, corporales)

  const detalle = (s: Sesion) =>
    s.ejercicios.map((e) => (
      <div key={e.ejercicioId} className="ejercicio-sesion">
        <div className="titulo-ejercicio">
          <strong>{p.nombre(e.ejercicioId)}</strong>
          <span>{textoVolumenEjercicio(e, p.unidadDe(e.ejercicioId), corporales.has(e.ejercicioId))}</span>
        </div>
        {/* sesionesDe devuelve las mismas series de entrada: conservan estado para editar. */}
        <ListaSeries series={e.series as SerieLocal[]} unidad={p.unidadDe(e.ejercicioId)} alEditar={p.alEditar} />
      </div>
    ))

  return (
    <div className="historial">
      {rechazadas.length > 0 && (
        <section className="bloque">
          <h2>{t().rechazadas}</h2>
          <p className="tenue">{t().corrigelas}</p>
          {rechazadas.map((b) => (
            <div key={b.ejercicioId} className="ejercicio-sesion">
              <strong>{p.nombre(b.ejercicioId)}</strong>
              <ListaSeries series={b.series} unidad={p.unidadDe(b.ejercicioId)} alEditar={p.alEditar} />
            </div>
          ))}
        </section>
      )}

      {!destacada && (p.cargando ? <p className="vacio">{t().cargando}</p> : <p className="vacio">{t().sinEntrenos(p.dias)}</p>)}

      {destacada && (
        <section className="tarjeta-sesion">
          <header>
            <h2>{destacada.rutina ?? t().sinRutina}</h2>
            <span>{etiquetaFecha(destacada.fecha)}</span>
          </header>
          <p className="total">{t().volumen(total(destacada))}</p>
          {detalle(destacada)}
        </section>
      )}

      {resto.length > 0 && (
        <ul className="sesiones">
          {resto.map((s) => (
            <li key={`${s.fecha}|${s.rutina ?? ''}`}>
              <details>
                <summary>
                  <strong>{etiquetaFecha(s.fecha)}</strong>
                  <span>
                    {s.rutina ?? t().sinRutina} · {total(s)}
                  </span>
                </summary>
                {detalle(s)}
              </details>
            </li>
          ))}
        </ul>
      )}

      {p.cargarVentana && !p.cargando && (
        <div className="mas-historial">
          {errorPrevias && <p className="tenue">{t().errorCarga}</p>}
          {fin ? (
            <p className="tenue">{t().nadaAntiguo}</p>
          ) : (
            <button className="secundario ancho" disabled={pidiendo} onClick={() => void verAnteriores()}>
              {pidiendo ? t().cargando : t().verAnteriores}
            </button>
          )}
        </div>
      )}
    </div>
  )
}
