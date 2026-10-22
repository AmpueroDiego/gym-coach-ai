import { renderToStaticMarkup } from 'react-dom/server'
import { afterEach, describe, expect, it } from 'vitest'
import { fijarIdioma } from '../i18n'
import PerfilCoach from './PerfilCoach'

describe('PerfilCoach', () => {
  it('renderiza el formulario vacío con sesión, sin campo de hora', () => {
    const html = renderToStaticMarkup(<PerfilCoach sub="u1" conSesion />)
    expect(html).toContain('Minutos disponibles')
    expect(html).toContain('Altura (cm)')
    expect(html).toContain('Peso (kg)')
    expect(html).toContain('inputMode="numeric"')
    expect(html).toContain('inputMode="decimal"')
    expect(html).toContain('Objetivo')
    expect(html).toContain('Nueva nota')
    expect(html).toContain('Guardar perfil')
    expect(html).toContain('Equipo preferido')
    for (const t of ['Máquinas', 'Peso libre', 'Mixto', 'Que lo deduzca']) expect(html).toContain(t)
    expect(html).toContain('Días por semana')
    expect(html).toContain('Nunca he entrenado')
    expect(html).toContain('Lo que ve el coach')
    expect(html).not.toContain('type="time"')
  })
  it('sin sesión deshabilita guardar y avisa', () => {
    const html = renderToStaticMarkup(<PerfilCoach sub="u1" conSesion={false} />)
    expect(html).toContain('Reconecta para editar')
    expect(html).toContain('Reconecta para ver lo que sabe de ti')
    expect(html).toMatch(/<button[^>]*disabled=""[^>]*>Guardar perfil/)
  })
})

describe('PerfilCoach en inglés', () => {
  afterEach(() => fijarIdioma('es'))
  it('etiquetas, opciones de equipo y botón en inglés', () => {
    fijarIdioma('en')
    const html = renderToStaticMarkup(<PerfilCoach sub="u1" conSesion />)
    for (const t of ['Days per week', 'Never trained', 'Minutes available per workout', 'Height (cm)', 'Preferred equipment', 'Machines', 'Let it figure out', 'Save profile']) expect(html).toContain(t)
    expect(html).not.toContain('Guardar perfil')
  })
})
