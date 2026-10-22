import { useEffect, useState } from 'react'
import { clasificarFallo, guardarPerfilCoach, leerPerfilCoach, type EquipoPreferido, type Experiencia } from '../api'
import { EQUIPOS, EXPERIENCIAS, MAX_NOTAS, MAX_TEXTO, armarPerfil, puedeGuardar, textoPerfil, type EstadoPerfil } from '../perfilCoach'
import { textos } from '../i18n'
import { fechaLocal } from '../prefill'
import { mensajeFallo } from '../presentacion'
import { pesoEnTexto } from '../unidades'
import PerfilEntrenamiento from './PerfilEntrenamiento'
import './perfilEntrenamiento.css'

interface Props {
  sub: string
  conSesion: boolean
  /** Opcional: el contenedor (Coach) decide si hay algo que cerrar. */
  alCerrar?: () => void
}

const t = textos(
  {
    notaLarga: `La nota va en una línea de hasta ${MAX_TEXTO} caracteres.`,
    reconecta: 'Reconecta para editar tu perfil.',
    cargando: 'Cargando…',
    reintentar: 'Reintentar',
    minutos: 'Minutos disponibles por entreno',
    sinDefinir: 'Sin definir',
    dias: 'Días por semana',
    experiencia: 'Experiencia',
    cuerpo: 'Tu cuerpo',
    altura: 'Altura (cm)',
    peso: 'Peso (kg)',
    ej: (x: string) => `Ej. ${x}`,
    equipo: 'Equipo preferido',
    objetivo: 'Objetivo',
    ejObjetivo: 'Ej. ganar fuerza en banca',
    notas: 'Notas para el coach (lesiones, preferencias)',
    quitarNota: (n: number) => `Quitar nota ${n}`,
    quitar: 'Quitar',
    nuevaNota: 'Nueva nota',
    ejNota: 'Ej. me duele el hombro derecho',
    agregar: 'Agregar',
    maxNotas: `Llegaste al máximo de ${MAX_NOTAS} notas.`,
    guardado: 'Perfil guardado.',
    guardando: 'Guardando…',
    guardar: 'Guardar perfil',
  },
  {
    notaLarga: `The note goes on one line of up to ${MAX_TEXTO} characters.`,
    reconecta: 'Reconnect to edit your profile.',
    cargando: 'Loading…',
    reintentar: 'Retry',
    minutos: 'Minutes available per workout',
    sinDefinir: 'Not set',
    dias: 'Days per week',
    experiencia: 'Experience',
    cuerpo: 'Your body',
    altura: 'Height (cm)',
    peso: 'Weight (kg)',
    ej: (x) => `E.g. ${x}`,
    equipo: 'Preferred equipment',
    objetivo: 'Goal',
    ejObjetivo: 'E.g. get stronger on bench',
    notas: 'Notes for the coach (injuries, preferences)',
    quitarNota: (n) => `Remove note ${n}`,
    quitar: 'Remove',
    nuevaNota: 'New note',
    ejNota: 'E.g. my right shoulder hurts',
    agregar: 'Add',
    maxNotas: `You've reached the maximum of ${MAX_NOTAS} notes.`,
    guardado: 'Profile saved.',
    guardando: 'Saving…',
    guardar: 'Save profile',
  },
)

// Pensado para una mano a 360 px: campos de 48 px y el guardar al ancho.
export default function PerfilCoach({ sub, conSesion, alCerrar }: Props) {
  const [estado, setEstado] = useState<EstadoPerfil>(conSesion ? 'cargando' : 'sinCargar')
  const [minutos, setMinutos] = useState('')
  const [objetivo, setObjetivo] = useState('')
  const [altura, setAltura] = useState('')
  const [peso, setPeso] = useState('')
  const [equipo, setEquipo] = useState<EquipoPreferido | null>(null)
  const [experiencia, setExperiencia] = useState<Experiencia | null>(null)
  const [dias, setDias] = useState('')
  // Solo lo tocado viaja en el PUT: lo que el coach guardó con `recordar` no se pisa con datos viejos.
  const [tocados, setTocados] = useState({ experiencia: false, dias: false })
  const [notas, setNotas] = useState<string[]>([])
  const [nueva, setNueva] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [guardado, setGuardado] = useState(false)
  const [guardados, setGuardados] = useState(0)

  // Al (re)tener sesión se vuelve a leer: nunca se edita sobre un formulario que no vino del servidor.
  const [sesionPrevia, setSesionPrevia] = useState(conSesion)
  if (sesionPrevia !== conSesion) {
    setSesionPrevia(conSesion)
    if (conSesion) setEstado('cargando')
  }

  useEffect(() => {
    if (!conSesion || estado !== 'cargando') return
    let vivo = true
    leerPerfilCoach(sub)
      .then((p) => {
        if (!vivo) return
        setMinutos(p.minutosDisponibles === null ? '' : String(p.minutosDisponibles))
        setObjetivo(p.objetivo ?? '')
        // Perfiles viejos llegan sin estos campos.
        setAltura(p.alturaCm == null ? '' : String(p.alturaCm))
        setPeso(p.pesoCorporalKg == null ? '' : pesoEnTexto(p.pesoCorporalKg, 'kg'))
        // Servidor viejo: sin el campo equivale a "que lo deduzca".
        setEquipo(EQUIPOS.find((o) => o.valor === p.equipo)?.valor ?? null)
        setExperiencia(EXPERIENCIAS.find((o) => o.valor === p.experiencia)?.valor ?? null)
        setDias(p.diasPorSemana == null ? '' : String(p.diasPorSemana))
        setTocados({ experiencia: false, dias: false })
        setNotas(p.notas)
        setEstado('listo')
      })
      .catch((e: unknown) => {
        if (!vivo) return
        if (clasificarFallo(e).tipo !== 'noAutenticado') setError(mensajeFallo(e))
        setEstado('errorCarga')
      })
    return () => {
      vivo = false
    }
  }, [conSesion, sub, estado])

  const editar = (f: () => void) => {
    f()
    setGuardado(false)
  }

  const agregarNota = () => {
    const nota = textoPerfil(nueva)
    if (nota === null) {
      setError(t().notaLarga)
      return
    }
    if (notas.length >= MAX_NOTAS) return
    editar(() => {
      setNotas([...notas, nota])
      setNueva('')
      setError(null)
    })
  }

  const guardar = async () => {
    if (!puedeGuardar(estado, conSesion)) return
    // Lo escrito en el campo de nota y no agregado también cuenta.
    const pendiente = nueva.trim() === '' ? [] : [nueva]
    const { perfil, errores } = armarPerfil(minutos, objetivo, [...notas, ...pendiente], altura, peso, equipo, tocados.experiencia ? experiencia : undefined, tocados.dias ? dias : undefined)
    if (errores.length > 0) {
      setError(errores[0].mensaje)
      return
    }
    setEstado('guardando')
    setError(null)
    try {
      await guardarPerfilCoach(perfil, sub)
      setTocados({ experiencia: false, dias: false })
      setNotas(perfil.notas)
      setNueva('')
      setGuardado(true)
      setGuardados((n) => n + 1)
      alCerrar?.()
    } catch (e) {
      setError(mensajeFallo(e))
    } finally {
      setEstado('listo')
    }
  }

  const bloqueado = !puedeGuardar(estado, conSesion)

  return (
    <>
      <PerfilEntrenamiento key={guardados} sub={sub} conSesion={conSesion} hoy={fechaLocal()} />
      <form
        className="perfil-coach"
        onSubmit={(e) => {
          e.preventDefault()
          void guardar()
        }}
      >
        {!conSesion && <p className="tenue">{t().reconecta}</p>}
        {estado === 'cargando' && <p className="tenue">{t().cargando}</p>}
        {estado === 'errorCarga' && (
          <button
            type="button"
            onClick={() => {
              setError(null)
              setEstado('cargando')
            }}
          >
            {t().reintentar}
          </button>
        )}
        <label>
          <span>{t().minutos}</span>
          <input
            type="text"
            inputMode="numeric"
            placeholder={t().sinDefinir}
            value={minutos}
            disabled={bloqueado}
            maxLength={3}
            onChange={(e) => editar(() => setMinutos(e.target.value))}
          />
        </label>
        <label>
          <span>{t().dias}</span>
          <input
            type="text"
            inputMode="numeric"
            placeholder={t().sinDefinir}
            value={dias}
            disabled={bloqueado}
            maxLength={1}
            onChange={(e) => editar(() => {
                setDias(e.target.value)
                setTocados((x) => ({ ...x, dias: true }))
              })}
          />
        </label>
        <fieldset disabled={bloqueado}>
          <legend>{t().cuerpo}</legend>
          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12 }}>
            <label>
              <span>{t().altura}</span>
              <input
                type="text"
                inputMode="numeric"
                placeholder={t().ej('175')}
                value={altura}
                maxLength={6}
                onChange={(e) => editar(() => setAltura(e.target.value))}
              />
            </label>
            <label>
              <span>{t().peso}</span>
              <input
                type="text"
                inputMode="decimal"
                placeholder={t().ej('78.5')}
                value={peso}
                maxLength={8}
                onChange={(e) => editar(() => setPeso(e.target.value))}
              />
            </label>
          </div>
        </fieldset>
        <fieldset disabled={bloqueado}>
          <legend>{t().experiencia}</legend>
          <div className="opciones-equipo">
            {EXPERIENCIAS.map((o) => (
              <button key={String(o.valor)} type="button" aria-pressed={experiencia === o.valor} onClick={() => editar(() => {
                    setExperiencia(o.valor)
                    setTocados((x) => ({ ...x, experiencia: true }))
                  })}>
                {o.texto()}
              </button>
            ))}
          </div>
        </fieldset>
        <fieldset disabled={bloqueado}>
          <legend>{t().equipo}</legend>
          <div className="opciones-equipo">
            {EQUIPOS.map((o) => (
              <button key={String(o.valor)} type="button" aria-pressed={equipo === o.valor} onClick={() => editar(() => setEquipo(o.valor))}>
                {o.texto()}
              </button>
            ))}
          </div>
        </fieldset>
        <label>
          <span>{t().objetivo}</span>
          <input
            type="text"
            placeholder={t().ejObjetivo}
            value={objetivo}
            disabled={bloqueado}
            maxLength={MAX_TEXTO}
            onChange={(e) => editar(() => setObjetivo(e.target.value))}
          />
        </label>
        <fieldset disabled={bloqueado}>
          <legend>{t().notas}</legend>
          {notas.length > 0 && (
            <ul>
              {notas.map((n, i) => (
                <li key={i}>
                  <span>{n}</span>
                  <button type="button" aria-label={t().quitarNota(i + 1)} onClick={() => editar(() => setNotas(notas.filter((_, j) => j !== i)))}>
                    {t().quitar}
                  </button>
                </li>
              ))}
            </ul>
          )}
          {notas.length < MAX_NOTAS ? (
            <div className="nueva-nota">
              <input
                type="text"
                aria-label={t().nuevaNota}
                placeholder={t().ejNota}
                value={nueva}
                maxLength={MAX_TEXTO}
                enterKeyHint="done"
                onChange={(e) => setNueva(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === 'Enter') {
                    e.preventDefault()
                    agregarNota()
                  }
                }}
              />
              <button type="button" onClick={agregarNota} disabled={nueva.trim() === ''}>
                {t().agregar}
              </button>
            </div>
          ) : (
            <p className="tenue">{t().maxNotas}</p>
          )}
        </fieldset>
        {error && (
          <p className="error" role="alert">
            {error}
          </p>
        )}
        {guardado && <p role="status">{t().guardado}</p>}
        <button type="submit" className="primario ancho" disabled={bloqueado}>
          {estado === 'guardando' ? t().guardando : t().guardar}
        </button>
      </form>
    </>
  )
}
