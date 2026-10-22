import type { ReactNode } from 'react'
import { textos } from '../i18n'
import './navegacion.css'

export type Pestana = 'entreno' | 'rutinas' | 'progreso' | 'coach' | 'mas'

interface Props {
  activa: Pestana
  alElegir: (p: Pestana) => void
  /** Selector de ejercicio o editor de rutinas abiertos: sin barra. */
  oculta?: boolean
  /** Asesoría de hoy sin hacer: punto en la pestaña Coach. */
  avisoCoach?: boolean
}

const Icono = ({ children }: { children: ReactNode }) => (
  <svg viewBox="0 0 24 24" aria-hidden="true">
    {children}
  </svg>
)

const t = textos(
  { secciones: 'Secciones', entreno: 'Entreno', rutinas: 'Rutinas', progreso: 'Progreso', coach: 'Coach', mas: 'Más', coachPendiente: 'Coach, asesoría pendiente' },
  { secciones: 'Sections', entreno: 'Workout', rutinas: 'Routines', progreso: 'Progress', coach: 'Coach', mas: 'More', coachPendiente: 'Coach, check-in pending' },
)

const PESTANAS: { clave: Pestana; icono: ReactNode }[] = [
  { clave: 'entreno', icono: <Icono><path d="M6 8v8M18 8v8M3 10v4M21 10v4M6 12h12" /></Icono> },
  { clave: 'rutinas', icono: <Icono><path d="M8 6h12M8 12h12M8 18h12M4 6h.01M4 12h.01M4 18h.01" /></Icono> },
  { clave: 'progreso', icono: <Icono><path d="M3 17l6-6 4 4 8-8M15 7h6v6" /></Icono> },
  { clave: 'coach', icono: <Icono><path d="M4 5h16v11H8l-4 4z" /></Icono> },
  { clave: 'mas', icono: <Icono><path d="M5 12h.01M12 12h.01M19 12h.01" /></Icono> },
]

export default function Navegacion({ activa, alElegir, oculta, avisoCoach }: Props) {
  if (oculta) return null
  return (
    <nav className="barra-nav" aria-label={t().secciones}>
      {PESTANAS.map((p) => (
        <button
          key={p.clave}
          aria-current={p.clave === activa ? 'page' : undefined}
          aria-label={p.clave === 'coach' && avisoCoach ? t().coachPendiente : undefined}
          onClick={() => alElegir(p.clave)}
        >
          {p.icono}
          <span>{t()[p.clave]}</span>
          {p.clave === 'coach' && avisoCoach && <i className="punto-aviso" aria-hidden="true" />}
        </button>
      ))}
    </nav>
  )
}
