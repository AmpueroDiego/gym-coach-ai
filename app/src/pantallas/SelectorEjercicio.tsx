import { useMemo, useState } from 'react'
import type { Ejercicio } from '../api'
import { etiquetaGrupo, filtrarEjercicios, gruposDe, nombreEjercicio, type FiltroEjercicios } from '../ejercicios'
import { textos, type NombresEj } from '../i18n'
import BotonFicha from './BotonFicha'

interface Props {
  ejercicios: Ejercicio[]
  nombres: NombresEj
  /** Ids usados hace poco, el más reciente primero. */
  recientes: string[]
  /** Ids que no se ofrecen (p. ej. los que ya están en la rutina). */
  excluir?: Set<string>
  alElegir: (id: string) => void
  alCerrar: () => void
}

const t = textos(
  {
    todos: 'Todos',
    maquina: 'Máquina',
    libre: 'Libre',
    volver: 'Volver',
    buscar: 'Buscar ejercicio',
    tipo: 'Tipo',
    grupo: 'Grupo muscular',
    recientes: 'Recientes',
    nada: 'Nada con esos filtros.',
  },
  {
    todos: 'All',
    maquina: 'Machine',
    libre: 'Free weights',
    volver: 'Back',
    buscar: 'Search exercise',
    tipo: 'Type',
    grupo: 'Muscle group',
    recientes: 'Recent',
    nada: 'Nothing matches those filters.',
  },
)

export default function SelectorEjercicio(p: Props) {
  const tipos: { valor: FiltroEjercicios['equipo']; texto: string }[] = [
    { valor: 'todos', texto: t().todos },
    { valor: 'maquina', texto: t().maquina },
    { valor: 'libre', texto: t().libre },
  ]
  const [texto, setTexto] = useState('')
  const [equipo, setEquipo] = useState<FiltroEjercicios['equipo']>('todos')
  const [grupo, setGrupo] = useState('todos')

  const disponibles = useMemo(
    () => (p.excluir ? p.ejercicios.filter((e) => !p.excluir!.has(e.id)) : p.ejercicios),
    [p.ejercicios, p.excluir],
  )
  const filtrados = useMemo(() => filtrarEjercicios(disponibles, { texto, equipo, grupo }), [disponibles, texto, equipo, grupo])

  const sinFiltro = texto.trim() === '' && equipo === 'todos' && grupo === 'todos'
  const recientes = sinFiltro
    ? p.recientes.map((id) => disponibles.find((e) => e.id === id)).filter((e): e is Ejercicio => !!e)
    : []

  const grupos = useMemo(() => {
    const m = new Map<string, Ejercicio[]>()
    for (const e of filtrados) m.set(e.grupo, [...(m.get(e.grupo) ?? []), e])
    return [...m]
  }, [filtrados])

  const lista = (clave: string, titulo: string, items: Ejercicio[]) => (
    <section key={clave}>
      <h3>{titulo}</h3>
      <ul className="opciones">
        {items.map((e) => (
          <li key={e.id}>
            <button onClick={() => p.alElegir(e.id)}>{nombreEjercicio(e, p.nombres)}</button>
            {/* Hermano, no hijo: un button dentro de otro button es HTML inválido. */}
            <BotonFicha ejercicioId={e.id} nombre={nombreEjercicio(e, p.nombres)} />
          </li>
        ))}
      </ul>
    </section>
  )

  return (
    <div className="selector">
      <header>
        <button className="cerrar" onClick={p.alCerrar} aria-label={t().volver}>
          ←
        </button>
        <input
          type="search"
          placeholder={t().buscar}
          aria-label={t().buscar}
          autoFocus
          value={texto}
          onChange={(e) => setTexto(e.target.value)}
        />
      </header>
      <div className="chips" role="group" aria-label={t().tipo}>
        {tipos.map((t) => (
          <button key={t.valor} className={equipo === t.valor ? 'chip activa' : 'chip'} aria-pressed={equipo === t.valor} onClick={() => setEquipo(t.valor)}>
            {t.texto}
          </button>
        ))}
      </div>
      <div className="chips" role="group" aria-label={t().grupo}>
        <button className={grupo === 'todos' ? 'chip activa' : 'chip'} aria-pressed={grupo === 'todos'} onClick={() => setGrupo('todos')}>
          {t().todos}
        </button>
        {gruposDe(p.ejercicios).map((g) => (
          <button key={g} className={grupo === g ? 'chip activa' : 'chip'} aria-pressed={grupo === g} onClick={() => setGrupo(g)}>
            {etiquetaGrupo(g)}
          </button>
        ))}
      </div>
      {recientes.length > 0 && lista('recientes', t().recientes, recientes)}
      {grupos.map(([g, items]) => lista(g, etiquetaGrupo(g), items))}
      {filtrados.length === 0 && <p className="vacio">{t().nada}</p>}
    </div>
  )
}
