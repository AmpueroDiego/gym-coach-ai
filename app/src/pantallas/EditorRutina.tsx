import { useEffect, useState } from 'react'
import { borrarRutina, guardarRutina, type Ejercicio, type Rutina } from '../api'
import { confirmarDescartar } from '../atras'
import { textos, type NombresEj } from '../i18n'
import {
  armarRutina,
  DESCANSOS_RUTINA,
  etiquetaDescanso,
  filaDe,
  filaNueva,
  MAX_EJERCICIOS_RUTINA,
  MAX_NOMBRE_RUTINA,
  mensajeFallo,
  mover,
  type FilaRutina,
} from '../presentacion'
import type { Unidad } from '../unidades'
import BotonFicha from './BotonFicha'
import SelectorEjercicio from './SelectorEjercicio'

interface Props {
  sub: string
  /** null = nueva. */
  rutina: Rutina | null
  /** Tope de rutinas por cuenta: una nueva solo si hay cupo (el servidor igual lo valida). */
  hayCupo: boolean
  ejercicios: Ejercicio[]
  nombres: NombresEj
  nombre: (id: string) => string
  unidadDe: (id: string) => Unidad
  recientes: string[]
  alGuardada: (r: Rutina) => void
  alBorrada: (id: string) => void
  alCerrar: () => void
  /** Avisa a App mientras se edita (oculta las pestañas: cambiar de pestaña descartaría sin avisar). */
  alEditando?: (v: boolean, estado?: EstadoEditor) => void
}

export interface EstadoEditor {
  sucio: boolean
  ocupado: boolean
}

const t = textos(
  {
    confirmarBorrar: (n: string) => `¿Borrar la rutina "${n}"? Las series ya registradas no se borran.`,
    volver: 'Volver',
    editar: 'Editar rutina',
    nueva: 'Nueva rutina',
    nombre: 'Nombre',
    ejemplo: 'Ej. Pierna A',
    yaNoExiste: ' (ya no existe)',
    subir: 'Subir',
    bajar: 'Bajar',
    quitar: (n: string) => `Quitar ${n}`,
    series: 'Series',
    reps: 'Reps',
    peso: (u: string) => `Peso (${u})`,
    delHistorial: 'del historial',
    descanso: 'Descanso',
    agregar: 'Agregar ejercicio',
    maximoEj: (n: number) => `Máximo ${n} ejercicios por rutina.`,
    sinCupo: 'Ya tienes el máximo de rutinas; borra una para crear otra.',
    quitaFaltantes: 'Quita los ejercicios que ya no existen para guardar.',
    borrar: 'Borrar',
    guardando: 'Guardando…',
    guardar: 'Guardar',
  },
  {
    confirmarBorrar: (n) => `Delete the routine "${n}"? Sets you already logged are kept.`,
    volver: 'Back',
    editar: 'Edit routine',
    nueva: 'New routine',
    nombre: 'Name',
    ejemplo: 'E.g. Legs A',
    yaNoExiste: ' (no longer exists)',
    subir: 'Move up',
    bajar: 'Move down',
    quitar: (n) => `Remove ${n}`,
    series: 'Sets',
    reps: 'Reps',
    peso: (u) => `Weight (${u})`,
    delHistorial: 'from history',
    descanso: 'Rest',
    agregar: 'Add exercise',
    maximoEj: (n) => `Maximum ${n} exercises per routine.`,
    sinCupo: "You've reached the routine limit; delete one to create another.",
    quitaFaltantes: "Remove the exercises that no longer exist to save.",
    borrar: 'Delete',
    guardando: 'Saving…',
    guardar: 'Save',
  },
)

const firma = (f: FilaRutina) => [f.id, f.series, f.reps, f.pesoTexto, f.descansoS]

const SERIES = Array.from({ length: 10 }, (_, i) => i + 1)

export default function EditorRutina(p: Props) {
  const [nombre, setNombre] = useState(p.rutina?.nombre ?? '')
  const [filas, setFilas] = useState<FilaRutina[]>(() => p.rutina?.ejercicios.map((e) => filaDe(e, p.unidadDe(e.id))) ?? [])
  const [eligiendo, setEligiendo] = useState(false)
  const [ocupado, setOcupado] = useState(false)
  const [error, setError] = useState<string | null>(null)
  // Fijo: si el PUT llega y se pierde la respuesta, el reintento sobrescribe en vez de duplicar.
  const [idNuevo] = useState(() => p.rutina?.id ?? crypto.randomUUID())
  const [inicial] = useState(() => JSON.stringify([p.rutina?.nombre ?? '', filas.map(firma)]))
  const sucio = JSON.stringify([nombre, filas.map(firma)]) !== inicial
  // Catálogo vacío = aún cargando: no marcar todo como inexistente.
  const huerfano = (id: string) => p.ejercicios.length > 0 && !p.ejercicios.some((e) => e.id === id)
  const faltantes = filas.filter((f) => huerfano(f.id))

  const { alEditando } = p
  useEffect(() => {
    alEditando?.(true)
    return () => alEditando?.(false)
  }, [alEditando])
  // App lo usa para pedir confirmación (o ignorar) con el atrás de Android.
  useEffect(() => {
    alEditando?.(true, { sucio, ocupado })
  }, [alEditando, sucio, ocupado])

  const armada = armarRutina(nombre, filas, p.unidadDe)
  const sinCupo = p.rutina === null && !p.hayCupo

  if (eligiendo) {
    return (
      <SelectorEjercicio
        ejercicios={p.ejercicios}
        nombres={p.nombres}
        recientes={p.recientes}
        excluir={new Set(filas.map((f) => f.id))}
        alElegir={(id) => {
          setFilas((fs) => (fs.some((f) => f.id === id) ? fs : [...fs, filaNueva(id)]))
          setEligiendo(false)
        }}
        alCerrar={() => setEligiendo(false)}
      />
    )
  }

  const cambiar = (i: number, cambio: Partial<FilaRutina>) =>
    setFilas((fs) => fs.map((f, j) => (j === i ? { ...f, ...cambio } : f)))

  const guardar = async () => {
    if ('error' in armada || ocupado) return
    setOcupado(true)
    setError(null)
    try {
      const r = await guardarRutina({ id: idNuevo, ...armada }, p.sub)
      p.alGuardada(r)
    } catch (e) {
      setError(mensajeFallo(e))
      setOcupado(false)
    }
  }

  const borrar = async () => {
    if (!p.rutina || ocupado) return
    if (!confirm(t().confirmarBorrar(p.rutina.nombre))) return
    setOcupado(true)
    setError(null)
    try {
      await borrarRutina(p.rutina.id, p.sub)
      p.alBorrada(p.rutina.id)
    } catch (e) {
      setError(mensajeFallo(e))
      setOcupado(false)
    }
  }

  return (
    <div className="editor-rutina">
      <header>
        <button className="cerrar" onClick={() => {
            if (!sucio || confirm(confirmarDescartar())) p.alCerrar()
          }}
          aria-label={t().volver}
        >
          ←
        </button>
        <h2>{p.rutina ? t().editar : t().nueva}</h2>
      </header>

      <label className="campo">
        <span>{t().nombre}</span>
        <input value={nombre} maxLength={MAX_NOMBRE_RUTINA} placeholder={t().ejemplo} onChange={(e) => setNombre(e.target.value)} />
      </label>

      <ol className="ejercicios-rutina">
        {filas.map((f, i) => {
          const u = p.unidadDe(f.id)
          return (
            <li key={f.id} className="bloque">
              <div className="titulo-ejercicio">
                <strong>
                  {i + 1}. {p.nombre(f.id)}
                  {huerfano(f.id) && t().yaNoExiste}
                </strong>
                <BotonFicha ejercicioId={f.id} nombre={p.nombre(f.id)} />
                <div className="orden">
                  <button onClick={() => setFilas((fs) => mover(fs, i, -1))} disabled={i === 0} aria-label={t().subir}>
                    ↑
                  </button>
                  <button onClick={() => setFilas((fs) => mover(fs, i, 1))} disabled={i === filas.length - 1} aria-label={t().bajar}>
                    ↓
                  </button>
                  <button
                    className="peligro"
                    onClick={() => setFilas((fs) => fs.filter((_, j) => j !== i))}
                    aria-label={t().quitar(p.nombre(f.id))}
                  >
                    ✕
                  </button>
                </div>
              </div>
              <div className="rejilla">
                <label className="campo">
                  <span>{t().series}</span>
                  <select value={f.series} onChange={(e) => cambiar(i, { series: Number(e.target.value) })}>
                    {SERIES.map((n) => (
                      <option key={n} value={n}>
                        {n}
                      </option>
                    ))}
                  </select>
                </label>
                <label className="campo">
                  <span>{t().reps}</span>
                  <input inputMode="numeric" value={f.reps} onChange={(e) => cambiar(i, { reps: e.target.value })} />
                </label>
                <label className="campo">
                  <span>{t().peso(u)}</span>
                  <input
                    inputMode="decimal"
                    placeholder={t().delHistorial}
                    value={f.pesoTexto}
                    onChange={(e) => cambiar(i, { pesoTexto: e.target.value })}
                  />
                </label>
                <label className="campo">
                  <span>{t().descanso}</span>
                  <select value={f.descansoS} onChange={(e) => cambiar(i, { descansoS: Number(e.target.value) })}>
                    {/* Un valor guardado fuera de la lista (el servidor acepta 0–600) se conserva. */}
                    {[...new Set([...DESCANSOS_RUTINA, f.descansoS])].sort((a, b) => a - b).map((s) => (
                      <option key={s} value={s}>
                        {etiquetaDescanso(s)}
                      </option>
                    ))}
                  </select>
                </label>
              </div>
            </li>
          )
        })}
      </ol>

      {filas.length < MAX_EJERCICIOS_RUTINA ? (
        <button className="ancho" onClick={() => setEligiendo(true)}>
          {t().agregar}
        </button>
      ) : (
        <p className="tenue">{t().maximoEj(MAX_EJERCICIOS_RUTINA)}</p>
      )}

      {sinCupo && <p className="error" role="alert">{t().sinCupo}</p>}
      {faltantes.length > 0 && <p className="error" role="alert">{t().quitaFaltantes}</p>}
      {'error' in armada && (nombre !== '' || filas.length > 0) && (
        <p className="tenue" role="alert">
          {armada.error}
        </p>
      )}
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}

      <div className="acciones">
        {p.rutina && (
          <button className="peligro" onClick={() => void borrar()} disabled={ocupado}>
            {t().borrar}
          </button>
        )}
        <button className="primario ancho" disabled={'error' in armada || ocupado || sinCupo || faltantes.length > 0} onClick={() => void guardar()}>
          {ocupado ? t().guardando : t().guardar}
        </button>
      </div>
    </div>
  )
}
