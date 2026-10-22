import { useEffect, useRef, useState } from 'react'
import { textos } from '../i18n'
import { mostrarBotonGoogle, type Sesion } from '../auth'

const t = textos(
  {
    fallo: 'No se pudo iniciar sesión. Revisa tu conexión.',
    aviso: 'Sin sesión: tus series se guardan aquí y se suben al reconectar.',
    reconectar: 'Reconectar',
  },
  {
    fallo: "Couldn't sign in. Check your connection.",
    aviso: 'Signed out: your sets are saved here and upload when you reconnect.',
    reconectar: 'Reconnect',
  },
)

/** Banda sin sesión: no tapa el entreno; las series siguen en la cola local. */
export default function Reconectar({ alEntrar }: { alEntrar: (s: Sesion) => void }) {
  const [intento, setIntento] = useState(0)
  const [error, setError] = useState<string | null>(null)
  const ref = useRef<HTMLDivElement>(null)
  // Ref para no reiniciar el botón si el padre pasa un alEntrar nuevo en cada render.
  const entrar = useRef(alEntrar)
  useEffect(() => {
    entrar.current = alEntrar
  })

  // Solo el botón de Google: One Tap tiene cooldown si el usuario lo cerró y la cookie ya no se recupera sola.
  useEffect(() => {
    if (intento === 0 || !ref.current) return
    let vivo = true
    mostrarBotonGoogle(ref.current, (s) => entrar.current(s), {
      oneTap: false,
      alFallar: () => vivo && setError(t().fallo),
    }).catch((e: Error) => vivo && setError(e.message))
    return () => {
      vivo = false
    }
  }, [intento])

  return (
    <div className="aviso sesion" role="status">
      <div>
        <span>{t().aviso}</span>
        {intento > 0 && <div ref={ref} />}
        {error && <p className="tenue">{error}</p>}
      </div>
      {/* Tras un error se puede reintentar (p. ej. GIS no cargó por la red). */}
      {(intento === 0 || error) && (
        <button
          type="button"
          onClick={() => {
            setError(null)
            setIntento((n) => n + 1)
          }}
        >
          {t().reconectar}
        </button>
      )}
    </div>
  )
}
