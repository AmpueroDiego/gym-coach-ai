import { useEffect, useState } from 'react'
import { clasificarFallo, listarRutinas, type Ejercicio, type Rutina } from '../api'
import { localeApp, textos, type NombresEj } from '../i18n'
import { MAX_RUTINAS, mensajeFallo } from '../presentacion'
import { guardarRutinasCache, leerRutinasCache } from '../rutinasCache'
import type { Unidad } from '../unidades'
import EditorRutina, { type EstadoEditor } from './EditorRutina'

interface Props {
  sub: string
  /** Con token vigente; sin él se muestra el caché y no se pide nada. */
  conSesion: boolean
  ejercicios: Ejercicio[]
  nombres: NombresEj
  nombre: (id: string) => string
  unidadDe: (id: string) => Unidad
  recientes: string[]
  alEmpezar: (r: Rutina) => void
  /** true al abrir el editor, false al salir (App oculta las pestañas mientras tanto). */
  alEditando?: (v: boolean, estado?: EstadoEditor) => void
}

const t = textos(
  {
    reentrar: 'Vuelve a entrar para ver tus rutinas.',
    reintentar: 'Reintentar',
    cargando: 'Cargando rutinas…',
    reconecta: 'Reconecta para ver tus rutinas.',
    vacio: 'Aún no tienes rutinas. Arma una y empiézala con un toque.',
    nueva: 'Nueva rutina',
    ejercicios: (n: number) => `${n} ${n === 1 ? 'ejercicio' : 'ejercicios'}`,
    yaNoExiste: ' (ya no existe)',
    editar: 'Editar',
    empezar: 'Empezar',
    maximo: (n: number) => `Llegaste al máximo de ${n} rutinas.`,
  },
  {
    reentrar: 'Sign in again to see your routines.',
    reintentar: 'Retry',
    cargando: 'Loading routines…',
    reconecta: 'Reconnect to see your routines.',
    vacio: "You don't have routines yet. Build one and start it with one tap.",
    nueva: 'New routine',
    ejercicios: (n) => `${n} ${n === 1 ? 'exercise' : 'exercises'}`,
    yaNoExiste: ' (no longer exists)',
    editar: 'Edit',
    empezar: 'Start',
    maximo: (n) => `You reached the limit of ${n} routines.`,
  },
)

const porNombre = (a: Rutina, b: Rutina) => a.nombre.localeCompare(b.nombre, localeApp())

// localStorage puede no existir o lanzar (modo privado, política del navegador).
function almacenLocal() {
  try {
    return typeof localStorage === 'undefined' ? null : localStorage
  } catch {
    return null
  }
}

export default function Rutinas(p: Props) {
  const [rutinas, setRutinas] = useState<Rutina[] | null>(() => leerRutinasCache(almacenLocal(), p.sub)?.sort(porNombre) ?? null)
  const [error, setError] = useState<string | null>(null)
  const [intento, setIntento] = useState(0)
  // null = lista; 'nueva' o la rutina que se edita.
  const [editando, setEditando] = useState<Rutina | 'nueva' | null>(null)

  useEffect(() => {
    if (rutinas !== null) guardarRutinasCache(almacenLocal(), p.sub, rutinas)
  }, [rutinas, p.sub])

  useEffect(() => {
    if (!p.conSesion) return
    let vivo = true
    listarRutinas(p.sub)
      .then((rs) => {
        if (!vivo) return
        setRutinas([...rs].sort(porNombre))
        setError(null)
      })
      .catch((e: unknown) => {
        // Sin sesión aparece el login encima; al volver, Reintentar.
        // Con lista cacheada el fallo no la tapa: se sigue mostrando lo último conocido.
        if (vivo) setError(clasificarFallo(e).tipo === 'noAutenticado' ? t().reentrar : mensajeFallo(e))
      })
    return () => {
      vivo = false
    }
  }, [p.sub, p.conSesion, intento])

  const existe = new Set(p.ejercicios.map((e) => e.id))

  if (editando) {
    return (
      <EditorRutina
        sub={p.sub}
        rutina={editando === 'nueva' ? null : editando}
        hayCupo={(rutinas?.length ?? 0) < MAX_RUTINAS}
        ejercicios={p.ejercicios}
        nombres={p.nombres}
        nombre={p.nombre}
        unidadDe={p.unidadDe}
        recientes={p.recientes}
        alGuardada={(r) => {
          setRutinas((rs) => [...(rs ?? []).filter((x) => x.id !== r.id), r].sort(porNombre))
          setEditando(null)
        }}
        alBorrada={(id) => {
          setRutinas((rs) => (rs ?? []).filter((x) => x.id !== id))
          setEditando(null)
        }}
        alCerrar={() => setEditando(null)}
        alEditando={p.alEditando}
      />
    )
  }

  if (rutinas === null) {
    return error ? (
      <div className="vacio">
        <p>{error}</p>
        <button
          className="primario"
          onClick={() => {
            setError(null)
            setIntento((n) => n + 1)
          }}
        >
          {t().reintentar}
        </button>
      </div>
    ) : (
      <p className="vacio">{p.conSesion ? t().cargando : t().reconecta}</p>
    )
  }

  return (
    <div className="rutinas">
      {error && <p className="tenue">{error}</p>}
      {rutinas.length === 0 ? (
        <div className="vacio">
          <p>{t().vacio}</p>
          <button className="primario" onClick={() => setEditando('nueva')}>
            {t().nueva}
          </button>
        </div>
      ) : (
        <>
          <ul className="lista-rutinas">
            {rutinas.map((r) => (
              <li key={r.id} className="bloque">
                <h2>{r.nombre}</h2>
                <p className="tenue">
                  {t().ejercicios(r.ejercicios.length)} · {r.ejercicios.map((e) => p.nombre(e.id) + (existe.has(e.id) || p.ejercicios.length === 0 ? '' : t().yaNoExiste)).join(', ')}
                </p>
                <div className="acciones">
                  <button onClick={() => setEditando(r)}>{t().editar}</button>
                  <button className="primario" onClick={() => p.alEmpezar(r)}>
                    {t().empezar}
                  </button>
                </div>
              </li>
            ))}
          </ul>
          {rutinas.length < MAX_RUTINAS ? (
            <button className="ancho" onClick={() => setEditando('nueva')}>
              {t().nueva}
            </button>
          ) : (
            <p className="tenue">{t().maximo(MAX_RUTINAS)}</p>
          )}
        </>
      )}
    </div>
  )
}
