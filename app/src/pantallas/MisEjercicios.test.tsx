import { renderToStaticMarkup } from 'react-dom/server'
import { afterEach, describe, expect, it } from 'vitest'
import { fijarIdioma } from '../i18n'
import MisEjercicios, { ListaEjercicios } from './MisEjercicios'
import type { EjercicioHistorial } from '../api'

const grupos = ['pecho', 'espalda', 'biceps', 'triceps', 'cuadriceps']
const ej = (i: number): EjercicioHistorial => ({
  ejercicioId: `ej-${i}`,
  nombre: `Ejercicio ${i}`,
  grupo: grupos[i % grupos.length],
  equipo: 'libre',
  cargaCorporal: false,
  sesiones: 2,
  series: 6,
  primeraFecha: '2026-09-01',
  ultimaFecha: '2026-10-06',
  ultimaSerie: { pesoKg: 45, reps: 8, fecha: '2026-10-06' },
  mejorSerie: { pesoKg: 50, reps: 5, fecha: '2026-09-20' },
  e1rmMaxKg: 58,
})

describe('ListaEjercicios', () => {
  it('muestra los 23 ejercicios de un fixture, agrupados', () => {
    const html = renderToStaticMarkup(<ListaEjercicios ejercicios={Array.from({ length: 23 }, (_, i) => ej(i))} unidadDe={() => 'kg'} />)
    expect(html.match(/<li/g)).toHaveLength(23)
    expect(html.match(/<h3>/g)).toHaveLength(5)
    expect(html).toContain('<h3>Bíceps</h3>')
    expect(html).toContain('Última serie: 45 kg × 8')
    expect(html).toContain('Mejor serie: 50 kg × 5')
  })
  it('usa lb para el ejercicio que lo pide y kg para el resto', () => {
    const html = renderToStaticMarkup(<ListaEjercicios ejercicios={[ej(0), ej(1)]} unidadDe={(id) => (id === 'ej-0' ? 'lb' : 'kg')} />)
    expect(html).toContain('Última serie: 99.2 lb × 8')
    expect(html).toContain('Última serie: 45 kg × 8')
  })
  it('carga corporal sin lastre se muestra en reps', () => {
    const e = { ...ej(0), cargaCorporal: true, ultimaSerie: { pesoKg: 0, reps: 12, fecha: '2026-10-06' }, mejorSerie: null }
    const html = renderToStaticMarkup(<ListaEjercicios ejercicios={[e]} unidadDe={() => 'kg'} />)
    expect(html).toContain('Última serie: 12 reps')
    expect(html).not.toContain('Mejor serie')
  })
  it('los nombres del servidor van como texto, no como HTML', () => {
    const html = renderToStaticMarkup(<ListaEjercicios ejercicios={[{ ...ej(0), nombre: '<img src=x onerror=alert(1)>' }]} unidadDe={() => 'kg'} />)
    expect(html).not.toContain('<img')
    expect(html).toContain('&lt;img')
  })
  it('con ficha: mini silueta antes de los datos y ⓘ al final; sin ficha, ninguno', () => {
    const html = renderToStaticMarkup(
      <ListaEjercicios ejercicios={[{ ...ej(0), ejercicioId: 'prensa', nombre: 'Prensa' }, { ...ej(5), ejercicioId: 'no-existe' }]} unidadDe={() => 'kg'} />,
    )
    const [conFicha, sinFicha] = html.split('</li>')
    expect(conFicha.indexOf('<svg')).toBeGreaterThan(-1)
    expect(conFicha.indexOf('<svg')).toBeLessThan(conFicha.indexOf('Prensa</strong>'))
    expect(conFicha.indexOf('Prensa</strong>')).toBeLessThan(conFicha.indexOf('info-ejercicio'))
    expect(sinFicha).not.toContain('<svg')
    expect(sinFicha).not.toContain('info-ejercicio')
  })
  it('estado vacío', () => {
    expect(renderToStaticMarkup(<ListaEjercicios ejercicios={[]} unidadDe={() => 'kg'} />)).toContain('Aún no tienes ejercicios')
  })
})

describe('MisEjercicios', () => {
  it('sin sesión no pide nada y avisa', () => {
    expect(renderToStaticMarkup(<MisEjercicios sub="u1" conSesion={false} hoy="2026-10-07" unidadDe={() => 'kg'} />)).toContain('Reconecta')
  })
  it('con sesión empieza cargando', () => {
    expect(renderToStaticMarkup(<MisEjercicios sub="u1" conSesion hoy="2026-10-07" unidadDe={() => 'kg'} />)).toContain('Cargando')
  })
  it('en inglés traduce etiquetas y estado vacío', () => {
    fijarIdioma('en')
    const html = renderToStaticMarkup(<ListaEjercicios ejercicios={[ej(0)]} unidadDe={() => 'kg'} />)
    expect(html).toContain('Last set: 45 kg × 8')
    expect(html).toContain('Best set: 50 kg × 5')
    expect(html).toContain('2 sessions · last')
    expect(renderToStaticMarkup(<ListaEjercicios ejercicios={[]} unidadDe={() => 'kg'} />)).toContain('No exercises yet')
  })
  afterEach(() => fijarIdioma('es'))
})
