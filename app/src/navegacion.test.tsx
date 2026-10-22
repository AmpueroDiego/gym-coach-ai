import { renderToStaticMarkup } from 'react-dom/server'
import { afterEach, describe, expect, it } from 'vitest'
import { fijarIdioma } from './i18n'
import Navegacion from './pantallas/Navegacion'
import Progreso from './pantallas/Progreso'
import Records, { type RecordVista } from './pantallas/Records'

const rec = (id: string, nombre: string): RecordVista => ({
  ejercicioId: id,
  nombre,
  pesoMax: { texto: '100 kg', reps: 5, fecha: '2026-10-05' },
  e1rm: null,
  repsMax: { reps: 12, texto: '60 kg', fecha: '2026-10-01' },
})

afterEach(() => fijarIdioma('es'))

describe('Navegacion', () => {
  it('en inglés traduce pestañas y aviso', () => {
    fijarIdioma('en')
    const html = renderToStaticMarkup(<Navegacion activa="entreno" alElegir={() => {}} avisoCoach />)
    expect(html).toContain('Workout')
    expect(html).toContain('More')
    expect(html).toContain('aria-label="Coach, check-in pending"')
    expect(html).not.toContain('Entreno')
  })
  it('marca solo la pestaña activa', () => {
    const html = renderToStaticMarkup(<Navegacion activa="progreso" alElegir={() => {}} />)
    expect(html.match(/aria-current="page"/g)).toHaveLength(1)
    expect(html).toMatch(/aria-current="page"[^>]*>.*?Progreso/)
  })
  it('tiene cinco pestañas con Coach entre Progreso y Más', () => {
    const html = renderToStaticMarkup(<Navegacion activa="entreno" alElegir={() => {}} />)
    expect(html.match(/<button/g)).toHaveLength(5)
    expect(html.indexOf('Progreso')).toBeLessThan(html.indexOf('Coach'))
    expect(html.indexOf('Coach')).toBeLessThan(html.indexOf('Más'))
  })
  it('el aviso del coach solo sale si está pendiente', () => {
    const sin = renderToStaticMarkup(<Navegacion activa="entreno" alElegir={() => {}} />)
    const con = renderToStaticMarkup(<Navegacion activa="entreno" alElegir={() => {}} avisoCoach />)
    expect(sin).not.toContain('asesoría pendiente')
    expect(con).toContain('aria-label="Coach, asesoría pendiente"')
  })
  it('no renderiza si está oculta', () => {
    expect(renderToStaticMarkup(<Navegacion activa="entreno" alElegir={() => {}} oculta />)).toBe('')
  })
})

describe('Progreso', () => {
  it('abre en Historial', () => {
    const html = renderToStaticMarkup(<Progreso historial={<p>HIST</p>} records={<p>REC</p>} />)
    expect(html).toContain('HIST')
    expect(html).not.toContain('REC<')
  })
})

describe('Progreso con vistas extra', () => {
  it('suma la pestaña Ejercicios después de Récords sin cambiar la vista inicial', () => {
    const html = renderToStaticMarkup(
      <Progreso historial={<p>HIST</p>} records={<p>REC</p>} extra={[{ clave: 'ejercicios', titulo: 'Ejercicios', nodo: <p>EJ</p> }]} />,
    )
    expect(html.match(/role="tab"/g)).toHaveLength(3)
    expect(html.indexOf('Récords')).toBeLessThan(html.indexOf('Ejercicios'))
    expect(html).toContain('HIST')
    expect(html).not.toContain('EJ<')
  })
})

describe('Records', () => {
  it('muestra vacío, cargando y error', () => {
    expect(renderToStaticMarkup(<Records records={[]} cargando={false} />)).toContain('Aún no tienes récords')
    expect(renderToStaticMarkup(<Records records={[]} cargando />)).toContain('Cargando')
    expect(renderToStaticMarkup(<Records records={[]} cargando={false} error="Falló" />)).toContain('Falló')
  })
  it('ordena por nombre', () => {
    const html = renderToStaticMarkup(<Records records={[rec('b', 'Sentadilla'), rec('a', 'Banca')]} cargando={false} />)
    expect(html.indexOf('Banca')).toBeLessThan(html.indexOf('Sentadilla'))
  })
})
