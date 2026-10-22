// Google Identity Services: botón, One Tap y el callback que cambia el ID token por la sesión de la API.
// Se usa a través de auth.ts (que re-exporta CLIENT_ID y mostrarBotonGoogle).
import { idiomaApp, textos } from './i18n'
import { crearSesion, leerCuentaRecordada, quitarSalioManual, salioManual, SesionDescartada, type Sesion } from './auth'

const t = textos(
  {
    noCargo: 'GIS no cargó',
    noScript: 'No se pudo cargar Google Sign-In',
    faltaId: 'Falta VITE_GOOGLE_CLIENT_ID',
  },
  {
    noCargo: "Google Sign-In didn't load",
    noScript: "Couldn't load Google Sign-In",
    faltaId: 'VITE_GOOGLE_CLIENT_ID is missing',
  },
)

const SCRIPT = 'https://accounts.google.com/gsi/client'

export const CLIENT_ID: string | undefined = import.meta.env.VITE_GOOGLE_CLIENT_ID

/** select_by dice cómo eligió la cuenta: https://developers.google.com/identity/gsi/web/reference/js-reference#select_by */
interface RespuestaGis {
  credential: string
  select_by?: string
}

interface Gis {
  accounts: {
    id: {
      initialize(opciones: { client_id: string; callback: (r: RespuestaGis) => void; auto_select?: boolean }): void
      renderButton(el: HTMLElement, opciones: Record<string, unknown>): void
      prompt(): void
      disableAutoSelect(): void
    }
  }
}

declare global {
  interface Window {
    google?: Gis
  }
}

/** Payload del ID token sin verificar la firma; null si está malformado. */
function payloadDe(token: string | null): Record<string, unknown> | null {
  try {
    const payload = token?.split('.')[1]
    if (!payload) return null
    const base64 = payload.replace(/-/g, '+').replace(/_/g, '/')
    const binario = atob(base64.padEnd(Math.ceil(base64.length / 4) * 4, '='))
    // atob da bytes; el JSON viene en UTF-8 (nombres con tilde o ñ).
    const texto = new TextDecoder().decode(Uint8Array.from(binario, (c) => c.charCodeAt(0)))
    const json: unknown = JSON.parse(texto)
    return typeof json === 'object' && json !== null ? (json as Record<string, unknown>) : null
  } catch {
    return null
  }
}

/** `sub` del ID token sin verificar la firma: solo decide si One Tap automático es de la cuenta recordada. */
function subDelToken(token: string | null): string | null {
  const sub = payloadDe(token)?.sub
  return typeof sub === 'string' && sub.trim() !== '' ? sub : null
}

// El usuario tocó algo (botón o cuenta en One Tap). Lo demás ('auto', 'itp', 'fedcm_auto', ausente) es automático.
const EXPLICITOS = new Set(['btn', 'btn_confirm', 'btn_add_session', 'btn_confirm_add_session', 'user', 'user_1tap', 'user_2tap'])

interface Callbacks {
  alEntrar: (s: Sesion) => void
  alFallar?: (e: unknown) => void
}

/**
 * Único callback de GIS. initialize es global, así que el modo sale de select_by y no de quién inicializó.
 * Automático: no re-entra tras un cierre manual ni cambia de cuenta (la cola de la cuenta recordada quedaría varada).
 * Nunca rechaza: GIS llama sin await.
 */
async function alRecibir(r: RespuestaGis, { alEntrar, alFallar }: Callbacks) {
  const explicito = EXPLICITOS.has(r.select_by ?? '')
  if (!explicito) {
    if (salioManual()) return
    if (subDelToken(r.credential) !== leerCuentaRecordada()?.sub) return
  }
  let s: Sesion
  try {
    s = await crearSesion(r.credential, { automatico: !explicito })
  } catch (e) {
    // Descartada: el usuario salió mientras volaba el POST; no es un error que mostrar.
    if (!(e instanceof SesionDescartada)) alFallar?.(e)
    return
  }
  if (explicito) quitarSalioManual()
  alEntrar(s)
}

let cargando: Promise<Gis> | null = null

function cargarGis(): Promise<Gis> {
  cargando ??= new Promise((ok, falla) => {
    if (window.google) return ok(window.google)
    const s = document.createElement('script')
    s.src = SCRIPT
    s.async = true
    // Si falla, se quita el <script> y se olvida la promesa: el siguiente intento vuelve a cargar.
    const fallar = (mensaje: string) => {
      cargando = null
      s.remove()
      falla(new Error(mensaje))
    }
    s.onload = () => (window.google ? ok(window.google) : fallar(t().noCargo))
    s.onerror = () => fallar(t().noScript)
    document.head.appendChild(s)
  })
  return cargando
}

/**
 * Pinta el botón de Google en `el` y llama a `alEntrar` con la sesión ya creada en la API.
 * `alFallar`: Google respondió pero la API no dio sesión (sin red, token rechazado).
 * `oneTap: false` para pintar solo el botón.
 */
export async function mostrarBotonGoogle(
  el: HTMLElement,
  alEntrar: (s: Sesion) => void,
  { oneTap = true, alFallar }: { oneTap?: boolean; alFallar?: (e: unknown) => void } = {},
) {
  if (!CLIENT_ID) throw new Error(t().faltaId)
  const gis = await cargarGis()
  const salio = salioManual()
  gis.accounts.id.initialize({
    client_id: CLIENT_ID,
    auto_select: !salio,
    callback: (r) => void alRecibir(r, { alEntrar, alFallar }),
  })
  gis.accounts.id.renderButton(el, { theme: 'filled_black', size: 'large', shape: 'pill', text: 'continue_with', locale: idiomaApp() })
  // One Tap: si ya entró antes, re-entra sin tocar nada.
  if (oneTap && !salio) gis.accounts.id.prompt()
}
