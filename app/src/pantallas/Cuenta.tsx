import { useState } from 'react'
import { textos } from '../i18n'

const t = textos(
  {
    sinNombre: 'Sin nombre',
    sinSubir: (n: number) =>
      n === 1
        ? '1 serie sin subir queda en este dispositivo hasta que vuelvas a entrar'
        : `${n} series sin subir quedan en este dispositivo hasta que vuelvas a entrar`,
    seguro: '¿Seguro? Toca para salir',
    cerrar: 'Cerrar sesión',
  },
  {
    sinNombre: 'No name',
    sinSubir: (n) =>
      n === 1
        ? '1 unsent set stays on this device until you sign in again'
        : `${n} unsent sets stay on this device until you sign in again`,
    seguro: 'Sure? Tap to sign out',
    cerrar: 'Sign out',
  },
)

interface Props {
  nombre: string | null
  foto: string | null
  /** Series en la cola local: se pierden de vista si sale y entra con otra cuenta. */
  pendientes: number
  alSalir: () => void
}

// Tarjeta visible en Más: quién eres y cómo salir, sin menús escondidos.
export default function Cuenta(p: Props) {
  // Dos toques para salir: el botón está a un dedo de los de registrar.
  const [confirmar, setConfirmar] = useState(false)
  // URL que falló: una foto nueva merece otro intento aunque la anterior fallara.
  const [fotoRota, setFotoRota] = useState<string | null>(null)

  const inicial = p.nombre ? Array.from(p.nombre)[0].toUpperCase() : '?'

  return (
    <div className="bloque tarjeta-cuenta">
      <div className="quien">
        <span className="avatar" aria-hidden="true">
          {p.foto && fotoRota !== p.foto ? (
            <img src={p.foto} alt="" width={40} height={40} referrerPolicy="no-referrer" onError={() => setFotoRota(p.foto)} />
          ) : (
            <span>{inicial}</span>
          )}
        </span>
        <p className="nombre">{p.nombre ?? t().sinNombre}</p>
      </div>
      {p.pendientes > 0 && (
        <p className="aviso">
          {t().sinSubir(p.pendientes)}
        </p>
      )}
      <button
        className={confirmar ? 'peligro ancho' : 'ancho'}
        aria-live="polite"
        onClick={() => (confirmar ? p.alSalir() : setConfirmar(true))}
        onBlur={() => setConfirmar(false)}
      >
        {confirmar ? t().seguro : t().cerrar}
      </button>
    </div>
  )
}
