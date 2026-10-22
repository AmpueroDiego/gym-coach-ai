import { useRef, useState } from 'react'
import type { EjercicioRutina, Serie, TipoSerie } from '../api'
import { objetivoParaSugerir, sugerida, type FilaSugerida } from '../anterior'
import { borradorInicial, filasPorBorradores, type BorradorFila, type BorradoresEjercicio } from '../estadoUi'
import { textos } from '../i18n'
import { formatoSerie } from '../prefill'
import { estadoEjercicio, letraTipo, resolverFila, textoObjetivo } from '../presentacion'
import { cuentaComoTrabajo, siguienteTipo } from '../records'
import type { SerieLocal } from '../series'
import { nombreTipo } from '../tiposSerie'
import { pesoEnTexto, type Unidad } from '../unidades'
import type { PuntoVolumen } from '../volumen'
import BotonFicha from './BotonFicha'
import GraficoVolumen from './GraficoVolumen'

interface Props {
  ejercicioId: string
  nombre: string
  unidad: Unidad
  /** Sin esto el usuario de lb tendría que abrir la hoja solo para cambiar la unidad. */
  alCambiarUnidad: (u: Unidad) => void
  objetivo: EjercicioRutina
  /** Carga corporal: sin historial ni peso objetivo se sugiere 0 (lastre), no vacío. */
  corporal: boolean
  /** Series de hoy de este ejercicio (ocupan las primeras filas). */
  hechas: SerieLocal[]
  /** Sesión anterior (fecha < hoy) y las filas que sugiere. */
  anterior: Serie[]
  filas: FilaSugerida[]
  volumen: PuntoVolumen[]
  etiquetaVolumen: string
  alGuardar: (pesoKg: number, reps: number, tipoSerie: TipoSerie) => void
  alEditar: (s: SerieLocal) => void
  /** Sube (-1) o baja (1) el ejercicio en el plan del día. */
  alMover: (dir: -1 | 1) => void
  esPrimero: boolean
  esUltimo: boolean
  /** Lo escrito sin guardar, por índice absoluto de fila (sobrevive a que maten la pestaña). */
  borradores: BorradoresEjercicio
  alBorrador: (indice: number, b: BorradorFila | null) => void
}

// Un segundo toque en ✓ cae sobre la fila recién hecha: no debe abrir la hoja.
const ANTIRREBOTE_MS = 600

const t = textos(
  {
    etiqueta: { pendiente: 'subiendo', error: 'sin guardar', rechazada: 'rechazada' } as Partial<Record<SerieLocal['estado'], string>>,
    completo: 'Completo',
    enCurso: 'En curso',
    unidad: (u: Unidad, otra: Unidad) => `Unidad: ${u}. Cambiar a ${otra}`,
    subir: (n: string) => `Subir ${n}`,
    bajar: (n: string) => `Bajar ${n}`,
    objetivo: 'Objetivo',
    serie: '+ Serie',
    tipo: (n: number, tipo: string) => `Serie ${n}: ${tipo}. Cambiar tipo`,
    vezPasada: 'La vez pasada',
    peso: (n: number, u: Unidad) => `Serie ${n}: peso en ${u}`,
    reps: (n: number) => `Serie ${n}: repeticiones`,
    guardar: (n: number) => `Guardar serie ${n}`,
  },
  {
    etiqueta: { pendiente: 'uploading', error: 'not saved', rechazada: 'rejected' },
    completo: 'Complete',
    enCurso: 'In progress',
    unidad: (u, otra) => `Unit: ${u}. Switch to ${otra}`,
    subir: (n) => `Move ${n} up`,
    bajar: (n) => `Move ${n} down`,
    objetivo: 'Target',
    serie: '+ Set',
    tipo: (n, tipo) => `Set ${n}: ${tipo}. Change type`,
    vezPasada: 'Last time',
    peso: (n, u) => `Set ${n}: weight in ${u}`,
    reps: (n) => `Set ${n}: reps`,
    guardar: (n) => `Save set ${n}`,
  },
)

/** Ejercicio del plan: una fila por serie, registrar lo de la vez pasada = un toque en ✓. */
export default function BloquePlan(p: Props) {
  // Meta de filas pedida con '+ Serie' (no un contador: guardar una extra no debe abrir otra sola).
  const [meta, setMeta] = useState(0)
  // Al menos las series objetivo: llegar a 'hecho' no exige tocar '+ Serie'. Los calentamientos no cuentan
  // para el objetivo: se suman aparte y las filas de trabajo que faltan siguen ahí.
  const trabajo = p.hechas.filter(cuentaComoTrabajo).length
  // Una fila extra con algo escrito no debe desaparecer al recargar.
  const conBorrador = filasPorBorradores(p.borradores, p.hechas.length)
  const total = Math.max(p.hechas.length + Math.max(0, p.objetivo.series - trabajo), p.filas.length, meta, conBorrador)
  const ultimaHecha = p.hechas[p.hechas.length - 1]

  const sugeridaDe = (i: number): FilaSugerida => {
    if (i < p.filas.length) return p.filas[i]
    const ref = p.anterior[i] ?? null
    // Más allá de lo sugerido: repite la última fila (la real si ya está hecha).
    return ultimaHecha ? { ref, pesoKg: ultimaHecha.pesoKg, reps: ultimaHecha.reps } : sugerida(p.anterior, objetivoParaSugerir(p.objetivo, p.corporal), i)
  }

  const estado = estadoEjercicio(p.hechas, p.objetivo.series)

  return (
    <section className={`bloque ${estado === 'hecho' ? 'hecho' : estado === 'en curso' ? 'en-curso' : ''}`} id={`bloque-${p.ejercicioId}`}>
      <header className="cabeza-bloque">
        {estado !== 'nada' && (
          <span className={`marca ${estado === 'hecho' ? 'llena' : 'parcial'}`} role="img" aria-label={estado === 'hecho' ? t().completo : t().enCurso}>
            {estado === 'hecho' ? '✓' : ''}
          </span>
        )}
        <h2>{p.nombre}</h2>
        <BotonFicha ejercicioId={p.ejercicioId} nombre={p.nombre} conSilueta />
        <button
          className="unidad-mini"
          onClick={() => p.alCambiarUnidad(p.unidad === 'kg' ? 'lb' : 'kg')}
          aria-label={t().unidad(p.unidad, p.unidad === 'kg' ? 'lb' : 'kg')}
        >
          {p.unidad}
        </button>
        <button className="mover" onClick={() => p.alMover(-1)} disabled={p.esPrimero} aria-label={t().subir(p.nombre)}>
          ↑
        </button>
        <button className="mover" onClick={() => p.alMover(1)} disabled={p.esUltimo} aria-label={t().bajar(p.nombre)}>
          ↓
        </button>
      </header>
      <p className="objetivo">{t().objetivo} {textoObjetivo(p.objetivo, p.unidad)}</p>
      <GraficoVolumen puntos={p.volumen} etiqueta={p.etiquetaVolumen} />

      <ol className="filas">
        {Array.from({ length: total }, (_, i) => {
          const hecha = p.hechas[i]
          const ref = p.anterior[i] ? formatoSerie(p.anterior[i], p.unidad) : '—'
          return hecha ? (
            <li key={`h${i}`}>
              <button className={`fila hecha ${hecha.estado}`} onClick={() => {
                  // Solo el rebote reciente se ignora; un ts inválido o futuro no deja la fila inerte.
                  const edad = Date.now() - Date.parse(hecha.ts)
                  if (!(edad >= 0 && edad < ANTIRREBOTE_MS)) p.alEditar(hecha)
                }}
                title={hecha.motivo}>
                <span className="num">
                  {i + 1}
                  <Letra tipo={hecha.tipoSerie} />
                </span>
                <span className="ref">{ref}</span>
                <span className="valor">
                  {formatoSerie(hecha, p.unidad)}
                  {t().etiqueta[hecha.estado] && <small> {t().etiqueta[hecha.estado]}</small>}
                </span>
                <span className="check" aria-hidden="true">
                  ✓
                </span>
              </button>
            </li>
          ) : (
            // La unidad en la key: al cambiarla, lo escrito (en la unidad vieja) se descarta.
            // Solo la primera pendiente guarda: un ✓ salteado ocuparía otra fila y movería lo escrito.
            <FilaPendiente
              key={`p${i}-${p.unidad}`}
              numero={i + 1}
              activa={i === p.hechas.length}
              refTexto={ref}
              unidad={p.unidad}
              sugerida={sugeridaDe(i)}
              alGuardar={p.alGuardar}
              borrador={Object.hasOwn(p.borradores, i) ? p.borradores[i] : undefined}
              alBorrador={(b) => p.alBorrador(i, b)}
            />
          )
        })}
      </ol>

      <div className="acciones">
        <button className="secundario ancho" onClick={() => setMeta(total + 1)}>
          {t().serie}
        </button>
      </div>
    </section>
  )
}

function FilaPendiente(p: {
  numero: number
  activa: boolean
  refTexto: string
  unidad: Unidad
  sugerida: FilaSugerida
  alGuardar: (pesoKg: number, reps: number, tipoSerie: TipoSerie) => void
  borrador?: BorradorFila
  alBorrador: (b: BorradorFila | null) => void
}) {
  const previo = borradorInicial(p.borrador, p.unidad)
  const [peso, setPeso] = useState(previo.peso)
  // null = sin elegir: sigue lo sugerido (el calentamiento de la vez pasada), que puede llegar después de montar.
  const [elegido, setElegido] = useState<TipoSerie | null>(previo.tipo)
  const [reps, setReps] = useState(previo.reps)
  const inputReps = useRef<HTMLInputElement>(null)
  const fila = useRef<HTMLLIElement>(null)
  const s = p.sugerida
  const tipo: TipoSerie = elegido ?? s.tipoSerie ?? 'normal'

  // Campo vacío = su sugerido (placeholder); escrito = lo escrito, con anti-deriva kg<->lb sobre el sugerido.
  const { pesoKg, reps: repsN, pesoOk, repsOk, pesoMal, repsMal } = resolverFila(peso, reps, p.unidad, s)
  const listo = p.activa && pesoOk && repsOk

  const guardar = () => {
    if (!listo) return
    p.alBorrador(null)
    p.alGuardar(pesoKg, repsN, tipo)
    // La siguiente pendiente ya está montada: el foco pasa a su peso sin tocar la pantalla.
    const sig = fila.current?.nextElementSibling?.querySelector('input')
    if (sig instanceof HTMLInputElement) sig.focus()
  }

  return (
    <li ref={fila} className={p.activa ? 'fila pendiente' : 'fila pendiente inactiva'}>
      <button
        className={tipo === 'normal' ? 'num tipo' : 'num tipo activo'}
        onClick={() => {
          const sig = siguienteTipo(tipo)
          setElegido(sig)
          p.alBorrador({ peso, reps, tipo: sig, unidad: p.unidad })
        }}
        aria-label={t().tipo(p.numero, nombreTipo(tipo))}
      >
        {p.numero}
        <Letra tipo={tipo} />
      </button>
      <span className="ref" title={t().vezPasada}>
        {p.refTexto}
      </span>
      <input
        inputMode="decimal"
        enterKeyHint="next"
        aria-label={t().peso(p.numero, p.unidad)}
        aria-invalid={pesoMal}
        className={pesoMal ? 'invalido' : ''}
        placeholder={s.pesoKg === null ? p.unidad : pesoEnTexto(s.pesoKg, p.unidad)}
        value={peso}
        onChange={(e) => {
          setPeso(e.target.value)
          p.alBorrador({ peso: e.target.value, reps, tipo: elegido, unidad: p.unidad })
        }}
        onKeyDown={(e) => e.key === 'Enter' && inputReps.current?.focus()}
      />
      <input
        ref={inputReps}
        inputMode="numeric"
        enterKeyHint="done"
        aria-label={t().reps(p.numero)}
        aria-invalid={repsMal}
        className={repsMal ? 'invalido' : ''}
        placeholder={s.reps === null ? 'reps' : String(s.reps)}
        value={reps}
        onChange={(e) => {
          setReps(e.target.value)
          p.alBorrador({ peso, reps: e.target.value, tipo: elegido, unidad: p.unidad })
        }}
        onKeyDown={(e) => e.key === 'Enter' && guardar()}
      />
      <button className="primario guardar" disabled={!listo} onClick={guardar} aria-label={t().guardar(p.numero)}>
        ✓
      </button>
    </li>
  )
}

/** C/F/D junto al número; la serie normal no lleva marca. */
function Letra({ tipo }: { tipo: TipoSerie | undefined }) {
  const l = letraTipo(tipo)
  return l ? <sup className="letra-tipo" title={nombreTipo(tipo)}>{l}</sup> : null
}
