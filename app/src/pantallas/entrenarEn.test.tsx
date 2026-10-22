import { renderToStaticMarkup } from 'react-dom/server'
import { afterEach, describe, expect, it } from 'vitest'
import type { Ejercicio } from '../api'
import { fijarIdioma } from '../i18n'
import { nombreTipo, tiposSerie } from '../tiposSerie'
import type { Importacion } from '../useImportacionHevy'
import HojaSerie from './HojaSerie'
import Hoy from './Hoy'
import ImportarHevy from './ImportarHevy'
import { BarraDescanso } from './Relojes'
import SelectorEjercicio from './SelectorEjercicio'
import { AccionesActualizar } from './TerminarRutina'

const ej: Ejercicio = { id: 'prensa', nombre: 'Prensa', nombreEn: 'Leg Press', grupo: 'cuadriceps', equipo: 'maquina' }

afterEach(() => fijarIdioma('es'))

describe('Entrenar en inglés', () => {
  it('tipos de serie', () => {
    expect(nombreTipo('calentamiento')).toBe('Calentamiento')
    fijarIdioma('en')
    expect(tiposSerie().map((t) => t.nombre)).toEqual(['Normal', 'Warm-up', 'Failure', 'Drop set'])
    expect(nombreTipo('fallo')).toBe('Failure')
    expect(nombreTipo(undefined)).toBe('Normal')
  })

  it('Hoy sin series', () => {
    const props = {
      series: [], historico: [], ejercicios: new Map(), nombre: (id: string) => id, unidadDe: () => 'kg' as const,
      alCambiarUnidad: () => {}, plan: null, hoy: '2026-10-16', enfocar: null, alRepetir: () => {}, alAgregarSerie: () => {},
      alGuardarFila: () => {}, alMover: () => {}, alEditar: () => {}, alAgregarEjercicio: () => {}, alTerminar: () => {},
      borradores: {}, alBorrador: () => {},
    }
    expect(renderToStaticMarkup(<Hoy {...props} />)).toContain('Sin series hoy')
    fijarIdioma('en')
    const html = renderToStaticMarkup(<Hoy {...props} />)
    expect(html).toContain('No sets today')
    expect(html).toContain('Add exercise')
    expect(html).not.toContain('Agregar')
  })

  it('Hoja de la serie', () => {
    fijarIdioma('en')
    const html = renderToStaticMarkup(
      <HojaSerie ejercicio={ej} nombre="Leg Press" unidad="kg" alCambiarUnidad={() => {}} locales={[]} hoy="2026-10-16" sub="u" conSesion={false}
        alGuardar={() => {}} alBorrar={() => {}} alCerrar={() => {}} />,
    )
    expect(html).toContain('Warm-up')
    expect(html).toContain('Save')
    expect(html).toContain('Set type')
    expect(html).toContain('Loading history')
    expect(html).not.toContain('Guardar')
  })

  it('barra de descanso', () => {
    fijarIdioma('en')
    const html = renderToStaticMarkup(
      <BarraDescanso descanso={{ ejercicioId: 'prensa', inicio: Date.now(), duracionS: 90 }} alCambiar={() => {}} alSaltar={() => {}} alFin={() => {}} />,
    )
    expect(html).toContain('Skip')
    expect(html).toContain('Subtract 15 seconds')
    expect(html).not.toContain('Saltar')
  })

  it('selector de ejercicio', () => {
    fijarIdioma('en')
    const html = renderToStaticMarkup(
      <SelectorEjercicio ejercicios={[ej]} nombres="app" recientes={[]} alElegir={() => {}} alCerrar={() => {}} />,
    )
    expect(html).toContain('Search exercise')
    expect(html).toContain('Free weights')
    expect(html).not.toContain('Buscar')
  })

  it('importar de Hevy', () => {
    fijarIdioma('en')
    const imp = { fase: { f: 'inicio' }, cancelando: false, esperandoServidor: false } as unknown as Importacion
    expect(renderToStaticMarkup(<ImportarHevy imp={imp} />)).toContain('Import from Hevy')
    const listo = {
      fase: { f: 'listo', guardadas: 1, existentes: 2, rechazadas: 0, pendientes: 0, actualizados: 0 }, cancelando: false, esperandoServidor: false,
    } as unknown as Importacion
    expect(renderToStaticMarkup(<ImportarHevy imp={listo} />)).toContain('1 new set, 2 already there. Records updated.')
  })

  it('rutina perdida: el botón primario y la ausencia de Reintentar no dependen del idioma', () => {
    const ver = () =>
      renderToStaticMarkup(<AccionesActualizar error="perdida" ocupado={false} haceAlgo alActualizar={() => {}} alTerminar={() => {}} />)
    const es = ver()
    expect(es).toContain('La rutina ya no existe')
    expect(es).toContain('<button class="primario ancho" disabled="">Terminar sin actualizar</button>'.replace(' disabled=""', ''))
    expect(es).not.toContain('Reintentar')
    fijarIdioma('en')
    const en = ver()
    expect(en).toContain('no longer exists')
    expect(en).toContain('<button class="primario ancho">Finish without updating</button>')
    expect(en).not.toContain('Retry')
    expect(en).not.toContain('Yes, update')
  })

  it('error del tope: se traduce al pintar', () => {
    fijarIdioma('en')
    const html = renderToStaticMarkup(
      <AccionesActualizar error={{ error: 'demasiados', n: 16, max: 15 }} ocupado={false} haceAlgo alActualizar={() => {}} alTerminar={() => {}} />,
    )
    expect(html).toContain('16 exercises; the maximum is 15')
    expect(html).toContain('Retry')
  })
})
