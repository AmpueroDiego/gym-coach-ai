import { renderToStaticMarkup } from 'react-dom/server'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { claveCoach } from '../coach'
import { fijarIdioma } from '../i18n'
import type { PlanDia } from '../api'
import Coach, { Pensando, PlanDelDia } from './Coach'

const HOY = '2026-10-09'

// Sin jsdom: localStorage falso con la conversación ya guardada; se prueba el marcado inicial.
const conConversacion = (mensajes: unknown[]) => {
  const m = new Map([[claveCoach('u1'), JSON.stringify({ fecha: HOY, mensajes })]])
  vi.stubGlobal('localStorage', { getItem: (k: string) => m.get(k) ?? null, setItem: (k: string, v: string) => void m.set(k, v) })
}

afterEach(() => vi.unstubAllGlobals())

const html = (conSesion = true) =>
  renderToStaticMarkup(
    <Coach sub="u1" conSesion={conSesion} hoy={HOY} nombre={(id) => id} autoPlan={false}
      alPlanVisto={() => {}} alEmpezarRutina={() => {}} perfil={<p>perfil</p>} />,
  )

describe('Pensando', () => {
  it('es un estado accesible con tres puntos y el texto de espera', () => {
    const h = renderToStaticMarkup(<Pensando segundos={0} />)
    expect(h).toContain('role="status"')
    expect(h).toContain("El coach está escribiendo")
    expect(h.match(/<i><\/i>/g)).toHaveLength(3)
    expect(h).toContain('Pensando…')
  })
  it('lleva la clase nuevo y data-pensando para la animación y las pruebas', () => {
    const h = renderToStaticMarkup(<Pensando segundos={0} />)
    expect(h).toContain('data-pensando="true"')
    expect(h).toMatch(/class="mensaje coach nuevo"/)
  })
})

describe('Coach', () => {
  it('el texto del coach es texto plano: el HTML sale escapado', () => {
    conConversacion([
      { rol: 'usuario', texto: 'hola', ts: '2026-10-09T10:00:00.000Z' },
      { rol: 'coach', texto: '<b>x</b>\n<img src=x onerror=alert(1)>', ts: '2026-10-09T10:00:01.000Z', herramientas: ['progreso', 'volumen_por_grupo'] },
    ])
    const h = html()
    expect(h).toContain('&lt;b&gt;x&lt;/b&gt;')
    expect(h).not.toContain('<b>')
    expect(h).not.toContain('<img')
    expect(h).toContain('Consultó: progreso, volumen por grupo')
    expect(h).toContain('Nueva conversación')
  })
  it('sin sesión el campo es de solo lectura (conserva el foco), Enviar deshabilitado y hay aviso', () => {
    const h = html(false)
    expect(h).toMatch(/<textarea[^>]*readOnly=""/)
    expect(h).not.toMatch(/<textarea[^>]*disabled=""/)
    expect(h).toMatch(/<button[^>]*aria-label="Enviar"[^>]*disabled=""[^>]*><svg/)
    expect(h).toContain('Sin sesión: reconecta para hablar con el coach.')
  })
  it('sin mensajes muestra las 3 sugerencias y no ofrece nueva conversación', () => {
    const h = html()
    expect(h.match(/class="chip"[^>]*>(Tu entreno de hoy|¿Cómo voy en press de banca\?|¿Estoy descuidando algún grupo muscular\?)</g)).toHaveLength(3)
    expect(h).not.toContain('Nueva conversación')
    expect(h).toMatch(/<textarea[^>]*maxLength="1000"/i)
    expect(h).toContain('enterKeyHint="send"')
  })
  it('la cabecera va antes de la lista que scrollea y el campo después, fuera de ella', () => {
    const h = html()
    const cabeza = h.indexOf('coach-cabeza')
    const lista = h.indexOf('class="coach-lista"')
    const redactar = h.indexOf('class="redactar"')
    expect(cabeza).toBeGreaterThan(-1)
    expect(lista).toBeGreaterThan(cabeza)
    expect(redactar).toBeGreaterThan(h.indexOf('</div>', h.indexOf('coach-relleno')))
  })
  it('coach-relleno es el último hijo de la lista y al inicio no hay ir-al-final', () => {
    const h = html()
    expect(h).toMatch(/<div class="coach-relleno"><\/div><\/div><\/div><form class="redactar"/)
    expect(h).not.toContain('ir-al-final')
  })
  it('los mensajes guardados no llevan la clase nuevo (no se animan al montar)', () => {
    conConversacion([
      { rol: 'usuario', texto: 'hola', ts: '2026-10-09T10:00:00.000Z' },
      { rol: 'coach', texto: 'toma', ts: '2026-10-09T10:00:01.000Z' },
    ])
    const h = html()
    expect(h).toContain('toma')
    expect(h).not.toMatch(/class="mensaje [^"]*nuevo/)
  })
  it('un mensaje fallido ofrece Reintentar y uno con rutina pinta la tarjeta', () => {
    conConversacion([
      { rol: 'usuario', texto: 'hola', ts: '2026-10-09T10:00:00.000Z', error: 'El coach no respondió. Reintenta.' },
      { rol: 'usuario', texto: 'rutina', ts: '2026-10-09T10:00:01.000Z' },
      { rol: 'coach', texto: 'toma', ts: '2026-10-09T10:00:02.000Z', rutina: {
        id: 'r1', nombre: 'Empuje', actualizada: '', minutosEstimados: 26,
        ejercicios: [{ id: 'press-banca', series: 4, reps: 8, pesoKg: null, descansoS: 90 }],
      } },
    ])
    const h = html()
    expect(h).toContain('El coach no respondió. Reintenta.')
    expect(h).toMatch(/<button[^>]*>Reintentar<\/button>/)
    expect(h).toContain('Rutina propuesta: Empuje')
  })
  it('un mensaje con programa pinta la tarjeta del programa', () => {
    const r = (id: string) => ({ id, nombre: 'Sesión ' + id, actualizada: '', minutosEstimados: 30, ejercicios: [{ id: 'press-banca', series: 3, reps: 10, pesoKg: null, descansoS: 60 }] })
    conConversacion([
      { rol: 'usuario', texto: 'programa', ts: '2026-10-09T10:00:01.000Z' },
      { rol: 'coach', texto: 'toma', ts: '2026-10-09T10:00:02.000Z', programa: { nombre: 'Full body', rutinas: [r('a'), r('b')] } },
    ])
    const h = html()
    expect(h).toContain('tarjeta-programa')
    expect(h).toContain('Programa propuesto: Full body')
  })
})

it('mensajes con el mismo ts se pintan sin choque de keys; sugerencias en un grupo', () => {
  const error = vi.spyOn(console, 'error').mockImplementation(() => {})
  conConversacion([
    { rol: 'usuario', texto: 'uno', ts: '2026-10-09T10:00:00.000Z' },
    { rol: 'usuario', texto: 'dos', ts: '2026-10-09T10:00:00.000Z' },
    { rol: 'coach', texto: 'tres', ts: '2026-10-09T10:00:00.000Z', herramientas: ['constructor'] },
  ])
  const h = html()
  expect(h).toContain('uno')
  expect(h).toContain('dos')
  expect(h).toContain('Consultó: constructor')
  expect(error).not.toHaveBeenCalled()
  error.mockRestore()
  vi.unstubAllGlobals()
  expect(html()).toContain('role="group" aria-label="Sugerencias"')
})

describe('PlanDelDia', () => {
  const rutina = {
    id: 'r1', nombre: 'Empuje', actualizada: '', minutosEstimados: 26,
    ejercicios: [{ id: 'press-banca', series: 4, reps: 8, pesoKg: null, descansoS: 90 }],
  }
  const plan = (o: Partial<PlanDia> = {}): PlanDia => ({
    fecha: HOY, numero: 1, restantes: 2, generando: false,
    actual: { texto: 'Hoy toca empuje.', herramientas: ['mis_ejercicios'], rutina, creado: '2026-10-09T13:00:00.000Z' },
    ...o,
  })
  const ver = (o: Partial<Parameters<typeof PlanDelDia>[0]> = {}) =>
    renderToStaticMarkup(
      <PlanDelDia plan={plan()} estado="listo" pidiendo={false} fallo={null} conSesion nombre={(id) => id}
        sub="u1" alEmpezar={() => {}} alOtraOpcion={() => {}} alReintentar={() => {}} {...o} />,
    )
  it('con plan pinta burbuja, tarjeta y "Otra opción (n)"', () => {
    const h = ver()
    expect(h).toContain('Hoy toca empuje.')
    expect(h).toContain('Consultó: tus ejercicios')
    expect(h).toContain('Rutina propuesta: Empuje')
    expect(h).toMatch(/Otra opción \(2\)/)
    expect(h).not.toMatch(/<button[^>]*disabled=""[^>]*>Otra opción/)
  })
  it('sin opciones restantes o generando, "Otra opción" queda deshabilitado', () => {
    expect(ver({ plan: plan({ restantes: 0 }) })).toMatch(/<button[^>]*disabled=""[^>]*>Otra opción/)
    expect(ver({ plan: plan({ generando: true }) })).toMatch(/<button[^>]*disabled=""[^>]*>Otra opción/)
    expect(ver({ pidiendo: true })).toContain('Preparando otra opción…')
  })
  it('sin plan y sin nada que decir no pinta bloque; generando lo anuncia', () => {
    expect(ver({ plan: null })).toBe('')
    expect(ver({ plan: plan({ actual: null, generando: true }) })).toContain('Preparando tu entreno de hoy…')
  })
  it('un fallo reintentable ofrece Reintentar y el tope no', () => {
    const h = ver({ plan: null, fallo: { texto: 'No se pudo preparar tu entreno de hoy.', reintentable: true, accion: 'leer' } })
    expect(h).toContain('No se pudo preparar tu entreno de hoy.')
    expect(h).toMatch(/<button[^>]*>Reintentar<\/button>/)
    const t = ver({ fallo: { texto: 'Ya usaste las 3 opciones de hoy.', reintentable: false, accion: 'otra' } })
    expect(t).toContain('Ya usaste las 3 opciones de hoy.')
    expect(t).not.toContain('Reintentar')
  })
  it('un plan sin rutina no pinta tarjeta', () => {
    const h = ver({ plan: plan({ actual: { texto: 'Hoy toca empuje.', herramientas: [], rutina: null, creado: 'x' } }) })
    expect(h).not.toContain('Rutina propuesta')
  })
})

it('Coach sin plan cargado muestra el chip del plan y ningún bloque', () => {
  const h = html()
  expect(h).not.toContain('class="plan-dia"')
  expect(h).toMatch(/class="chip"[^>]*>Tu entreno de hoy</)
})

describe('Coach en inglés', () => {
  afterEach(() => fijarIdioma('es'))
  it('textos en inglés y el chip del plan sigue siendo el del plan (por id, no por texto)', () => {
    fijarIdioma('en')
    const h = html()
    expect(h).toContain("Today&#x27;s workout")
    expect(h).toContain('Ask the coach')
    expect(h).toContain('aria-label="Suggestions"')
    expect(h).not.toContain('Pregúntale')
    // El chip del plan se deshabilita como el del plan (no como un mensaje): con sesión y sin pedir, habilitado.
    expect(h).toMatch(/<button class="chip">Today&#x27;s workout<\/button>/)
  })
  it('PlanDelDia y un fallo en inglés', () => {
    fijarIdioma('en')
    const h = renderToStaticMarkup(
      <PlanDelDia plan={null} estado="fallo" pidiendo={false} fallo={{ texto: 'x', reintentable: true, accion: 'leer' }}
        conSesion nombre={(id) => id} sub="u1" alEmpezar={() => {}} alOtraOpcion={() => {}} alReintentar={() => {}} />,
    )
    expect(h).toContain('Retry')
    expect(h).toContain('aria-label="Today&#x27;s workout"')
  })
})
