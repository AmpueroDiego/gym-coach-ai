import { renderToStaticMarkup } from 'react-dom/server'
import { afterEach, describe, expect, it } from 'vitest'
import { fijarIdioma } from '../i18n'
import type { ParaCoach } from '../api'
import PerfilEntrenamiento, { VistaParaCoach } from './PerfilEntrenamiento'

const marca = { pesoKg: 80, reps: 8, fecha: '2026-10-06' }
const p: ParaCoach = {
  calculadoEl: '2026-10-07T12:00:00Z',
  seriesAnalizadas: 100,
  truncado: false,
  primerEntreno: '2026-09-01',
  ultimoEntreno: '2026-10-06',
  diasDesdeUltimo: 1,
  diasEntrenados: 12,
  historialSuficiente: true,
  habitos: {
    diasPorSemana: 3.5,
    division: [{ grupos: ['pecho', 'triceps'], veces: 5 }],
    rutinasFrecuentes: [{ nombre: '<script>alert(1)</script>', veces: 4 }],
    horario: { franja: 'tarde', horaTipica: '18:00', sesiones: 9 },
    duracionTipicaMin: 55,
    seriesPorSesion: 18,
  },
  equipoPreferido: 'libre',
  equipoOrigen: 'historial',
  pctSeriesMaquina: 20,
  principales: [{ ejercicioId: 'ej-1', nombre: '<b>Banca</b>', sesiones: 3 }],
  tendencias: [{ ejercicioId: 'ej-1', nombre: 'Banca', estado: 'progreso', reciente: marca, anterior: null, cambioPct: 5.2 }],
}

describe('VistaParaCoach', () => {
  const html = renderToStaticMarkup(<VistaParaCoach p={p} json={JSON.stringify(p, null, 2)} />)
  it('pinta los campos y el calculado', () => {
    expect(html).toContain('3.5 días por semana')
    expect(html).toContain('Pecho + Tríceps · 5 veces')
    expect(html).toContain('Tarde, hacia las 18:00 (9 sesiones)')
    expect(html).toContain('Peso libre (deducido de tus series, 20 % en máquina)')
    expect(html).toContain('Progreso +5.2 %')
    expect(html).toContain('Calculado el')
    expect(html).toContain('<details>')
  })
  it('los textos del servidor van como texto, no como HTML', () => {
    expect(html).not.toContain('<script>')
    expect(html).not.toContain('<b>Banca')
    expect(html).toContain('&lt;script&gt;')
  })
  it('avisa si hay poco historial', () => {
    expect(renderToStaticMarkup(<VistaParaCoach p={{ ...p, historialSuficiente: false }} json="{}" />)).toContain('poco historial')
  })
})

describe('PerfilEntrenamiento', () => {
  it('sin sesión avisa y con sesión empieza cargando', () => {
    expect(renderToStaticMarkup(<PerfilEntrenamiento sub="u1" conSesion={false} hoy="2026-10-07" />)).toContain('Reconecta')
    expect(renderToStaticMarkup(<PerfilEntrenamiento sub="u1" conSesion hoy="2026-10-07" />)).toContain('Cargando')
  })
})

describe('VistaParaCoach en inglés', () => {
  afterEach(() => fijarIdioma('es'))
  it('pinta en inglés y no deja español', () => {
    fijarIdioma('en')
    const html = renderToStaticMarkup(<VistaParaCoach p={p} json="{}" />)
    expect(html).toContain('3.5 days per week')
    expect(html).toContain('Afternoon, around 18:00 (9 sessions)')
    expect(html).toContain('Free weights (inferred from your sets, 20 % on machines)')
    expect(html).toContain('See exactly what the coach receives')
    expect(html).not.toContain('Entrenos')
    expect(renderToStaticMarkup(<PerfilEntrenamiento sub="u" conSesion={false} hoy="2026-10-06" />)).toContain('Reconnect to see what it knows about you.')
  })
})
