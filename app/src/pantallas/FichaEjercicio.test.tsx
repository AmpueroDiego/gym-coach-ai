import { renderToStaticMarkup } from 'react-dom/server'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { fijarIdioma } from '../i18n'
import { fichaDe, urlImagen, type FichaReferencia } from '../referencia/referencia'
import FichaEjercicio from './FichaEjercicio'

const base = fichaDe('prensa') as FichaReferencia
const html = (f: FichaReferencia) => renderToStaticMarkup(<FichaEjercicio nombre="Prensa" ficha={f} alCerrar={() => {}} />)
// renderToStaticMarkup escapa el texto; las pruebas comparan contra lo escapado.
const esc = (s: string) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#x27;')

describe('FichaEjercicio', () => {
  it('es un diálogo con nombre, cierre, fotos y pasos', () => {
    const h = html(base)
    expect(h).toContain('role="dialog"')
    expect(h).toContain('aria-label="Prensa"')
    expect(h).toContain('aria-label="Cerrar"')
    expect(h).toContain('alt="Inicio del movimiento"')
    expect(h).toContain('alt="Fin del movimiento"')
    expect(h).toContain(esc(urlImagen(base.imagenes[0])))
    expect(h).toContain('crossorigin="anonymous"')
    expect(h).toContain(esc(`Equipo: ${base.equipo}`))
    expect(h).toContain('Fotos: free-exercise-db')
    for (const p of base.pasos) expect(h).toContain(esc(p))
  })

  it('sin window las fotos van lado a lado (sin animar)', () => {
    expect(html(base)).toContain('fotos lado')
  })

  it('con movimiento permitido hay botón de pausa que cruza las fotos; con reduced-motion no', () => {
    const con = (reduce: boolean) => vi.stubGlobal('window', { matchMedia: () => ({ matches: reduce }) })
    con(false)
    const h = html(base)
    expect(h).toContain('fotos animada')
    expect(h).toContain('type="button" class="marco" aria-pressed="false" aria-label="Pausar animación"')
    expect(h).not.toContain('class="play"')
    con(true)
    const q = html(base)
    expect(q).toContain('fotos lado')
    expect(q).not.toContain('marco')
    expect(q).not.toContain('Pausar')
    vi.unstubAllGlobals()
  })

  it('la nota de foto aproximada solo sale si aplica', () => {
    const nota = 'Foto aproximada: la máquina de tu gym puede verse distinta.'
    expect(html(base)).not.toContain(nota)
    expect(html({ ...base, aproximada: true })).toContain(nota)
  })

  it('incluye la silueta', () => {
    expect(html(base)).toContain('<svg')
  })
})

describe('FichaEjercicio en inglés', () => {
  afterEach(() => fijarIdioma('es'))

  it('traduce textos, músculos y pasos', () => {
    fijarIdioma('en')
    const h = html(base)
    expect(h).toContain('alt="Start of the movement"')
    expect(h).toContain('aria-label="Close"')
    vi.stubGlobal('window', { matchMedia: () => ({ matches: false }) })
    expect(html(base)).toContain('aria-label="Pause animation"')
    vi.unstubAllGlobals()
    expect(h).toContain(esc(`Equipment: ${base.equipoEn}`))
    expect(h).toContain('Primary: Quads')
    for (const p of base.pasosEn) expect(h).toContain(esc(p))
    expect(h).not.toContain('Equipo:')
    expect(html({ ...base, aproximada: true })).toContain('Approximate photo')
  })

  it('sin pasosEn muestra los pasos en español', () => {
    fijarIdioma('en')
    const h = html({ ...base, pasosEn: [] })
    for (const p of base.pasos) expect(h).toContain(esc(p))
  })
})
