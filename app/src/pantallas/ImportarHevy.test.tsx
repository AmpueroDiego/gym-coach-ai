import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import Mas from './Mas'

describe('Mas', () => {
  it('muestra la sección Datos solo si recibe datos', () => {
    const con = renderToStaticMarkup(<Mas configuracion={null} cuenta={null} datos={<p>x</p>} />)
    expect(con).toContain('>Datos</h2>')
    expect(renderToStaticMarkup(<Mas configuracion={null} cuenta={null} />)).not.toContain('Datos')
  })
})
