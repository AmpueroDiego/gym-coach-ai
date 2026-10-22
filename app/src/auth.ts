// Sesión por cookie. El ID token de Google solo vive en memoria durante el POST /api/sesion: la API lo valida
// y deja una cookie HttpOnly que JS nunca ve ni nombra. La parte de Google Sign-In está en gis.ts.

export { CLIENT_ID, mostrarBotonGoogle } from './gis'

// Token de la versión anterior (sessionStorage): ya no se usa; limpiarLegado lo borra.
const CLAVE_LEGADO = 'gc.idToken'
// Tras un cierre manual, One Tap no re-entra solo (GIS puede no estar cargado aún). En localStorage para que
// sobreviva a cerrar la PWA; no es secreto. Solo la quita una entrada explícita del usuario.
const CLAVE_SALIO = 'gc.salioManual'
// Última cuenta (sin credenciales) en localStorage: si el sistema mata la pestaña, la app monta igual con su caché.
const CLAVE_CUENTA = 'gc.cuenta'
// La API exige este header en lo que cambia estado (CSRF): un formulario de otro sitio no puede ponerlo.
const CABECERA_CSRF = { 'X-Requested-With': 'gym-coach' }
// Al volver al frente no se verifica la sesión más de una vez cada 12 h: la cookie dura 90 días.
const VERIFICAR_CADA_MS = 12 * 3600_000

/** Lo que la API devuelve de la sesión. Mismas reglas que la cuenta recordada. */
export interface Sesion {
  sub: string
  nombre: string | null
  foto: string | null
}

export type CuentaRecordada = Sesion

/** El usuario salió mientras volaba el login: la sesión que llegó no se aplica. */
export class SesionDescartada extends Error {}

// desconocida: aún no se verificó (o sin red); se trata como activa para no tapar la app offline.
type Estado = 'desconocida' | 'activa' | 'ausente'

let estado: Estado = 'desconocida'
let sesion: Sesion | null = null
// Sube con cada login y cada salida: una respuesta de antes no debe pisar lo que pasó después.
let generacion = 0
let ultimaVerificacion = 0

export function estadoSesion(): { estado: Estado; sub: string | null; generacion: number } {
  return { estado, sub: sesion?.sub ?? null, generacion }
}

/** 401 de la API: pasa a ausente solo si nadie entró ni salió desde que salió la petición. Dice si cambió algo. */
export function marcarSinSesion(generacionVista: number): boolean {
  if (generacionVista !== generacion || estado === 'ausente') return false
  estado = 'ausente'
  sesion = null
  return true
}

/** Borra la cookie en la API. Sin await y sin lanzar: salir funciona igual sin red. */
function borrarCookie() {
  try {
    fetch('/api/sesion', { method: 'DELETE', keepalive: true, credentials: 'same-origin', headers: CABECERA_CSRF })
      .catch(() => {})
  } catch {
    // Sin fetch la cookie queda; vence sola.
  }
}

/**
 * Cambia el ID token de Google por la cookie de sesión. Lanza si la API no la da.
 * Si el usuario salió mientras volaba (o, siendo automático, salió en otra pestaña), lanza SesionDescartada.
 */
export async function crearSesion(idToken: string, { automatico = false } = {}): Promise<Sesion> {
  const gen = generacion
  const res = await fetch('/api/sesion', {
    method: 'POST',
    credentials: 'same-origin',
    // 30 s: el primer login tras un rato pega en un cold start de Functions.
    signal: AbortSignal.timeout(30_000),
    headers: { ...CABECERA_CSRF, 'Content-Type': 'application/json' },
    body: JSON.stringify({ idToken }),
  })
  if (!res.ok) throw new Error(`HTTP ${res.status}`)
  const s = validarCuenta(await res.json().catch(() => null))
  if (!s) throw new Error('sesion_invalida')
  if (gen !== generacion || (automatico && salioManual())) {
    // La cookie ya llegó: si lo último fue salir, se borra. Si entró otro login, ese manda.
    if (estado !== 'activa' || salioManual()) borrarCookie()
    throw new SesionDescartada()
  }
  estado = 'activa'
  sesion = s
  generacion++
  ultimaVerificacion = Date.now()
  recordarCuenta(s)
  return s
}

// Sin respuesta útil: vale reintentar más tarde (no dice nada de la cookie).
const esTransitorio = (status: number) => status >= 500 || status === 429 || status === 408

/**
 * Pregunta a la API si la cookie sigue valiendo (y la renueva si toca).
 * null: no hay sesión. 'sin_red': no se sabe; el estado no cambia (optimista).
 */
export async function restaurarSesion(): Promise<Sesion | null | 'sin_red'> {
  // Se anota también sin red: vigilarSesion no reintenta en cada foco.
  ultimaVerificacion = Date.now()
  // Tras salir a mano no se reusa la cookie aunque el DELETE no haya llegado: se reintenta borrarla.
  if (salioManual()) {
    estado = 'ausente'
    sesion = null
    borrarCookie()
    return null
  }
  const gen = generacion
  // Ya sin sesión, un fallo de red no la devuelve: la UI no oscila entre Reconectar y la app.
  const sinRed = () => (estado === 'ausente' ? null : 'sin_red')
  let res: Response
  try {
    res = await fetch('/api/sesion', { credentials: 'same-origin', signal: AbortSignal.timeout(15_000), headers: CABECERA_CSRF })
  } catch {
    return sinRed()
  }
  if (esTransitorio(res.status)) return sinRed()
  if (!res.ok) {
    // 401 o cualquier otro rechazo: sin sesión, salvo que alguien entrara mientras volaba el GET.
    marcarSinSesion(gen)
    return gen === generacion ? null : sesion
  }
  const s = validarCuenta(await res.json().catch(() => null))
  if (!s) return sinRed()
  // Entró o salió alguien mientras volaba (también otra pestaña, vía la bandera): manda eso.
  if (gen !== generacion || salioManual()) return sesion
  estado = 'activa'
  sesion = s
  return s
}

type Documento = Pick<Document, 'visibilityState' | 'addEventListener' | 'removeEventListener'>

/** Al volver al frente, si pasaron más de 12 h desde la última verificación, vuelve a preguntar. Devuelve con qué dejar de escuchar. */
export function vigilarSesion(alCambiar: (r: Sesion | null | 'sin_red') => void, doc: Documento = document): () => void {
  let activo = true
  const alVolver = () => {
    if (doc.visibilityState !== 'visible' || Date.now() - ultimaVerificacion <= VERIFICAR_CADA_MS) return
    void restaurarSesion().then((r) => {
      if (activo) alCambiar(r)
    })
  }
  doc.addEventListener('visibilitychange', alVolver)
  return () => {
    activo = false
    doc.removeEventListener('visibilitychange', alVolver)
  }
}

/** La versión anterior guardaba el ID token en sessionStorage: se borra al arrancar. */
export function limpiarLegado(): void {
  try {
    sessionStorage.removeItem(CLAVE_LEGADO)
  } catch {
    // Sin sessionStorage no hay nada que borrar.
  }
}

/** Cierre que pide el usuario: borra la cookie y apaga el auto-select para que One Tap no lo re-entre. */
export function cerrarSesionManual() {
  estado = 'ausente'
  sesion = null
  // Lo que esté en vuelo (login, verificación) llega tarde y se descarta.
  generacion++
  olvidarCuenta()
  try {
    localStorage.setItem(CLAVE_SALIO, '1')
  } catch {
    // Sin localStorage no hay bandera; se sigue con lo demás.
  }
  try {
    if (typeof window !== 'undefined') window.google?.accounts.id.disableAutoSelect()
  } catch {
    // Si GIS falla, la sesión local ya quedó cerrada.
  }
  borrarCookie()
}

const MAX_NOMBRE = 60

/** Recorta por grafemas (no parte un emoji ni una bandera); sin Intl.Segmenter, por code points. */
function recortar(texto: string, max: number): string {
  const partes = typeof Intl !== 'undefined' && 'Segmenter' in Intl
    ? Array.from(new Intl.Segmenter().segment(texto), (x) => x.segment)
    : Array.from(texto)
  return partes.slice(0, max).join('')
}

// Mismos hosts que permite el CSP (img-src): las fotos de perfil salen de lh3–lh6, no de cualquier *.googleusercontent.com.
const HOST_FOTO = /^lh[3-6]\.googleusercontent\.com$/

function fotoValida(picture: unknown): string | null {
  if (typeof picture !== 'string') return null
  try {
    const url = new URL(picture)
    const ok = url.protocol === 'https:' && HOST_FOTO.test(url.hostname) && !url.username && !url.password && !url.port
    return ok ? url.href : null
  } catch {
    return null
  }
}

const MAX_SUB = 255

/** Ni localStorage ni la respuesta de la API son de fiar para pintar: sub obligatorio, nombre y foto saneados. */
function validarCuenta(c: unknown): Sesion | null {
  if (typeof c !== 'object' || c === null) return null
  const { sub, nombre, foto } = c as Record<string, unknown>
  if (typeof sub !== 'string' || sub.trim() === '' || sub.length > MAX_SUB) return null
  const limpio = typeof nombre === 'string' ? recortar(nombre.trim(), MAX_NOMBRE).trim() : ''
  return { sub, nombre: limpio || null, foto: fotoValida(foto) }
}

export function leerCuentaRecordada(): CuentaRecordada | null {
  try {
    const crudo = localStorage.getItem(CLAVE_CUENTA)
    return crudo === null ? null : validarCuenta(JSON.parse(crudo))
  } catch {
    return null
  }
}

/** Guarda sub, nombre y foto; nunca credenciales. */
export function recordarCuenta(s: Sesion): void {
  try {
    const cuenta = validarCuenta(s)
    if (cuenta) localStorage.setItem(CLAVE_CUENTA, JSON.stringify(cuenta))
  } catch {
    // Sin localStorage solo se pierde el arranque sin red.
  }
}

export function olvidarCuenta(): void {
  try {
    localStorage.removeItem(CLAVE_CUENTA)
  } catch {
    // Nada que olvidar.
  }
}

export function salioManual(): boolean {
  try {
    return localStorage.getItem(CLAVE_SALIO) === '1'
  } catch {
    return false
  }
}

/** Solo una entrada explícita quita la bandera de salida. */
export function quitarSalioManual(): void {
  try {
    localStorage.removeItem(CLAVE_SALIO)
  } catch {
    // La bandera solo afecta al auto-select.
  }
}
