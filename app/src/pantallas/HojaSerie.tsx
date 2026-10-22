import { useEffect, useMemo, useState } from 'react'
import { historial, type Ejercicio, type Serie, type TipoSerie } from '../api'
import type { SerieLocal } from '../series'
import { textos } from '../i18n'
import { tiposSerie } from '../tiposSerie'
import { derivarPrefill, fechaLocal, formatoSerie } from '../prefill'
import { pesoEnTexto, resolverPesoKg, sumarPaso, type Unidad } from '../unidades'

// Máximo de la API: así "Anterior" trae completo el último día aunque haya muchas series.
const LIMITE_HISTORIAL = 50

const t = textos(
  {
    lastre: 'lastre extra',
    cerrar: 'Cerrar',
    anterior: (fecha: string, series: string) => `Anterior (${fecha}): ${series}`,
    cargando: 'Cargando historial…',
    sinAnteriores: 'Sin series anteriores',
    unidad: (u: string, otra: string) => `Unidad: ${u}. Cambiar a ${otra}`,
    menosPeso: 'Menos peso',
    masPeso: 'Más peso',
    pesoEn: (u: string) => `Peso en ${u}`,
    reps: 'reps',
    menosReps: 'Menos reps',
    masReps: 'Más reps',
    repeticiones: 'Repeticiones',
    tipoSerie: 'Tipo de serie',
    rechazada: (motivo: string) => `Rechazada por el servidor: ${motivo}. Corrígela o bórrala.`,
    sinDetalle: 'sin detalle',
    noGuardo: 'No se pudo guardar (sin conexión o error del servidor).',
    rangoKg: 'Peso 0–500 kg (máx. 2 decimales)',
    rangoLb: 'Peso 0–1102.3 lb (máx. 1 decimal)',
    yReps: ' y reps 1–100.',
    borrar: 'Borrar',
    reintentar: 'Reintentar',
    guardar: 'Guardar',
  },
  {
    lastre: 'added weight',
    cerrar: 'Close',
    anterior: (fecha, series) => `Previous (${fecha}): ${series}`,
    cargando: 'Loading history…',
    sinAnteriores: 'No previous sets',
    unidad: (u, otra) => `Unit: ${u}. Switch to ${otra}`,
    menosPeso: 'Less weight',
    masPeso: 'More weight',
    pesoEn: (u) => `Weight in ${u}`,
    reps: 'reps',
    menosReps: 'Fewer reps',
    masReps: 'More reps',
    repeticiones: 'Reps',
    tipoSerie: 'Set type',
    rechazada: (motivo) => `Rejected by the server: ${motivo}. Fix it or delete it.`,
    sinDetalle: 'no details',
    noGuardo: "Couldn't save (offline or server error).",
    rangoKg: 'Weight 0–500 kg (max. 2 decimals)',
    rangoLb: 'Weight 0–1102.3 lb (max. 1 decimal)',
    yReps: ' and reps 1–100.',
    borrar: 'Delete',
    reintentar: 'Retry',
    guardar: 'Save',
  },
)

interface Props {
  ejercicio: Ejercicio
  nombre: string
  unidad: Unidad
  alCambiarUnidad: (u: Unidad) => void
  /** Si viene, la hoja edita esa serie (mismo id, fecha y ts). */
  editar?: SerieLocal
  /** Series en memoria del ejercicio: incluyen las que aún no llegan al servidor. */
  locales: Serie[]
  hoy: string
  /** Dueño de la sesión: la API rechaza la llamada si el token es de otra cuenta. */
  sub: string
  /** Sin sesión no se pide el historial; al reconectar se pide. */
  conSesion: boolean
  alGuardar: (s: Serie) => void
  alBorrar: (id: string) => void
  alCerrar: () => void
}

export default function HojaSerie(p: Props) {
  const [remotas, setRemotas] = useState<Serie[] | null>(null)
  const [pesoTexto, setPesoTexto] = useState(p.editar ? pesoEnTexto(p.editar.pesoKg, p.unidad) : '')
  const [reps, setReps] = useState(p.editar?.reps ?? 0)
  const [tocado, setTocado] = useState(p.editar !== undefined)
  // null = sin elegir: al editar, el de la serie; una nueva es normal (cero toques extra).
  const [elegido, setElegido] = useState<TipoSerie | null>(null)

  useEffect(() => {
    if (!p.conSesion) return
    let vivo = true
    historial(p.ejercicio.id, LIMITE_HISTORIAL, p.sub)
      .then((h) => vivo && setRemotas(h))
      .catch(() => vivo && setRemotas([]))
    return () => {
      vivo = false
    }
  }, [p.ejercicio.id, p.sub, p.conSesion])

  const prefill = useMemo(() => {
    const porId = new Map([...(remotas ?? []), ...p.locales].map((s) => [s.id, s]))
    return derivarPrefill([...porId.values()], p.hoy)
  }, [remotas, p.locales, p.hoy])

  // kg que originó el texto: resolverPesoKg lo devuelve tal cual si el texto no cambió (sin deriva kg<->lb).
  const baseKg = p.editar ? p.editar.pesoKg : prefill.pesoKg
  const baseReps = p.editar ? p.editar.reps : prefill.reps

  // Mientras no toque nada, la hoja sigue a la base (el historial llega después de abrir).
  const texto = tocado ? pesoTexto : pesoEnTexto(baseKg, p.unidad)
  const peso = tocado ? resolverPesoKg(pesoTexto, p.unidad, baseKg) : baseKg
  const repsActual = tocado ? reps : baseReps
  const tipo: TipoSerie = elegido ?? p.editar?.tipoSerie ?? 'normal'
  const valido = Number.isFinite(peso) && Number.isInteger(repsActual) && repsActual >= 1 && repsActual <= 100

  const tocar = () => {
    if (!tocado) {
      setPesoTexto(pesoEnTexto(baseKg, p.unidad))
      setReps(baseReps)
      setTocado(true)
    }
  }

  const sumarPeso = (dir: 1 | -1) => {
    tocar()
    setPesoTexto(sumarPaso(texto, p.unidad, dir))
  }
  const sumarReps = (d: number) => {
    tocar()
    setReps(Math.min(100, Math.max(1, repsActual + d)))
  }

  // Alternar no marca "tocado": sin tocar, el texto se deriva solo en la unidad nueva.
  const alternarUnidad = () => {
    const nueva: Unidad = p.unidad === 'kg' ? 'lb' : 'kg'
    if (tocado) setPesoTexto(pesoEnTexto(Number.isFinite(peso) ? peso : baseKg, nueva))
    p.alCambiarUnidad(nueva)
  }

  const guardar = () => {
    if (!valido) return
    p.alGuardar(
      p.editar
        ? { ...p.editar, pesoKg: peso, reps: repsActual, tipoSerie: tipo }
        : {
            id: crypto.randomUUID(),
            ejercicioId: p.ejercicio.id,
            pesoKg: peso,
            reps: repsActual,
            fecha: fechaLocal(),
            ts: new Date().toISOString(),
            ...(tipo !== 'normal' ? { tipoSerie: tipo } : {}),
          },
    )
  }

  const nota = p.ejercicio.cargaCorporal ? t().lastre : null
  // Una serie que no llegó al servidor se corrige aquí y se reenvía, o se borra.
  const sinGuardar = p.editar?.estado === 'error' || p.editar?.estado === 'rechazada'

  return (
    <div className="fondo" onClick={p.alCerrar}>
      <div className="hoja" role="dialog" aria-label={p.nombre} onClick={(e) => e.stopPropagation()}>
        <header>
          <h2>{p.nombre}</h2>
          <button className="cerrar" onClick={p.alCerrar} aria-label={t().cerrar}>
            ✕
          </button>
        </header>

        <p className="anterior">
          {prefill.anterior
            ? t().anterior(prefill.anterior.fecha, prefill.anterior.series.map((s) => formatoSerie(s, p.unidad)).join(', '))
            : remotas === null
              ? t().cargando
              : t().sinAnteriores}
        </p>

        <div className="control">
          <button
            className="etiqueta unidad"
            onClick={alternarUnidad}
            aria-label={t().unidad(p.unidad, p.unidad === 'kg' ? 'lb' : 'kg')}
          >
            {p.unidad}
            {nota && <small>{nota}</small>}
          </button>
          <button onClick={() => sumarPeso(-1)} aria-label={t().menosPeso}>
            −
          </button>
          <input
            inputMode="decimal"
            aria-label={t().pesoEn(p.unidad)}
            value={texto}
            onFocus={(e) => {
              tocar()
              e.target.select()
            }}
            onChange={(e) => setPesoTexto(e.target.value)}
          />
          <button onClick={() => sumarPeso(1)} aria-label={t().masPeso}>
            +
          </button>
        </div>

        <div className="control">
          <span className="etiqueta">{t().reps}</span>
          <button onClick={() => sumarReps(-1)} aria-label={t().menosReps}>
            −
          </button>
          <output aria-label={t().repeticiones}>{repsActual}</output>
          <button onClick={() => sumarReps(1)} aria-label={t().masReps}>
            +
          </button>
        </div>

        <div className="tipos-serie" role="group" aria-label={t().tipoSerie}>
          {tiposSerie().map((t) => (
            <button key={t.tipo} className="chip" aria-pressed={tipo === t.tipo} onClick={() => setElegido(t.tipo)}>
              {t.nombre}
            </button>
          ))}
        </div>

        {p.editar?.estado === 'rechazada' && (
          <p className="error">{t().rechazada(p.editar.motivo ?? t().sinDetalle)}</p>
        )}
        {p.editar?.estado === 'error' && <p className="error">{t().noGuardo}</p>}
        {!valido && (
          <p className="error">
            {p.unidad === 'kg' ? t().rangoKg : t().rangoLb}
            {t().yReps}
          </p>
        )}

        <div className="acciones">
          {p.editar && (
            <button className="peligro" onClick={() => p.alBorrar(p.editar!.id)}>
              {t().borrar}
            </button>
          )}
          <button className="primario ancho" disabled={!valido} onClick={guardar}>
            {sinGuardar ? t().reintentar : t().guardar}
          </button>
        </div>
      </div>
    </div>
  )
}
