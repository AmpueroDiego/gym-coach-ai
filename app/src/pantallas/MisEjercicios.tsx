import type { EjercicioHistorial } from '../api'
import { etiquetaGrupo } from '../ejercicios'
import { localeApp, textos } from '../i18n'
import { perfilEntrenamientoValido, textoMarca, usePerfilEntrenamiento } from '../perfilEntrenamiento'
import { etiquetaFecha } from '../presentacion'
import { fichaDe } from '../referencia/referencia'
import type { Unidad } from '../unidades'
import BotonFicha from './BotonFicha'
import Silueta from './Silueta'
import './perfilEntrenamiento.css'

const t = textos(
  {
    vacio: 'Aún no tienes ejercicios: registra o importa tus entrenos.',
    sesion: 'sesión',
    sesiones: 'sesiones',
    ultima: 'última',
    ultimaSerie: 'Última serie',
    mejorSerie: 'Mejor serie',
    sinSesion: 'Reconecta para ver tus ejercicios.',
    cargando: 'Cargando…',
    ilegible: 'La respuesta del servidor no se entiende.',
    reintentar: 'Reintentar',
  },
  {
    vacio: 'No exercises yet: log or import your workouts.',
    sesion: 'session',
    sesiones: 'sessions',
    ultima: 'last',
    ultimaSerie: 'Last set',
    mejorSerie: 'Best set',
    sinSesion: 'Reconnect to see your exercises.',
    cargando: 'Loading…',
    ilegible: 'The server response is not understood.',
    reintentar: 'Retry',
  },
)

/**
 * Pestaña "Ejercicios" de Progreso (B4): todos los ejercicios del historial, agrupados por grupo.
 * - sub: cuenta dueña de la petición (leerPerfilEntrenamiento).
 * - conSesion: sin sesión no se pide nada (estado "sin sesión"); al recuperarla se vuelve a leer.
 * - hoy: día local YYYY-MM-DD (fechaLocal()); el offset lo saca el componente de getTimezoneOffset.
 * - unidadDe: kg o lb por ejercicio, como en Historial.
 */
interface Props {
  sub: string
  conSesion: boolean
  hoy: string
  unidadDe: (id: string) => Unidad
}

/** Grupos por orden alfabético; dentro de cada uno queda el orden del servidor (más usados primero). */
function porGrupo(lista: EjercicioHistorial[]): [string, EjercicioHistorial[]][] {
  const grupos = new Map<string, EjercicioHistorial[]>()
  for (const e of lista) grupos.set(e.grupo, [...(grupos.get(e.grupo) ?? []), e])
  return [...grupos].sort((a, b) => etiquetaGrupo(a[0]).localeCompare(etiquetaGrupo(b[0]), localeApp()))
}

export function ListaEjercicios({ ejercicios, unidadDe }: { ejercicios: EjercicioHistorial[]; unidadDe: (id: string) => Unidad }) {
  const x = t()
  if (ejercicios.length === 0) return <p className="vacio">{x.vacio}</p>
  return (
    <div className="mis-ejercicios">
      {porGrupo(ejercicios).map(([grupo, lista]) => (
        <section key={grupo} aria-label={etiquetaGrupo(grupo)}>
          <h3>{etiquetaGrupo(grupo)}</h3>
          <ul>
            {lista.map((e) => {
              const u = unidadDe(e.ejercicioId)
              const ficha = fichaDe(e.ejercicioId)
              return (
                <li key={e.ejercicioId}>
                  {ficha && <Silueta principales={ficha.principales} secundarios={ficha.secundarios} mini />}
                  <div className="ej-datos">
                    <strong>{e.nombre}</strong>
                    <span>
                      {e.sesiones} {e.sesiones === 1 ? x.sesion : x.sesiones} · {x.ultima} {etiquetaFecha(e.ultimaFecha)}
                    </span>
                    {e.ultimaSerie && <span>{x.ultimaSerie}: {textoMarca(e.ultimaSerie, u, e.cargaCorporal)}</span>}
                    {e.mejorSerie && <span>{x.mejorSerie}: {textoMarca(e.mejorSerie, u, e.cargaCorporal)}</span>}
                  </div>
                  <BotonFicha ejercicioId={e.ejercicioId} nombre={e.nombre} />
                </li>
              )
            })}
          </ul>
        </section>
      ))}
    </div>
  )
}

export default function MisEjercicios({ sub, conSesion, hoy, unidadDe }: Props) {
  const x = t()
  const { estado, datos, error, reintentar } = usePerfilEntrenamiento(sub, conSesion, hoy)
  if (estado === 'sinSesion') return <p className="vacio">{x.sinSesion}</p>
  if (estado === 'cargando') return <p className="vacio">{x.cargando}</p>
  const perfil = estado === 'listo' ? perfilEntrenamientoValido(datos?.perfil) : null
  if (!perfil) {
    return (
      <>
        <p className="vacio" role="alert">
          {estado === 'error' ? error : x.ilegible}
        </p>
        <button type="button" onClick={reintentar}>
          {x.reintentar}
        </button>
      </>
    )
  }
  return <ListaEjercicios ejercicios={perfil.ejercicios} unidadDe={unidadDe} />
}
