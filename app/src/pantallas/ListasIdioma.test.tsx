import { renderToStaticMarkup } from 'react-dom/server'
import { afterEach, describe, expect, it } from 'vitest'
import { fijarIdioma } from '../i18n'
import GraficoVolumen from './GraficoVolumen'
import Historial from './Historial'
import Progreso from './Progreso'
import Records from './Records'
import Rutinas from './Rutinas'

describe('pantallas de rutinas, historial y progreso en inglés', () => {
  afterEach(() => fijarIdioma('es'))

  it('Progreso: pestañas', () => {
    fijarIdioma('en')
    const h = renderToStaticMarkup(<Progreso historial={null} records={null} />)
    expect(h).toContain('>History<')
    expect(h).toContain('>Records<')
    expect(h).toContain('aria-label="Progress view"')
  })

  it('Records: vacío y lista', () => {
    fijarIdioma('en')
    expect(renderToStaticMarkup(<Records records={[]} cargando={false} />)).toContain('No records yet')
    const h = renderToStaticMarkup(
      <Records
        cargando={false}
        records={[{ ejercicioId: 'a', nombre: 'Bench', pesoMax: { texto: '100 kg', reps: 5, fecha: '2026-10-06' }, e1rm: { texto: '116 kg' }, repsMax: { reps: 12, texto: '60 kg', fecha: '2026-10-06' } }]}
      />,
    )
    expect(h).toContain('Max weight')
    expect(h).toContain('Estimated 1RM')
    expect(h).toContain('12 reps (60 kg)')
    expect(h).toContain('Tue Oct 6')
  })

  it('Historial: vacío', () => {
    fijarIdioma('en')
    const h = renderToStaticMarkup(
      <Historial series={[]} ejercicios={new Map()} nombre={(i) => i} unidadDe={() => 'kg'} hoy="2026-10-06" dias={30} alEditar={() => {}} />,
    )
    expect(h).toContain('No workouts in the last 30 days.')
  })

  it('GraficoVolumen: aria', () => {
    fijarIdioma('en')
    const h = renderToStaticMarkup(<GraficoVolumen puntos={[{ fecha: '2026-10-05', valor: 10 }, { fecha: '2026-10-06', valor: 20 }]} etiqueta="kg" />)
    expect(h).toContain('Volume last 2 days')
  })

  it('Rutinas sin sesión ni caché', () => {
    fijarIdioma('en')
    const h = renderToStaticMarkup(
      <Rutinas sub="u" conSesion={false} ejercicios={[]} nombres="app" nombre={(i) => i} unidadDe={() => 'kg'} recientes={[]} alEmpezar={() => {}} />,
    )
    expect(h).toContain('Reconnect to see your routines.')
  })
})
