// Estado de las series en el cliente, sin React: UI optimista, PUTs encadenados por id,
// deshacer/borrar y persistencia (por cuenta) de lo que aún no llegó al servidor.
import type { Serie, TipoFallo, TipoSerie } from './api'

/**
 * ok: guardada. pendiente: en vuelo, esperando login o esperando Retry-After.
 * error: falló por red/servidor, se reenvía sola. rechazada: la API la rechazó (4xx), hay que corregirla o borrarla.
 */
export type EstadoSerie = 'ok' | 'pendiente' | 'error' | 'rechazada'
export interface SerieLocal extends Serie {
  estado: EstadoSerie
  /** Por qué la rechazó la API (solo en 'rechazada'). */
  motivo?: string
}

export interface Dependencias {
  put: (s: Serie) => Promise<unknown>
  /** Debe tratar 404 como éxito. */
  del: (id: string) => Promise<void>
  clasificar: (e: unknown) => TipoFallo
  almacen?: (Pick<Storage, 'getItem' | 'setItem'> & Partial<Pick<Storage, 'removeItem'>>) | null
  /** Clave de persistencia: una por cuenta, para no mezclar pendientes entre usuarios. */
  clave: string
  /** Clave de los DELETE pendientes; por defecto se deriva de `clave` (misma cuenta). */
  claveBorrar?: string
  /** Caché de lo ya guardado (series ok) para mostrar algo sin sesión; sin ella no hay caché. */
  claveCache?: string
  /** Reloj en ms (inyectable para pruebas de Retry-After). */
  ahora?: () => number
}

export const prefijoSinSubir = 'gc.sinSubir:'
export const claveSinSubir = (sub: string) => prefijoSinSubir + sub
export const claveSeriesCache = (sub: string) => 'gc.series:' + sub
export const claveBorrarPendiente = (sub: string) => 'gc.borrarPendiente:' + sub

const claveBorrarDe = (clave: string) =>
  clave.startsWith(prefijoSinSubir) ? claveBorrarPendiente(clave.slice(prefijoSinSubir.length)) : clave + ':borrar'

const MAX_LAPIDAS = 500
// Tope de la cola de DELETE: más que esto offline no es realista y el localStorage no crece sin fin.
const MAX_BORRAR = 200
// Tope del caché de series ok: unos meses de entrenos, sin que el localStorage crezca sin fin.
const MAX_CACHE = 1000
const FECHA = /^\d{4}-\d{2}-\d{2}$/
const finitoPositivo = (n: unknown) => typeof n === 'number' && Number.isFinite(n) && n >= 0
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

interface Entrada {
  serie: SerieLocal
  /** Borrada en la UI, con el DELETE aún en cola. */
  borrada: boolean
  /** Sube en cada guardar: solo el resultado de la última edición marca el estado. */
  version: number
  /** Generación del último cambio local: una carga lanzada antes no la pisa. */
  generacion: number
  /** El servidor la tiene (un PUT dio ok o vino en una carga). Si no, deshacer es solo local. */
  confirmada: boolean
  /** Un PUT pudo llegar aunque su respuesta se perdió (error de red/5xx): el DELETE se manda igual. */
  incierta?: boolean
  /** Epoch ms: tras un 429 no se reenvía antes de esto. */
  reintentarDesde?: number
}

/** Forma persistida en localStorage. */
type Guardada = SerieLocal & { confirmada?: boolean; reintentarDesde?: number }

// 'normal' no se manda: el servidor lo omite y así una serie vieja y una nueva quedan iguales.
const datos = ({ id, ejercicioId, pesoKg, reps, fecha, ts, rutina, tipoSerie }: Serie): Serie => ({
  id, ejercicioId, pesoKg, reps, fecha, ts,
  ...(rutina !== undefined ? { rutina } : {}),
  ...(tipoSerie !== undefined && tipoSerie !== 'normal' ? { tipoSerie } : {}),
})

const TIPOS: readonly unknown[] = ['calentamiento', 'fallo', 'drop'] satisfies TipoSerie[]

export class AlmacenSeries {
  private entradas = new Map<string, Entrada>()
  /** Borradas ya confirmadas, con la generación del borrado: una carga vieja no las revive. */
  private lapidas = new Map<string, number>()
  /** Borradas en la UI cuyo PUT pudo llegar y cuyo DELETE falló: persistido, se reenvía y ninguna carga las revive. */
  private porBorrar = new Set<string>()
  private colas = new Map<string, Promise<unknown>>()
  private oyentes = new Set<() => void>()
  private cache: SerieLocal[] = []
  private gen = 0
  private deps: Dependencias
  private ahora: () => number
  private claveBorrar: string
  private ultimoCache = ''
  /** true si lo sin subir o los DELETE en cola no cupieron en el almacenamiento: viven solo en memoria. */
  sinEspacio = false

  constructor(deps: Dependencias) {
    this.deps = deps
    this.ahora = deps.ahora ?? Date.now
    this.claveBorrar = deps.claveBorrar ?? claveBorrarDe(deps.clave)
    this.porBorrar = new Set(this.leerPorBorrar())
    // Primero el caché (ya confirmadas); lo sin subir pisa lo que coincida.
    for (const g of this.leerLista(deps.claveCache, true)) {
      if (this.porBorrar.has(g.id)) continue
      this.entradas.set(g.id, {
        serie: { ...datos(g), estado: 'ok' },
        borrada: false,
        version: 0,
        generacion: 0,
        confirmada: true,
      })
    }
    for (const g of this.leerLista(deps.clave)) {
      if (this.porBorrar.has(g.id)) continue
      const { confirmada, reintentarDesde, ...serie } = g
      this.entradas.set(g.id, {
        serie,
        borrada: false,
        version: 0,
        generacion: 0,
        confirmada: confirmada ?? false,
        // incierta no se persiste: tras recargar, un error o pendiente sin confirmar pudo haber llegado.
        incierta: !confirmada && (serie.estado === 'error' || serie.estado === 'pendiente'),
        reintentarDesde,
      })
    }
    this.cache = this.calcular()
  }

  // Funciones flecha: se pasan tal cual a useSyncExternalStore.
  lista = (): SerieLocal[] => this.cache

  suscribir = (fn: () => void): (() => void) => {
    this.oyentes.add(fn)
    return () => this.oyentes.delete(fn)
  }

  /** Para fusionarServidor: tomarla justo antes de lanzar el GET. */
  generacion = (): number => this.gen

  /** Optimista: aparece al instante como pendiente; el PUT espera al anterior del mismo id. */
  guardar(s: Serie): Promise<void> {
    const previa = this.entradas.get(s.id)
    const version = (previa?.version ?? 0) + 1
    this.entradas.set(s.id, {
      serie: { ...datos(s), estado: 'pendiente' },
      borrada: false,
      version,
      generacion: ++this.gen,
      confirmada: previa?.confirmada ?? false,
    })
    this.lapidas.delete(s.id)
    // Guardarla de nuevo gana sobre un DELETE pendiente (el PUT va en la cola del id, después).
    this.porBorrar.delete(s.id)
    this.emitir()

    return this.encadenar(s.id, async () => {
      let serie: SerieLocal = { ...datos(s), estado: 'ok' }
      let reintentarDesde: number | undefined
      let confirmada = false
      let incierta = false
      try {
        await this.deps.put(datos(s))
        confirmada = true
      } catch (e) {
        const f = this.deps.clasificar(e)
        if (f.tipo === 'noAutenticado') serie = { ...serie, estado: 'pendiente' }
        else if (f.tipo === 'limite') {
          serie = { ...serie, estado: 'pendiente' }
          reintentarDesde = this.ahora() + f.esperaMs
        } else if (f.tipo === 'rechazada') serie = { ...serie, estado: 'rechazada', motivo: f.motivo }
        else {
          serie = { ...serie, estado: 'error' }
          incierta = true
        }
      }
      const actual = this.entradas.get(s.id)
      if (!actual) return
      if (confirmada) actual.confirmada = true
      if (incierta) actual.incierta = true
      // Si la borraron mientras tanto, se actualiza igual (por si el DELETE falla y hay que restaurarla).
      if (actual.version === version) {
        actual.serie = serie
        actual.reintentarDesde = reintentarDesde
        // El PUT acaba de cambiar el servidor: una carga lanzada antes trae datos viejos.
        actual.generacion = ++this.gen
        this.emitir()
      }
    })
  }

  /**
   * Quita la serie al instante; el DELETE va después de cualquier PUT en vuelo.
   * Si el servidor nunca la confirmó, se borra solo en local. false si el DELETE falló (y la restaura).
   */
  borrar(id: string): Promise<boolean> {
    const entrada = this.entradas.get(id)
    if (!entrada || entrada.borrada) return Promise.resolve(true)
    entrada.borrada = true
    entrada.generacion = ++this.gen
    this.emitir()

    return this.encadenar(id, async () => {
      // Tras el PUT en vuelo (si había) ya se sabe si el servidor la tiene.
      if (!entrada.confirmada && !entrada.incierta) {
        this.quitar(id, entrada)
        return true
      }
      // En cola antes de salir el DELETE: si cierran la pestaña con él en vuelo, el borrado se reenvía al volver.
      this.encolarBorrado(id)
      this.emitir()
      try {
        await this.deps.del(id)
        this.porBorrar.delete(id)
        this.quitar(id, entrada)
        return true
      } catch (e) {
        // Incierta (el PUT pudo llegar) o sin sesión: en la UI queda borrada y el DELETE queda en cola para reenviarse.
        if (!entrada.confirmada || this.deps.clasificar(e).tipo === 'noAutenticado') {
          this.encolarBorrado(id)
          this.quitar(id, entrada)
          return true
        }
        // Se restaura con su estado real (el PUT previo pudo terminar en ok o en error mientras tanto).
        this.porBorrar.delete(id)
        const actual = this.entradas.get(id)
        if (actual) actual.borrada = false
        this.emitir()
        return false
      }
    })
  }

  /**
   * Reenvía (PUT idempotente) lo pendiente o en error; nunca lo rechazado ni lo que espera un Retry-After.
   * También los DELETE en cola (idempotentes: `del` ya cuenta el 404 como éxito).
   */
  reenviarPendientes(): Promise<void[]> {
    const ahora = this.ahora()
    const porEnviar = [...this.entradas.values()].filter(
      (e) =>
        !e.borrada &&
        (e.serie.estado === 'pendiente' || e.serie.estado === 'error') &&
        !this.colas.has(e.serie.id) &&
        (e.reintentarDesde ?? 0) <= ahora,
    )
    const borrados = [...this.porBorrar].filter((id) => !this.colas.has(id)).map((id) => this.reenviarBorrado(id))
    return Promise.all([...porEnviar.map((e) => this.guardar(e.serie)), ...borrados])
  }

  /**
   * Mezcla una carga del servidor. generacionAlLanzar = generacion() justo antes del GET:
   * lo cambiado o borrado localmente después no se pisa ni revive.
   * Con `rango` (lo que pidió el GET), las ok confirmadas que el servidor ya no devolvió se quitan
   * (borradas en otro dispositivo o fuera de la ventana), salvo las posteriores al rango.
   */
  fusionarServidor(servidor: Serie[], generacionAlLanzar: number, rango?: { desde: string; hasta: string }) {
    for (const s of servidor) {
      // El servidor aún la tiene porque su DELETE no ha salido: no revive ni suma.
      if (this.porBorrar.has(s.id)) continue
      const lapida = this.lapidas.get(s.id)
      if (lapida !== undefined && lapida > generacionAlLanzar) continue
      const e = this.entradas.get(s.id)
      if (e && (e.generacion > generacionAlLanzar || e.borrada || e.serie.estado !== 'ok' || this.colas.has(s.id))) continue
      this.entradas.set(s.id, {
        serie: { ...datos(s), estado: 'ok' },
        borrada: false,
        version: e?.version ?? 0,
        generacion: e?.generacion ?? 0,
        confirmada: true,
      })
    }
    if (rango) {
      const vinieron = new Set(servidor.map((s) => s.id))
      for (const [id, e] of this.entradas) {
        if (
          !vinieron.has(id) && e.serie.estado === 'ok' && e.confirmada && !e.borrada &&
          e.generacion <= generacionAlLanzar && !this.colas.has(id) && e.serie.fecha <= rango.hasta
        ) this.entradas.delete(id)
      }
    }
    this.emitir()
  }

  private quitar(id: string, entrada: Entrada) {
    if (this.entradas.get(id) === entrada) this.entradas.delete(id)
    this.lapidas.delete(id)
    this.lapidas.set(id, ++this.gen)
    // Tope simple: solo una carga en vuelo durante cientos de borrados podría revivir una vieja.
    while (this.lapidas.size > MAX_LAPIDAS) this.lapidas.delete(this.lapidas.keys().next().value!)
    this.emitir()
  }

  private encolarBorrado(id: string) {
    this.porBorrar.delete(id)
    this.porBorrar.add(id)
    while (this.porBorrar.size > MAX_BORRAR) this.porBorrar.delete(this.porBorrar.keys().next().value!)
  }

  private reenviarBorrado(id: string): Promise<void> {
    return this.encadenar(id, async () => {
      // Un guardar posterior pudo sacarla de la cola mientras esperaba turno.
      if (!this.porBorrar.has(id)) return
      try {
        await this.deps.del(id)
      } catch (e) {
        // Un 4xx permanente no se arregla reintentando; lo demás (red, 5xx, 401, 429) queda en cola.
        if (this.deps.clasificar(e).tipo !== 'rechazada') return
      }
      this.porBorrar.delete(id)
      this.lapidas.set(id, ++this.gen)
      this.emitir()
    })
  }

  private encadenar<T>(id: string, op: () => Promise<T>): Promise<T> {
    const previa = this.colas.get(id) ?? Promise.resolve()
    const p = previa.then(op, op)
    this.colas.set(id, p)
    const limpiar = () => {
      if (this.colas.get(id) === p) this.colas.delete(id)
    }
    p.then(limpiar, limpiar)
    return p
  }

  private calcular(): SerieLocal[] {
    return [...this.entradas.values()].filter((e) => !e.borrada).map((e) => e.serie)
  }

  private emitir() {
    this.cache = this.calcular()
    let ok = this.guardarPersistente()
    if (!ok && this.deps.claveCache) {
      // Sin espacio: el caché es lo más pesado y se reconstruye; lo sin subir no.
      this.ultimoCache = ''
      try {
        this.deps.almacen?.removeItem?.(this.deps.claveCache)
      } catch {
        // Sin almacenamiento: nada que hacer.
      }
      ok = this.guardarPersistente()
    }
    this.sinEspacio = !ok
    if (ok) this.guardarCache()
    for (const fn of this.oyentes) fn()
  }

  /** Lo que no puede perderse (sin subir y DELETE en cola); false si alguno no cupo. */
  private guardarPersistente(): boolean {
    const a = this.guardarSinSubir()
    const b = this.guardarPorBorrar()
    return a && b
  }

  private guardarPorBorrar(): boolean {
    try {
      this.deps.almacen?.setItem(this.claveBorrar, JSON.stringify([...this.porBorrar]))
      return true
    } catch {
      // Igual que sinSubir: sin almacenamiento solo se pierde la persistencia.
      return false
    }
  }

  private leerPorBorrar(): string[] {
    try {
      const crudo: unknown = JSON.parse(this.deps.almacen?.getItem(this.claveBorrar) ?? '[]')
      if (!Array.isArray(crudo)) return []
      return crudo.filter((x): x is string => typeof x === 'string' && UUID.test(x)).slice(-MAX_BORRAR)
    } catch {
      return []
    }
  }

  private guardarCache() {
    if (!this.deps.claveCache) return
    const ok = [...this.entradas.values()]
      .filter((e) => !e.borrada && e.serie.estado === 'ok' && e.confirmada)
      .map((e) => datos(e.serie))
      .sort((a, b) => b.ts.localeCompare(a.ts))
      .slice(0, MAX_CACHE)
    const json = JSON.stringify(ok)
    if (json === this.ultimoCache) return
    try {
      this.deps.almacen?.setItem(this.deps.claveCache, json)
      this.ultimoCache = json
    } catch {
      // Cuota llena: un caché viejo revive series borradas, mejor quitarlo.
      this.ultimoCache = ''
      try {
        this.deps.almacen?.removeItem?.(this.deps.claveCache)
      } catch {
        // Sin almacenamiento: nada que hacer.
      }
    }
  }

  private guardarSinSubir(): boolean {
    const sinSubir: Guardada[] = [...this.entradas.values()]
      .filter((e) => !e.borrada && e.serie.estado !== 'ok')
      .map((e) => ({ ...e.serie, confirmada: e.confirmada, reintentarDesde: e.reintentarDesde }))
    try {
      this.deps.almacen?.setItem(this.deps.clave, JSON.stringify(sinSubir))
      return true
    } catch {
      // Sin almacenamiento (modo privado, cuota llena): se pierde solo la persistencia.
      return false
    }
  }

  private leerLista(clave: string | undefined, estricto = false): Guardada[] {
    if (!clave) return []
    try {
      const crudo: unknown = JSON.parse(this.deps.almacen?.getItem(clave) ?? '[]')
      if (!Array.isArray(crudo)) return []
      return (estricto ? crudo.slice(0, MAX_CACHE) : crudo)
        .filter(
          (x): x is Guardada =>
            typeof x?.id === 'string' && typeof x.ejercicioId === 'string' && typeof x.pesoKg === 'number' &&
            typeof x.reps === 'number' && typeof x.fecha === 'string' && typeof x.ts === 'string' &&
            (!estricto || (UUID.test(x.id) && FECHA.test(x.fecha) && x.ejercicioId.length <= 100 && finitoPositivo(x.pesoKg) && finitoPositivo(x.reps))),
        )
        .map((x): Guardada => {
          const estado: EstadoSerie = x.estado === 'error' || x.estado === 'rechazada' ? x.estado : 'pendiente'
          return {
            ...datos(x),
            // Dato raro en localStorage: un objeto aquí tumbaría el render del Historial.
            rutina: typeof x.rutina === 'string' && x.rutina.length <= 40 ? x.rutina : undefined,
            // Inválido o ausente = normal: no tumba la cola.
            tipoSerie: TIPOS.includes(x.tipoSerie) ? x.tipoSerie : undefined,
            estado,
            motivo: estado === 'rechazada' && typeof x.motivo === 'string' ? x.motivo : undefined,
            confirmada: x.confirmada === true,
            reintentarDesde: typeof x.reintentarDesde === 'number' ? x.reintentarDesde : undefined,
          }
        })
    } catch {
      return []
    }
  }
}
