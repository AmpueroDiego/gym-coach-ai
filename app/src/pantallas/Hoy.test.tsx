import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import type { SerieLocal } from '../series'
import Hoy from './Hoy'

const serie = (ejercicioId: string): SerieLocal => ({
  id: 's1', ejercicioId, pesoKg: 100, reps: 5, fecha: '2026-01-01', ts: '2026-01-01T10:00:00Z', estado: 'ok',
})

// Sin plan: todo ejercicio cae en el bloque "fuera del plan".
const html = (ejercicioId: string) => {
  const s = [serie(ejercicioId)]
  return renderToStaticMarkup(
    <Hoy
      series={s} historico={s} ejercicios={new Map()} nombre={() => 'Prensa'} unidadDe={() => 'kg'}
      alCambiarUnidad={() => {}} plan={null} hoy="2026-01-01" enfocar={null} alRepetir={() => {}}
      alAgregarSerie={() => {}} alGuardarFila={() => {}} alMover={() => {}} alEditar={() => {}}
      alAgregarEjercicio={() => {}} alTerminar={() => {}} borradores={{}} alBorrador={() => {}}
    />,
  )
}

describe('Hoy: bloque fuera del plan', () => {
  it('con ficha: cabecera con h2 y la silueta como botón de la ficha', () => {
    const h = html('prensa')
    expect(h).toMatch(/<header class="cabeza-bloque"><h2>[^<]*<\/h2><button type="button" class="silueta-boton"/)
  })
  it('sin ficha: no hay botón de silueta', () => {
    expect(html('no-existe')).not.toContain('silueta-boton')
  })
})
