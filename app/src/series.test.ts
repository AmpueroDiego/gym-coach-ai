import { describe, expect, it } from 'vitest'
import type { Serie, TipoFallo } from './api'
import { AlmacenSeries, claveSeriesCache, claveSinSubir, type Dependencias } from './series'

class NoAuth extends Error {}
class Limite extends Error {
  esperaMs: number
  constructor(esperaMs: number) {
    super('429')
    this.esperaMs = esperaMs
  }
}
class Rechazo extends Error {}

const clasificar = (e: unknown): TipoFallo =>
  e instanceof NoAuth
    ? { tipo: 'noAutenticado' }
    : e instanceof Limite
      ? { tipo: 'limite', esperaMs: e.esperaMs }
      : e instanceof Rechazo
        ? { tipo: 'rechazada', motivo: e.message }
        : { tipo: 'transitorio' }

/** Promesa que la prueba resuelve o rechaza cuando quiere: simula una petición en vuelo. */
function diferida<T = void>() {
  let resolver!: (v: T) => void
  let rechazar!: (e: unknown) => void
  const promesa = new Promise<T>((ok, mal) => {
    resolver = ok
    rechazar = mal
  })
  return { promesa, resolver, rechazar }
}

type Llamada = { tipo: 'put' | 'del'; id: string; serie?: Serie; d: ReturnType<typeof diferida<unknown>> }

/** API falsa: cada llamada queda en cola y la prueba decide su resultado. Almacenamiento y reloj compartibles. */
function apiFalsa(sub = 'A', almacen = new Map<string, string>(), reloj = { ms: 1_000_000 }) {
  const llamadas: Llamada[] = []
  const deps: Dependencias = {
    put: (s) => {
      const d = diferida<unknown>()
      llamadas.push({ tipo: 'put', id: s.id, serie: s, d })
      return d.promesa
    },
    del: (id) => {
      const d = diferida<unknown>()
      llamadas.push({ tipo: 'del', id, d })
      return d.promesa.then(() => undefined)
    },
    clasificar,
    almacen: { getItem: (k) => almacen.get(k) ?? null, setItem: (k, v) => void almacen.set(k, v) },
    clave: claveSinSubir(sub),
    ahora: () => reloj.ms,
  }
  return { deps, llamadas, almacen, reloj }
}

// Deja correr las continuaciones de promesas pendientes.
const tic = () => new Promise((r) => setTimeout(r, 0))

const serie = (id = 'a', pesoKg = 60, reps = 8): Serie => ({
  id,
  ejercicioId: 'press-banca',
  pesoKg,
  reps,
  fecha: '2026-10-06',
  ts: '2026-10-06T18:00:00.000Z',
})

/** Guarda y confirma (PUT ok): la serie queda en el servidor. */
async function confirmada(a: AlmacenSeries, llamadas: Llamada[], s = serie()) {
  const fin = a.guardar(s)
  await tic()
  llamadas[llamadas.length - 1].d.resolver(undefined)
  await fin
}

describe('AlmacenSeries', () => {
  it('guardar: aparece al instante como pendiente y pasa a ok cuando responde el PUT', async () => {
    const { deps, llamadas } = apiFalsa()
    const a = new AlmacenSeries(deps)
    const fin = a.guardar(serie())
    expect(a.lista()).toMatchObject([{ id: 'a', estado: 'pendiente' }])
    await tic()
    llamadas[0].d.resolver(undefined)
    await fin
    expect(a.lista()).toMatchObject([{ id: 'a', estado: 'ok' }])
  })

  it('deshacer con el PUT en vuelo: el DELETE espera al PUT y la serie no reaparece', async () => {
    const { deps, llamadas } = apiFalsa()
    const a = new AlmacenSeries(deps)
    a.guardar(serie())
    const borrado = a.borrar('a')
    expect(a.lista()).toEqual([])

    await tic()
    expect(llamadas.map((l) => l.tipo)).toEqual(['put']) // el DELETE aún no sale
    llamadas[0].d.resolver(undefined)
    await tic()
    expect(llamadas.map((l) => l.tipo)).toEqual(['put', 'del'])
    expect(a.lista()).toEqual([]) // el ok del PUT no la revive

    llamadas[1].d.resolver(undefined)
    expect(await borrado).toBe(true)
    expect(a.lista()).toEqual([])
  })

  it('PUT que falla (respuesta perdida) y luego deshacer: manda el DELETE por si el PUT sí llegó', async () => {
    const { deps, llamadas } = apiFalsa()
    const a = new AlmacenSeries(deps)
    a.guardar(serie())
    await tic()
    llamadas[0].d.rechazar(new TypeError('red'))
    await tic()
    expect(a.lista()).toMatchObject([{ estado: 'error' }])

    const borrado = a.borrar('a')
    await tic()
    expect(llamadas.map((l) => l.tipo)).toEqual(['put', 'del'])
    llamadas[1].d.resolver(undefined)
    expect(await borrado).toBe(true)
    expect(a.lista()).toEqual([])
  })

  it('PUT incierto, DELETE también falla (sin red): se ve borrada y el DELETE queda en cola', async () => {
    const { deps, llamadas } = apiFalsa()
    const a = new AlmacenSeries(deps)
    a.guardar(serie())
    await tic()
    llamadas[0].d.rechazar(new TypeError('red'))
    await tic()
    const borrado = a.borrar('a')
    await tic()
    llamadas[1].d.rechazar(new TypeError('red'))
    expect(await borrado).toBe(true)
    expect(a.lista()).toEqual([])

    // El PUT sí había llegado: la carga no la revive mientras el DELETE siga pendiente.
    a.fusionarServidor([serie()], a.generacion())
    expect(a.lista()).toEqual([])
    a.reenviarPendientes()
    await tic()
    expect(llamadas.map((l) => l.tipo)).toEqual(['put', 'del', 'del'])
  })

  it('DELETE que falla: restaura la serie con su estado real', async () => {
    const { deps, llamadas } = apiFalsa()
    const a = new AlmacenSeries(deps)
    await confirmada(a, llamadas, serie('a', 60))
    a.guardar(serie('a', 65)) // edición en vuelo
    const borrado = a.borrar('a')
    await tic()
    llamadas[1].d.rechazar(new TypeError('red')) // la edición falló mientras estaba "borrada"
    await tic()
    llamadas[2].d.rechazar(new TypeError('red')) // y el DELETE también
    expect(await borrado).toBe(false)
    expect(a.lista()).toMatchObject([{ id: 'a', pesoKg: 65, estado: 'error' }])
  })

  it('dos ediciones seguidas del mismo id: los PUT van en orden y gana la última', async () => {
    const { deps, llamadas } = apiFalsa()
    const a = new AlmacenSeries(deps)
    a.guardar(serie('a', 60))
    const segunda = a.guardar(serie('a', 62.5))
    expect(a.lista()).toMatchObject([{ pesoKg: 62.5, estado: 'pendiente' }])

    await tic()
    expect(llamadas).toHaveLength(1) // la segunda espera a la primera
    llamadas[0].d.resolver(undefined)
    await tic()
    expect(a.lista()).toMatchObject([{ pesoKg: 62.5, estado: 'pendiente' }]) // el ok viejo no la marca
    expect(llamadas.map((l) => l.serie?.pesoKg)).toEqual([60, 62.5])

    llamadas[1].d.resolver(undefined)
    await segunda
    expect(a.lista()).toMatchObject([{ pesoKg: 62.5, estado: 'ok' }])
  })

  it('401 → queda pendiente → se reenvía tras volver a entrar', async () => {
    const { deps, llamadas } = apiFalsa()
    const a = new AlmacenSeries(deps)
    const fin = a.guardar(serie())
    await tic()
    llamadas[0].d.rechazar(new NoAuth())
    await fin
    expect(a.lista()).toMatchObject([{ estado: 'pendiente' }])

    const reenvio = a.reenviarPendientes()
    await tic()
    expect(llamadas).toHaveLength(2)
    llamadas[1].d.resolver(undefined)
    await reenvio
    expect(a.lista()).toMatchObject([{ estado: 'ok' }])
  })

  it('reenviar no duplica lo que ya está en vuelo', async () => {
    const { deps, llamadas } = apiFalsa()
    const a = new AlmacenSeries(deps)
    a.guardar(serie())
    a.reenviarPendientes()
    await tic()
    expect(llamadas).toHaveLength(1)
  })

  it('conserva `rutina` al guardar, persistir, reenviar y fusionar con el servidor', async () => {
    const almacen = new Map<string, string>()
    const primera = apiFalsa('A', almacen)
    const a = new AlmacenSeries(primera.deps)
    const conRutina = { ...serie('r1'), rutina: 'Push' }
    const fin = a.guardar(conRutina)
    await tic()
    expect(primera.llamadas[0].serie).toMatchObject({ rutina: 'Push' })
    primera.llamadas[0].d.rechazar(new NoAuth()) // queda pendiente y persistida
    await fin
    expect(a.lista()[0]).toMatchObject({ rutina: 'Push', estado: 'pendiente' })
    expect(almacen.get(claveSinSubir('A'))).toContain('"rutina":"Push"')

    const segunda = apiFalsa('A', almacen)
    const b = new AlmacenSeries(segunda.deps)
    expect(b.lista()[0]).toMatchObject({ rutina: 'Push' })
    b.reenviarPendientes()
    await tic()
    expect(segunda.llamadas[0].serie).toMatchObject({ rutina: 'Push' })
    segunda.llamadas[0].d.resolver(undefined)
    await tic()
    b.fusionarServidor([{ ...conRutina, pesoKg: 65 }], b.generacion())
    expect(b.lista()[0]).toMatchObject({ rutina: 'Push', pesoKg: 65, estado: 'ok' })
  })

  it('(a) cambio de cuenta: B no ve ni reenvía lo de A; al volver a A se reenvía', async () => {
    const almacen = new Map<string, string>()
    const deA = apiFalsa('A', almacen)
    const a = new AlmacenSeries(deA.deps)
    const fin = a.guardar(serie('de-a'))
    await tic()
    deA.llamadas[0].d.rechazar(new NoAuth()) // sesión vencida: queda pendiente para A
    await fin

    const deB = apiFalsa('B', almacen)
    const b = new AlmacenSeries(deB.deps)
    expect(b.lista()).toEqual([])
    await b.reenviarPendientes()
    expect(deB.llamadas).toHaveLength(0)

    const deA2 = apiFalsa('A', almacen)
    const a2 = new AlmacenSeries(deA2.deps)
    expect(a2.lista()).toMatchObject([{ id: 'de-a', estado: 'pendiente' }])
    a2.reenviarPendientes()
    await tic()
    expect(deA2.llamadas.map((l) => l.id)).toEqual(['de-a'])
  })

  it('(b) 400 → rechazada con motivo, persistida, y no se reenvía nunca sola', async () => {
    const { deps, llamadas, almacen } = apiFalsa()
    const a = new AlmacenSeries(deps)
    const fin = a.guardar(serie())
    await tic()
    llamadas[0].d.rechazar(new Rechazo('pesoKg: Máximo 2 decimales.'))
    await fin
    expect(a.lista()).toMatchObject([{ estado: 'rechazada', motivo: 'pesoKg: Máximo 2 decimales.' }])

    await a.reenviarPendientes() // lo que hacen el montaje, el evento online y el login
    expect(llamadas).toHaveLength(1)

    // Persistida y recuperada como rechazada: tampoco se reenvía en la instancia nueva.
    const otra = new AlmacenSeries(apiFalsa('A', almacen).deps)
    expect(otra.lista()).toMatchObject([{ estado: 'rechazada', motivo: 'pesoKg: Máximo 2 decimales.' }])
    await otra.reenviarPendientes()

    // Corregirla a mano sí la manda.
    a.guardar(serie('a', 60))
    await tic()
    expect(llamadas).toHaveLength(2)
  })

  it('(c) 429: no se reenvía antes de Retry-After y sí después', async () => {
    const { deps, llamadas, reloj } = apiFalsa()
    const a = new AlmacenSeries(deps)
    const fin = a.guardar(serie())
    await tic()
    llamadas[0].d.rechazar(new Limite(30_000))
    await fin
    expect(a.lista()).toMatchObject([{ estado: 'pendiente' }])

    reloj.ms += 29_999
    await a.reenviarPendientes()
    expect(llamadas).toHaveLength(1)

    reloj.ms += 1
    a.reenviarPendientes()
    await tic()
    expect(llamadas).toHaveLength(2)
  })

  it('(d) fusionarServidor con GET en vuelo: no pisa una edición ni revive una borrada', async () => {
    const { deps, llamadas } = apiFalsa()
    const a = new AlmacenSeries(deps)
    await confirmada(a, llamadas, serie('editada', 60))
    await confirmada(a, llamadas, serie('borrada', 50))

    const genGet = a.generacion() // se lanza el GET
    await confirmada(a, llamadas, serie('editada', 70)) // edición confirmada mientras el GET vuela
    const b = a.borrar('borrada')
    await tic()
    llamadas[llamadas.length - 1].d.resolver(undefined)
    await b

    // El GET responde con lo que el servidor tenía al lanzarlo.
    a.fusionarServidor([serie('editada', 60), serie('borrada', 50), serie('nueva', 80)], genGet)
    const porId = Object.fromEntries(a.lista().map((s) => [s.id, s]))
    expect(Object.keys(porId).sort()).toEqual(['editada', 'nueva'])
    expect(porId.editada).toMatchObject({ pesoKg: 70, estado: 'ok' })

    // Un GET lanzado después sí actualiza.
    a.fusionarServidor([serie('editada', 75)], a.generacion())
    expect(a.lista().find((s) => s.id === 'editada')).toMatchObject({ pesoKg: 75 })
  })

  it('(e) deshacer de una serie nunca confirmada (401, sin PUT en vuelo): sin DELETE y no se reenvía', async () => {
    const { deps, llamadas, almacen } = apiFalsa()
    const a = new AlmacenSeries(deps)
    const fin = a.guardar(serie())
    await tic()
    llamadas[0].d.rechazar(new NoAuth())
    await fin

    expect(await a.borrar('a')).toBe(true)
    expect(llamadas.map((l) => l.tipo)).toEqual(['put'])
    await a.reenviarPendientes()
    expect(llamadas).toHaveLength(1)
    expect(new AlmacenSeries(apiFalsa('A', almacen).deps).lista()).toEqual([])
  })

  it('almacenamiento corrupto o que lanza: arranca vacío sin romper', () => {
    const { deps } = apiFalsa()
    const roto = new AlmacenSeries({ ...deps, almacen: { getItem: () => '{no json', setItem: () => {} } })
    expect(roto.lista()).toEqual([])
    const lanza = new AlmacenSeries({
      ...deps,
      almacen: {
        getItem: () => {
          throw new Error('privado')
        },
        setItem: () => {
          throw new Error('cuota')
        },
      },
    })
    lanza.guardar(serie())
    expect(lanza.lista()).toHaveLength(1)
  })

  it('fusionarServidor: une por id sin duplicar y sin pisar lo pendiente', () => {
    const { deps } = apiFalsa()
    const a = new AlmacenSeries(deps)
    a.guardar(serie('local', 70)) // pendiente
    a.fusionarServidor([serie('local', 60), serie('srv', 80), serie('srv', 80)], a.generacion())
    const porId = Object.fromEntries(a.lista().map((s) => [s.id, s]))
    expect(a.lista()).toHaveLength(2)
    expect(porId.local).toMatchObject({ pesoKg: 70, estado: 'pendiente' })
    expect(porId.srv).toMatchObject({ pesoKg: 80, estado: 'ok' })
  })
})

describe('DELETE pendientes', () => {
  const U1 = '11111111-1111-4111-8111-111111111111'
  const U2 = '22222222-2222-4222-8222-222222222222'

  /** PUT con respuesta perdida y luego borrar con el DELETE fallando por red. */
  async function borradaIncierta(a: AlmacenSeries, llamadas: Llamada[], id: string) {
    a.guardar(serie(id))
    await tic()
    llamadas[llamadas.length - 1].d.rechazar(new TypeError('red'))
    await tic()
    const borrado = a.borrar(id)
    await tic()
    llamadas[llamadas.length - 1].d.rechazar(new TypeError('red'))
    expect(await borrado).toBe(true)
  }

  it('recarga: conserva la cola, no revive la serie y reenvía el DELETE; ok limpia la cola', async () => {
    const almacen = new Map<string, string>()
    const primera = apiFalsa('A', almacen)
    await borradaIncierta(new AlmacenSeries(primera.deps), primera.llamadas, U1)
    expect(JSON.parse(almacen.get('gc.borrarPendiente:A')!)).toEqual([U1])

    const segunda = apiFalsa('A', almacen)
    const b = new AlmacenSeries(segunda.deps)
    expect(b.lista()).toEqual([])
    b.fusionarServidor([serie(U1)], b.generacion()) // el servidor aún la tiene
    expect(b.lista()).toEqual([])

    const reenvio = b.reenviarPendientes()
    await tic()
    expect(segunda.llamadas.map((l) => [l.tipo, l.id])).toEqual([['del', U1]])
    segunda.llamadas[0].d.resolver(undefined) // 200 o 404: `del` ya los da por buenos
    await reenvio
    expect(JSON.parse(almacen.get('gc.borrarPendiente:A')!)).toEqual([])
    await b.reenviarPendientes()
    expect(segunda.llamadas).toHaveLength(1)
  })

  it('reenvío que vuelve a fallar: sigue en cola', async () => {
    const { deps, llamadas, almacen } = apiFalsa()
    const a = new AlmacenSeries(deps)
    await borradaIncierta(a, llamadas, U1)
    const reenvio = a.reenviarPendientes()
    await tic()
    llamadas[llamadas.length - 1].d.rechazar(new NoAuth())
    await reenvio
    expect(JSON.parse(almacen.get('gc.borrarPendiente:A')!)).toEqual([U1])
  })

  it('serie incierta recargada desde la cola sin subir: borrarla manda el DELETE', async () => {
    const almacen = new Map<string, string>()
    almacen.set(claveSinSubir('A'), JSON.stringify([{ ...serie(U1), estado: 'error' }]))
    const { deps, llamadas } = apiFalsa('A', almacen)
    const a = new AlmacenSeries(deps)
    const borrado = a.borrar(U1)
    await tic()
    expect(llamadas.map((l) => l.tipo)).toEqual(['del'])
    llamadas[0].d.rechazar(new TypeError('red'))
    expect(await borrado).toBe(true)
    expect(JSON.parse(almacen.get('gc.borrarPendiente:A')!)).toEqual([U1])
  })

  it('la cola de A no se mezcla con B', async () => {
    const almacen = new Map<string, string>()
    const deA = apiFalsa('A', almacen)
    await borradaIncierta(new AlmacenSeries(deA.deps), deA.llamadas, U1)

    const deB = apiFalsa('B', almacen)
    const b = new AlmacenSeries(deB.deps)
    b.fusionarServidor([serie(U1)], b.generacion()) // en B no está pendiente de borrar
    expect(b.lista()).toHaveLength(1)
    await b.reenviarPendientes()
    expect(deB.llamadas).toHaveLength(0)
  })

  it('cola corrupta, ids no UUID o almacenamiento que lanza: no rompe', async () => {
    const almacen = new Map<string, string>()
    almacen.set('gc.borrarPendiente:A', '{no json')
    expect(new AlmacenSeries(apiFalsa('A', almacen).deps).lista()).toEqual([])

    almacen.set('gc.borrarPendiente:A', JSON.stringify([U2, 'a', 3, null, { id: U1 }, '../x']))
    const { deps, llamadas } = apiFalsa('A', almacen)
    const a = new AlmacenSeries(deps)
    a.reenviarPendientes()
    await tic()
    expect(llamadas.map((l) => l.id)).toEqual([U2])

    const lanza = new AlmacenSeries({
      ...deps,
      almacen: {
        getItem: () => {
          throw new Error('privado')
        },
        setItem: () => {
          throw new Error('cuota')
        },
      },
    })
    expect(lanza.lista()).toEqual([])
  })

  it('tope de la cola: conserva los últimos 200', async () => {
    const almacen = new Map<string, string>()
    const ids = Array.from({ length: 250 }, (_, i) => `00000000-0000-4000-8000-${String(i).padStart(12, '0')}`)
    almacen.set('gc.borrarPendiente:A', JSON.stringify(ids))
    const { deps, llamadas } = apiFalsa('A', almacen)
    new AlmacenSeries(deps).reenviarPendientes()
    await tic()
    expect(llamadas).toHaveLength(200)
    expect(llamadas[0].id).toBe(ids[50])
  })
})

describe('tipo de serie', () => {
  it('conserva tipoSerie en el PUT, la cola persistida, el reenvío y la fusión; normal no se manda', async () => {
    const almacen = new Map<string, string>()
    const primera = apiFalsa('A', almacen)
    const a = new AlmacenSeries(primera.deps)
    const cal: Serie = { ...serie('c1'), tipoSerie: 'calentamiento' }
    const fin = a.guardar(cal)
    a.guardar({ ...serie('n1'), tipoSerie: 'normal' })
    await tic()
    expect(primera.llamadas[0].serie).toMatchObject({ tipoSerie: 'calentamiento' })
    expect(primera.llamadas[1].serie).not.toHaveProperty('tipoSerie')
    primera.llamadas[0].d.rechazar(new NoAuth())
    await fin
    expect(almacen.get(claveSinSubir('A'))).toContain('"tipoSerie":"calentamiento"')

    const segunda = apiFalsa('A', almacen)
    const b = new AlmacenSeries(segunda.deps)
    expect(b.lista().find((s) => s.id === 'c1')).toMatchObject({ tipoSerie: 'calentamiento', estado: 'pendiente' })
    b.reenviarPendientes()
    await tic()
    expect(segunda.llamadas.find((l) => l.id === 'c1')!.serie).toMatchObject({ tipoSerie: 'calentamiento' })
    b.fusionarServidor([{ ...serie('srv'), tipoSerie: 'drop' }], b.generacion())
    expect(b.lista().find((s) => s.id === 'srv')).toMatchObject({ tipoSerie: 'drop', estado: 'ok' })
  })

  it('cola con tipo inválido o sin campo (serie vieja): carga como normal sin romper', () => {
    const almacen = new Map<string, string>()
    almacen.set(claveSinSubir('A'), JSON.stringify([
      { ...serie('raro'), tipoSerie: 'superserie', estado: 'pendiente' },
      { ...serie('objeto'), tipoSerie: { x: 1 }, estado: 'pendiente' },
      { ...serie('vieja'), estado: 'error' },
    ]))
    const a = new AlmacenSeries(apiFalsa('A', almacen).deps)
    expect(a.lista()).toHaveLength(3)
    expect(a.lista().every((s) => s.tipoSerie === undefined)).toBe(true)
  })
})

describe('caché de series ok', () => {
  const OK1 = '33333333-3333-4333-8333-333333333333'
  const BORR = '44444444-4444-4444-8444-444444444444'
  const PEND = '55555555-5555-4555-8555-555555555555'
  const conCache = (almacen: Map<string, string>) => {
    const api = apiFalsa('A', almacen)
    api.deps.claveCache = claveSeriesCache('A')
    return api
  }

  it('un almacén nuevo sobre el mismo storage recupera las ok; nunca guarda pendientes ni borradas', async () => {
    const almacen = new Map<string, string>()
    const p = conCache(almacen)
    const a = new AlmacenSeries(p.deps)
    await confirmada(a, p.llamadas, serie(OK1))
    await confirmada(a, p.llamadas, serie(BORR))
    const borrado = a.borrar(BORR)
    await tic()
    a.guardar(serie(PEND)) // en vuelo: pendiente
    p.llamadas.find((l) => l.tipo === 'del')!.d.resolver(undefined)
    await borrado
    const guardado = JSON.parse(almacen.get('gc.series:A')!) as Serie[]
    expect(guardado.map((s) => s.id)).toEqual([OK1])

    const otra = new AlmacenSeries(conCache(almacen).deps)
    expect(otra.lista().find((s) => s.id === OK1)).toMatchObject({ estado: 'ok' })
    expect(otra.lista().find((s) => s.id === PEND)).toMatchObject({ estado: 'pendiente' }) // viene de sinSubir
    expect(otra.lista().find((s) => s.id === BORR)).toBeUndefined()
  })

  it('sinSubir gana sobre el caché con el mismo id', () => {
    const almacen = new Map<string, string>()
    almacen.set('gc.series:A', JSON.stringify([serie(OK1, 60)]))
    almacen.set(claveSinSubir('A'), JSON.stringify([{ ...serie(OK1, 80), estado: 'error', confirmada: true }]))
    const a = new AlmacenSeries(conCache(almacen).deps)
    expect(a.lista()).toMatchObject([{ id: OK1, pesoKg: 80, estado: 'error' }])
  })

  it('tope de 1000, las más recientes', () => {
    const almacen = new Map<string, string>()
    const muchas = Array.from({ length: 1100 }, (_, i) => ({ ...serie('00000000-0000-4000-8000-' + String(i).padStart(12, '0')), ts: `2026-10-06T18:${String(i).padStart(5, '0')}` }))
    const a = new AlmacenSeries(conCache(almacen).deps)
    a.fusionarServidor(muchas, a.generacion())
    const guardado = JSON.parse(almacen.get('gc.series:A')!) as Serie[]
    expect(guardado).toHaveLength(1000)
    expect(guardado[0].id).toBe('00000000-0000-4000-8000-000000001099')
  })

  it('si el caché no se puede escribir (cuota), lo quita para no revivir borradas', async () => {
    const almacen = new Map<string, string>()
    const p = conCache(almacen)
    let lleno = false
    p.deps.almacen = {
      getItem: (k) => almacen.get(k) ?? null,
      setItem: (k, v) => {
        if (lleno && k === 'gc.series:A') throw new Error('cuota')
        almacen.set(k, v)
      },
      removeItem: (k) => void almacen.delete(k),
    }
    const a = new AlmacenSeries(p.deps)
    await confirmada(a, p.llamadas, serie(OK1))
    expect(almacen.has('gc.series:A')).toBe(true)
    lleno = true
    const borrado = a.borrar(OK1)
    await tic()
    p.llamadas[p.llamadas.length - 1].d.resolver(undefined)
    await borrado
    expect(almacen.has('gc.series:A')).toBe(false)
  })

  it('el caché descarta entradas con id, fecha o números inválidos', () => {
    const almacen = new Map<string, string>()
    almacen.set('gc.series:A', JSON.stringify([
      serie(OK1), serie('no-uuid'), { ...serie(BORR), fecha: 'ayer' }, { ...serie(PEND), pesoKg: -1 }, { ...serie(PEND), reps: null },
    ]))
    expect(new AlmacenSeries(conCache(almacen).deps).lista().map((s) => s.id)).toEqual([OK1])
  })

  it('no revive del caché una serie con DELETE en cola', () => {
    const almacen = new Map<string, string>()
    const U1 = '11111111-1111-4111-8111-111111111111'
    almacen.set('gc.series:A', JSON.stringify([serie(U1)]))
    almacen.set('gc.borrarPendiente:A', JSON.stringify([U1]))
    expect(new AlmacenSeries(conCache(almacen).deps).lista()).toEqual([])
  })
})

describe('fusionarServidor con rango', () => {
  const rango = { desde: '2026-09-01', hasta: '2026-10-06' }

  it('quita una ok confirmada que el servidor ya no devuelve', async () => {
    const { deps, llamadas } = apiFalsa()
    const a = new AlmacenSeries(deps)
    await confirmada(a, llamadas, serie('a'))
    a.fusionarServidor([], a.generacion(), rango)
    expect(a.lista()).toEqual([])
  })

  it('no quita una pendiente', () => {
    const { deps } = apiFalsa()
    const a = new AlmacenSeries(deps)
    a.guardar(serie('p'))
    a.fusionarServidor([], a.generacion(), rango)
    expect(a.lista()).toMatchObject([{ id: 'p', estado: 'pendiente' }])
  })

  it('no quita una guardada después de lanzar el GET', async () => {
    const { deps, llamadas } = apiFalsa()
    const a = new AlmacenSeries(deps)
    const gen = a.generacion()
    await confirmada(a, llamadas, serie('n'))
    a.fusionarServidor([], gen, rango)
    expect(a.lista()).toMatchObject([{ id: 'n', estado: 'ok' }])
  })

  it('no quita una con fecha posterior a hasta', async () => {
    const { deps, llamadas } = apiFalsa()
    const a = new AlmacenSeries(deps)
    await confirmada(a, llamadas, { ...serie('f'), fecha: '2026-10-07' })
    a.fusionarServidor([], a.generacion(), rango)
    expect(a.lista()).toHaveLength(1)
  })
})

describe('borrar sin sesión', () => {
  it('una confirmada con DELETE noAutenticado se quita, queda en cola y se reenvía', async () => {
    const { deps, llamadas, almacen } = apiFalsa()
    const a = new AlmacenSeries(deps)
    const id = '11111111-1111-4111-8111-111111111111'
    await confirmada(a, llamadas, serie('c'))
    await confirmada(a, llamadas, serie(id))
    const borrado = a.borrar(id)
    await tic()
    llamadas[llamadas.length - 1].d.rechazar(new NoAuth())
    expect(await borrado).toBe(true)
    expect(a.lista().map((s) => s.id)).toEqual(['c'])
    expect(JSON.parse(almacen.get('gc.borrarPendiente:A')!)).toEqual([id])

    const n = llamadas.length
    const reenvio = a.reenviarPendientes()
    await tic()
    expect(llamadas.slice(n)).toMatchObject([{ tipo: 'del', id }])
    llamadas[llamadas.length - 1].d.resolver(undefined)
    await reenvio
    expect(JSON.parse(almacen.get('gc.borrarPendiente:A')!)).toEqual([])
  })
})

describe('DELETE en vuelo', () => {
  it('queda en gc.borrarPendiente antes de responder el DELETE y sale al confirmarse', async () => {
    const { deps, llamadas, almacen } = apiFalsa()
    const a = new AlmacenSeries(deps)
    await confirmada(a, llamadas, serie('v'))
    const borrado = a.borrar('v')
    await tic()
    expect(llamadas[llamadas.length - 1].tipo).toBe('del')
    expect(JSON.parse(almacen.get('gc.borrarPendiente:A')!)).toEqual(['v'])
    llamadas[llamadas.length - 1].d.resolver(undefined)
    await borrado
    expect(JSON.parse(almacen.get('gc.borrarPendiente:A')!)).toEqual([])
  })

  it('si el DELETE se rechaza y se restaura la serie, sale de la cola', async () => {
    const { deps, llamadas, almacen } = apiFalsa()
    const a = new AlmacenSeries(deps)
    await confirmada(a, llamadas, serie('v'))
    const borrado = a.borrar('v')
    await tic()
    llamadas[llamadas.length - 1].d.rechazar(new TypeError('red'))
    expect(await borrado).toBe(false)
    expect(JSON.parse(almacen.get('gc.borrarPendiente:A')!)).toEqual([])
    expect(a.lista()).toHaveLength(1)
  })
})

describe('almacenamiento lleno', () => {
  it('quita el caché para que quepa lo sin subir; si aun así no cabe, sinEspacio', async () => {
    const almacen = new Map<string, string>()
    const p = apiFalsa('A', almacen)
    p.deps.claveCache = claveSeriesCache('A')
    let tope = Infinity
    p.deps.almacen = {
      getItem: (k) => almacen.get(k) ?? null,
      setItem: (k, v) => {
        const total = [...almacen].filter(([c]) => c !== k).reduce((n, [, x]) => n + x.length, 0) + v.length
        if (total > tope) throw new Error('cuota')
        almacen.set(k, v)
      },
      removeItem: (k) => void almacen.delete(k),
    }
    const a = new AlmacenSeries(p.deps)
    await confirmada(a, p.llamadas, serie('ok'))
    expect(almacen.has('gc.series:A')).toBe(true)
    tope = almacen.get('gc.series:A')!.length + 100 // con el caché no cabe lo sin subir, sin él sí
    a.guardar(serie('off')) // pendiente: debe caber quitando el caché
    expect(almacen.has('gc.series:A')).toBe(false)
    expect(JSON.parse(almacen.get(claveSinSubir('A'))!)).toMatchObject([{ id: 'off' }])
    expect(a.sinEspacio).toBe(false)

    tope = 5 // ya nada cabe
    a.guardar(serie('off2'))
    expect(a.sinEspacio).toBe(true)
    expect(a.lista().map((s) => s.id)).toContain('off2')
  })
})
