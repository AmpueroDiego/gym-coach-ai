import { useState } from 'react'
import { textos } from '../i18n'
import { fichaDe } from '../referencia/referencia'
import FichaEjercicio from './FichaEjercicio'
import Silueta from './Silueta'
import './ficha.css'

const t = textos({ ficha: 'Ficha de' }, { ficha: 'Details for' })

interface Props {
  ejercicioId: string
  nombre: string
  /** Silueta mini del músculo como contenido del botón, en vez del ⓘ. */
  conSilueta?: boolean
}

export default function BotonFicha({ ejercicioId, nombre, conSilueta }: Props) {
  const [abierta, setAbierta] = useState(false)
  const ficha = fichaDe(ejercicioId)
  if (!ficha) return null
  return (
    <>
      <button
        type="button"
        className={conSilueta ? 'silueta-boton' : 'info-ejercicio'}
        aria-label={`${t().ficha} ${nombre}`}
        onClick={(e) => {
          e.stopPropagation()
          setAbierta(true)
        }}
      >
        {conSilueta ? <Silueta principales={ficha.principales} secundarios={ficha.secundarios} mini /> : 'ⓘ'}
      </button>
      {abierta && <FichaEjercicio nombre={nombre} ficha={ficha} alCerrar={() => setAbierta(false)} />}
    </>
  )
}
