import { afterEach, describe, expect, it, vi } from 'vitest'
import { marcarSinSesion } from './auth'
import {
  borrarRutina,
  borrarSerie,
  DemasiadasPeticiones,
  esperaRetryAfter,
  EVENTO_NO_AUTENTICADO,
  guardarPerfilCoach,
  guardarRutina,
  guardarSerie,
  leerPerfilCoach,
  listarEjercicios,
  listarRecords,
  NoAutenticado,
  pedirPlanDia,
  preguntarCoach,
} from './api'
import { fijarIdioma } from './i18n'

// Estado mutable: simula que la sesión cambia mientras una petición vuela.
const s = vi.hoisted(() => ({
  estado: 'activa' as 'desconocida' | 'activa' | 'ausente',
  sub: 'B' as string | null,
  generacion: 1,
  marca: true,
}))

vi.mock('./auth', () => ({
  estadoSesion: () => ({ estado: s.estado, sub: s.sub, generacion: s.generacion }),
  marcarSinSesion: vi.fn(() => s.marca),
}))

const ok = (cuerpo: unknown = {}) => vi.fn().mockResolvedValue({ ok: true, status: 200, json: async () => cuerpo })
const ventana = () => {
  const w = { dispatchEvent: vi.fn() }
  vi.stubGlobal('window', w)
  return w
}

afterEach(() => {
  vi.unstubAllGlobals()
  fijarIdioma('es')
  vi.mocked(marcarSinSesion).mockClear()
  Object.assign(s, { estado: 'activa', sub: 'B', generacion: 1, marca: true })
})

describe('cabeceras', () => {
  it('cookie y X-Requested-With también en GET; nunca Authorization ni X-Custom-Authorization', async () => {
    const fetchFalso = ok([])
    vi.stubGlobal('fetch', fetchFalso)
    await listarEjercicios()
    const init = fetchFalso.mock.calls[0][1]
    expect(init.credentials).toBe('same-origin')
    expect(init.headers['X-Requested-With']).toBe('gym-coach')
    expect(init.headers).not.toHaveProperty('Authorization')
    expect(init.headers).not.toHaveProperty('X-Custom-Authorization')
    expect(init.headers).not.toHaveProperty('X-Gc-Sub')
  })

  it('X-Gc-Sub con el dueño, sin perder las cabeceras de init', async () => {
    const fetchFalso = ok()
    vi.stubGlobal('fetch', fetchFalso)
    await guardarRutina({ id: 'r1', nombre: 'Push', ejercicios: [] }, 'B')
    const { headers, credentials } = fetchFalso.mock.calls[0][1]
    expect(headers['X-Gc-Sub']).toBe('B')
    expect(headers['X-Requested-With']).toBe('gym-coach')
    expect(headers['Content-Type']).toBe('application/json')
    expect(credentials).toBe('same-origin')
  })

  it('sesión sin verificar (sub desconocido): envía igual y la API decide con X-Gc-Sub', async () => {
    Object.assign(s, { estado: 'desconocida', sub: null })
    const fetchFalso = ok([])
    vi.stubGlobal('fetch', fetchFalso)
    await expect(listarRecords('A')).resolves.toEqual([])
    expect(fetchFalso.mock.calls[0][1].headers['X-Gc-Sub']).toBe('A')
  })
})

describe('sin sesión', () => {
  it("'ausente': NoAutenticado sin llamar a fetch y avisa", async () => {
    const w = ventana()
    const fetchFalso = vi.fn()
    vi.stubGlobal('fetch', fetchFalso)
    s.estado = 'ausente'
    await expect(listarRecords()).rejects.toBeInstanceOf(NoAutenticado)
    await expect(listarRecords('B')).rejects.toBeInstanceOf(NoAutenticado)
    expect(fetchFalso).not.toHaveBeenCalled()
    expect(w.dispatchEvent).toHaveBeenCalledTimes(2)
    expect(w.dispatchEvent.mock.calls[0][0].type).toBe(EVENTO_NO_AUTENTICADO)
  })

  it('operación de la cuenta A con la sesión de B: no se envía ni avisa', async () => {
    const w = ventana()
    const fetchFalso = vi.fn()
    vi.stubGlobal('fetch', fetchFalso)
    const serie = { id: 'a', ejercicioId: 'x', pesoKg: 1, reps: 1, fecha: '2026-10-06', ts: '2026-10-06T12:00:00Z' }
    await expect(guardarSerie(serie, 'A')).rejects.toBeInstanceOf(NoAutenticado)
    expect(fetchFalso).not.toHaveBeenCalled()
    expect(w.dispatchEvent).not.toHaveBeenCalled()
  })
})

describe('401', () => {
  const r401 = { ok: false, status: 401, headers: new Headers() }

  it('marca sin sesión con la generación de antes del fetch y avisa', async () => {
    const w = ventana()
    vi.stubGlobal('fetch', vi.fn().mockImplementation(async () => {
      s.generacion = 2 // el usuario volvió a entrar mientras volaba
      return r401
    }))
    await expect(listarRecords('B')).rejects.toBeInstanceOf(NoAutenticado)
    expect(marcarSinSesion).toHaveBeenCalledWith(1)
    expect(w.dispatchEvent.mock.calls[0][0].type).toBe(EVENTO_NO_AUTENTICADO)
  })

  it('tardío de otra generación (marcarSinSesion no cambia nada): sin evento', async () => {
    const w = ventana()
    s.marca = false
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(r401))
    await expect(listarRecords('B')).rejects.toBeInstanceOf(NoAutenticado)
    expect(marcarSinSesion).toHaveBeenCalledOnce()
    expect(w.dispatchEvent).not.toHaveBeenCalled()
  })
})

describe('esperaRetryAfter con tope', () => {
  it('nunca más de 5 minutos', () => {
    expect(esperaRetryAfter('99999')).toBe(300_000)
    expect(esperaRetryAfter('Tue, 06 Oct 2026 18:00:00 GMT', Date.parse('2026-10-06T12:00:00Z'))).toBe(300_000)
    expect(esperaRetryAfter('120')).toBe(120_000)
  })
  it('inválido o no finito: un minuto', () => {
    expect(esperaRetryAfter('Infinity')).toBe(60_000)
    expect(esperaRetryAfter('-5')).toBe(60_000)
    expect(esperaRetryAfter('abc')).toBe(60_000)
  })
  it('segundos, fecha HTTP o un minuto por defecto', () => {
    expect(esperaRetryAfter('7')).toBe(7000)
    expect(esperaRetryAfter('Tue, 06 Oct 2026 12:00:30 GMT', Date.parse('2026-10-06T12:00:00Z'))).toBe(30_000)
    expect(esperaRetryAfter(null)).toBe(60_000)
    expect(esperaRetryAfter('basura')).toBe(60_000)
  })
})

describe('borrarSerie', () => {
  it('404 cuenta como éxito (DELETE idempotente para la cola)', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: false, status: 404, headers: new Headers() }))
    await expect(borrarSerie('a', 'B')).resolves.toBeUndefined()
  })
})

describe('records', () => {
  it('listarRecords: GET /api/records con el dueño', async () => {
    const fetchFalso = ok([])
    vi.stubGlobal('fetch', fetchFalso)
    await expect(listarRecords('B')).resolves.toEqual([])
    expect(fetchFalso.mock.calls[0][0]).toBe('/api/records')
    await expect(listarRecords('A')).rejects.toBeInstanceOf(NoAutenticado)
    expect(fetchFalso).toHaveBeenCalledTimes(1)
  })
})

describe('rutinas', () => {
  const rutina = { id: 'r1', nombre: 'Push', ejercicios: [{ id: 'x', series: 3, reps: 10, pesoKg: null, descansoS: 90 }] }

  it('guardarRutina: PUT sin id en el cuerpo', async () => {
    const fetchFalso = ok({ ...rutina, actualizada: 'z' })
    vi.stubGlobal('fetch', fetchFalso)
    await guardarRutina(rutina, 'B')
    const [url, init] = fetchFalso.mock.calls[0]
    expect(url).toBe('/api/rutinas/r1')
    expect(init.method).toBe('PUT')
    expect(JSON.parse(init.body)).toEqual({ nombre: 'Push', ejercicios: rutina.ejercicios })
  })
  it('borrarRutina: 404 cuenta como éxito', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: false, status: 404, headers: new Headers() }))
    await expect(borrarRutina('r1', 'B')).resolves.toBeUndefined()
  })
  it('otro sub no llama a fetch', async () => {
    const fetchFalso = vi.fn()
    vi.stubGlobal('fetch', fetchFalso)
    await expect(guardarRutina(rutina, 'A')).rejects.toBeInstanceOf(NoAutenticado)
    await expect(borrarRutina('r1', 'A')).rejects.toBeInstanceOf(NoAutenticado)
    expect(fetchFalso).not.toHaveBeenCalled()
  })
})

describe('coach', () => {
  it('preguntarCoach: POST /api/coach con cookie, el cuerpo y su propio timeout', async () => {
    const timeout = vi.spyOn(AbortSignal, 'timeout')
    const fetchFalso = ok({ respuesta: 'hola', herramientas: [] })
    vi.stubGlobal('fetch', fetchFalso)
    const mensajes = [{ rol: 'usuario' as const, texto: '¿Cómo voy?' }]
    await expect(preguntarCoach(mensajes, '2026-10-09', 'B')).resolves.toEqual({ respuesta: 'hola', herramientas: [] })
    const [url, init] = fetchFalso.mock.calls[0]
    expect(url).toBe('/api/coach')
    expect(init.method).toBe('POST')
    expect(init.credentials).toBe('same-origin')
    expect(init.headers['X-Requested-With']).toBe('gym-coach')
    expect(init.headers['X-Gc-Sub']).toBe('B')
    expect(JSON.parse(init.body)).toEqual({ hoy: '2026-10-09', mensajes, idioma: 'es' })
    expect(timeout).toHaveBeenCalledWith(45_000)
    expect(init.signal).toBe(timeout.mock.results[timeout.mock.calls.findIndex((c) => c[0] === 45_000)].value)
    timeout.mockRestore()
  })
  it('en inglés el coach y el plan del día llevan idioma en el cuerpo', async () => {
    fijarIdioma('en')
    const fetchFalso = ok({ respuesta: 'hi', herramientas: [] })
    vi.stubGlobal('fetch', fetchFalso)
    await preguntarCoach([{ rol: 'usuario', texto: 'x' }], '2026-10-09', 'B', -300)
    expect(JSON.parse(fetchFalso.mock.calls[0][1].body)).toEqual({ hoy: '2026-10-09', mensajes: [{ rol: 'usuario', texto: 'x' }], offsetMin: -300, idioma: 'en' })
    await pedirPlanDia({ hoy: '2026-10-09', offsetMin: -300, otraOpcion: true, numero: 1 }, 'B')
    const [url, init] = fetchFalso.mock.calls[1]
    expect(url).toBe('/api/coach/plan-dia')
    expect(JSON.parse(init.body)).toEqual({ hoy: '2026-10-09', offsetMin: -300, otraOpcion: true, numero: 1, idioma: 'en' })
  })
  it('pedirPlanDia en español manda idioma es', async () => {
    const fetchFalso = ok({})
    vi.stubGlobal('fetch', fetchFalso)
    await pedirPlanDia({ hoy: '2026-10-09', offsetMin: 0 }, 'B')
    expect(JSON.parse(fetchFalso.mock.calls[0][1].body)).toEqual({ hoy: '2026-10-09', offsetMin: 0, otraOpcion: false, idioma: 'es' })
  })
  it('503 coach_no_configurado se distingue del 503 transitorio', async () => {
    const r503 = (error: string) => ({ ok: false, status: 503, headers: new Headers(), json: async () => ({ error }) })
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(r503('coach_no_configurado')))
    await expect(preguntarCoach([{ rol: 'usuario', texto: 'x' }], '2026-10-09', 'B')).rejects.toThrow('coach_no_configurado')
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(r503('no_disponible')))
    await expect(preguntarCoach([{ rol: 'usuario', texto: 'x' }], '2026-10-09', 'B')).rejects.toThrow('HTTP 503')
  })
  it('429 da DemasiadasPeticiones y otro sub no llama a fetch', async () => {
    const fetchFalso = vi.fn().mockResolvedValue({ ok: false, status: 429, headers: new Headers({ 'Retry-After': '30' }) })
    vi.stubGlobal('fetch', fetchFalso)
    await expect(preguntarCoach([{ rol: 'usuario', texto: 'x' }], '2026-10-09', 'B')).rejects.toBeInstanceOf(DemasiadasPeticiones)
    await expect(preguntarCoach([{ rol: 'usuario', texto: 'x' }], '2026-10-09', 'A')).rejects.toBeInstanceOf(NoAutenticado)
    expect(fetchFalso).toHaveBeenCalledTimes(1)
  })
  it('perfil: GET y PUT a /api/coach/perfil', async () => {
    const perfil = { minutosDisponibles: 45, objetivo: null, notas: ['rodilla'], alturaCm: 175, pesoCorporalKg: 78.5 }
    const fetchFalso = ok(perfil)
    vi.stubGlobal('fetch', fetchFalso)
    await expect(leerPerfilCoach('B')).resolves.toEqual(perfil)
    expect(fetchFalso.mock.calls[0][0]).toBe('/api/coach/perfil')
    await guardarPerfilCoach(perfil, 'B')
    const [url, init] = fetchFalso.mock.calls[1]
    expect(url).toBe('/api/coach/perfil')
    expect(init.method).toBe('PUT')
    expect(JSON.parse(init.body)).toEqual(perfil)
    expect(init.headers['X-Gc-Sub']).toBe('B')
  })
})
