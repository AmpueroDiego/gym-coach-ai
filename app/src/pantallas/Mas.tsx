import type { ReactNode } from 'react'
import { textos } from '../i18n'

const t = textos(
  { configuracion: 'Configuración', datos: 'Datos', cuenta: 'Cuenta' },
  { configuracion: 'Settings', datos: 'Data', cuenta: 'Account' },
)

interface Props {
  configuracion: ReactNode
  cuenta: ReactNode
  datos?: ReactNode
}

export default function Mas(p: Props) {
  return (
    <div className="mas">
      <section aria-labelledby="mas-configuracion">
        <h2 id="mas-configuracion">{t().configuracion}</h2>
        {p.configuracion}
      </section>
      {p.datos && (
        <section aria-labelledby="mas-datos">
          <h2 id="mas-datos">{t().datos}</h2>
          {p.datos}
        </section>
      )}
      <section aria-labelledby="mas-cuenta">
        <h2 id="mas-cuenta">{t().cuenta}</h2>
        {p.cuenta}
      </section>
    </div>
  )
}
