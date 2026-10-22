import { useEffect, useEffectEvent, useRef, useState, type ReactNode } from 'react'
import { leerPlanDia, pedirPlanDia, preguntarCoach, type PlanDia, type Rutina } from '../api'
import {
  getVigente,
  debePedirPlanSolo,
  debeRepedirPorIdioma,
  etiquetaHerramienta,
  falloPlan,
  guardarConversacion,
  herramientasValidas,
  leerConversacion,
  limpiarTexto,
  marcarAsesoria,
  MAX_TEXTO,
  paraEnviar,
  planValido,
  programaValido,
  rutinaValida,
  SONDEO_MAX_MS,
  SONDEO_MS,
  SUGERENCIAS,
  textoEspera,
  textoError,
  textoFalloPlan,
  type EstadoCoach,
  type MensajeCoach,
} from '../coach'
import { idiomaApp, textos } from '../i18n'
import { textoVersionApp } from '../version'
import './coach.css'
import TarjetaPrograma from './TarjetaPrograma'
import TarjetaRutina from './TarjetaRutina'
import { useScrollChat } from './useScrollChat'

interface Props {
  sub: string
  conSesion: boolean
  hoy: string
  nombre: (id: string) => string
  /** App lo enciende cuando ya pasó la hora de la asesoría, hoy no se vio el plan y la pestaña está a la vista. */
  autoPlan: boolean
  /** El plan del día ya se mostró con la pestaña a la vista: App apaga el punto de aviso. */
  alPlanVisto: () => void
  /** guardada: el usuario ya la guardó desde la tarjeta. */
  alEmpezarRutina: (r: Rutina, guardada: boolean) => void
  /** Formulario del perfil; como función recibe cómo volver al chat (p. ej. tras guardar). */
  perfil: ReactNode | ((cerrar: () => void) => ReactNode)
}

// localStorage puede no existir o lanzar (modo privado, política del navegador).
function almacenLocal() {
  try {
    return typeof localStorage === 'undefined' ? null : localStorage
  } catch {
    return null
  }
}

const t = textos(
  {
    escribiendo: 'El coach está escribiendo',
    planHoy: 'Tu entreno de hoy',
    consulto: (h: string) => `Consultó: ${h}`,
    otraOpcion: (n: number) => `Otra opción (${n})`,
    preparandoOtra: 'Preparando otra opción…',
    preparandoHoy: 'Preparando tu entreno de hoy…',
    reintentar: 'Reintentar',
    tarda: 'Tu entreno de hoy tarda más de lo normal.',
    noRespondio: 'El coach no respondió.',
    volver: 'Volver al chat',
    perfilCoach: 'Perfil del coach',
    perfil: 'Perfil',
    nueva: 'Nueva conversación',
    sinSesion: 'Sin sesión: reconecta para hablar con el coach.',
    sugerencias: 'Sugerencias',
    irFinalNuevo: 'Ir al final, respuesta nueva',
    irFinal: 'Ir al final',
    mensaje: 'Mensaje para el coach',
    pregunta: 'Pregúntale al coach',
    reconecta: 'Reconecta para escribir',
    enviar: 'Enviar',
  },
  {
    escribiendo: 'The coach is typing',
    planHoy: "Today's workout",
    consulto: (h) => `Checked: ${h}`,
    otraOpcion: (n) => `Another option (${n})`,
    preparandoOtra: 'Preparing another option…',
    preparandoHoy: "Preparing today's workout…",
    reintentar: 'Retry',
    tarda: "Today's workout is taking longer than usual.",
    noRespondio: "The coach didn't respond.",
    volver: 'Back to chat',
    perfilCoach: 'Coach profile',
    perfil: 'Profile',
    nueva: 'New conversation',
    sinSesion: 'Signed out: reconnect to talk to the coach.',
    sugerencias: 'Suggestions',
    irFinalNuevo: 'Go to the end, new reply',
    irFinal: 'Go to the end',
    mensaje: 'Message for the coach',
    pregunta: 'Ask the coach',
    reconecta: 'Reconnect to write',
    enviar: 'Send',
  },
)

/** Keys estables aunque dos mensajes compartan ts: el n-ésimo con el mismo ts y rol lleva su número. */
function claves(mensajes: MensajeCoach[]): string[] {
  const vistas = new Map<string, number>()
  return mensajes.map((m) => {
    const base = m.rol + m.ts
    const n = vistas.get(base) ?? 0
    vistas.set(base, n + 1)
    return `${base}#${n}`
  })
}

/** Indicador animado. El lector de pantalla oye un texto fijo una vez; los puntos y el texto que cambia van ocultos. */
export function Pensando({ segundos }: { segundos: number }) {
  return (
    <li className="mensaje coach nuevo" data-pensando="true">
      <div className="burbuja pensando">
        <span role="status" className="solo-lector">{t().escribiendo}</span>
        <span className="puntos" aria-hidden="true"><i /><i /><i /></span>
        <span className="pensando-texto" aria-hidden="true">{textoEspera(segundos)}</span>
      </div>
    </li>
  )
}

type EstadoPlan = 'cargando' | 'listo' | 'fallo'
interface FalloPlan {
  texto: string
  reintentable: boolean
  /** Qué repetir con Reintentar. */
  accion: 'leer' | 'pedir' | 'otra'
}

interface PropsPlan {
  plan: PlanDia | null
  estado: EstadoPlan
  pidiendo: boolean
  fallo: FalloPlan | null
  conSesion: boolean
  nombre: (id: string) => string
  sub: string
  alEmpezar: (r: Rutina, guardada: boolean) => void
  alOtraOpcion: () => void
  alReintentar: () => void
}

/** Bloque fijo sobre el chat: el plan de hoy (el mismo en todos los dispositivos), fuera de la conversación. */
export function PlanDelDia(p: PropsPlan) {
  const actual = p.plan?.actual ?? null
  const ocupado = p.pidiendo || !!p.plan?.generando
  if (!actual && !p.fallo && !ocupado) return null
  return (
    <section className="plan-dia" aria-label={t().planHoy}>
      <h3>{t().planHoy}</h3>
      {actual && (
        <>
          <p className="burbuja">{actual.texto}</p>
          {actual.herramientas.length > 0 && <p className="consulto">{t().consulto(actual.herramientas.map(etiquetaHerramienta).join(', '))}</p>}
          {actual.rutina && <TarjetaRutina key={actual.creado} rutina={actual.rutina} nombre={p.nombre} sub={p.sub} alEmpezar={p.alEmpezar} />}
          <button className="chip" onClick={p.alOtraOpcion} disabled={!p.conSesion || ocupado || (p.plan?.restantes ?? 0) <= 0}>
            {t().otraOpcion(p.plan?.restantes ?? 0)}
          </button>
        </>
      )}
      {ocupado && <p className="consulto" role="status">{actual ? t().preparandoOtra : t().preparandoHoy}</p>}
      {p.fallo && (
        <div className="fallo">
          <p className="error" role="alert">{p.fallo.texto}</p>
          {p.fallo.reintentable && <button onClick={p.alReintentar} disabled={ocupado || !p.conSesion}>{t().reintentar}</button>}
        </div>
      )}
    </section>
  )
}

// Fuera del componente: es una hora de evento (al enviar o responder), no algo que se calcule al renderizar.
// Minutos de adelanto sobre UTC de la zona local (el backend no la conoce).
const offsetMin = () => -new Date().getTimezoneOffset()
const ahoraIso = () => new Date().toISOString()

export default function Coach(p: Props) {
  // App re-renderiza todo el árbol al cambiar el idioma (useIdioma): basta leerlo aquí.
  const idioma = idiomaApp()
  const [estado, setEstado] = useState<EstadoCoach>(() => leerConversacion(almacenLocal(), p.sub, p.hoy))
  const [texto, setTexto] = useState('')
  const [pensando, setPensando] = useState(false)
  const [verPerfil, setVerPerfil] = useState(false)
  const [segundos, setSegundos] = useState(0)
  const campo = useRef<HTMLTextAreaElement>(null)
  const clave = p.sub + '|' + p.hoy
  const [deQuien, setDeQuien] = useState(clave)
  // Sube con "Nueva conversación"; junto con claveActual descarta la respuesta tardía de una conversación que ya no está.
  const generacion = useRef(0)
  const claveActual = useRef(clave)
  // Plan del día: se descarta una respuesta de otra cuenta/día o más vieja que la última petición.
  const [plan, setPlan] = useState<PlanDia | null>(null)
  const [estadoPlan, setEstadoPlan] = useState<EstadoPlan>('cargando')
  const [pidiendo, setPidiendo] = useState(false)
  const [falloPlanUi, setFalloPlanUi] = useState<FalloPlan | null>(null)
  // GET y POST llevan contadores aparte: un GET en vuelo no debe invalidar un POST (ni dejar pidiendo colgado).
  const peticionGet = useRef(0)
  const peticionPost = useRef(0)
  // Clave con un POST en vuelo (null = ninguno).
  const postEnVuelo = useRef<string | null>(null)
  // Clave que ya hizo el POST automático: uno por cuenta y día, aunque falle.
  const planPedido = useRef<string | null>(null)
  // Mensajes ya pintados al montar: solo los que llegan después se animan.
  const montados = useRef(estado.mensajes.length)

  // Cambió el día o la cuenta: la conversación guardada ya no es la de ahora (se ajusta al renderizar, sin efecto).
  if (deQuien !== clave) {
    setDeQuien(clave)
    const leida = leerConversacion(almacenLocal(), p.sub, p.hoy)
    setEstado(leida)
    montados.current = leida.mensajes.length
    setPensando(false)
    setPlan(null)
    setEstadoPlan('cargando')
    setPidiendo(false)
    setFalloPlanUi(null)
    // El borrador es de la cuenta anterior: no debe poder salir con la nueva.
    setTexto('')
  }
  useEffect(() => {
    claveActual.current = clave
  }, [clave])

  useEffect(() => {
    if (estado.fecha === p.hoy) guardarConversacion(almacenLocal(), p.sub, estado)
  }, [estado, p.sub, p.hoy])

  const nMensajes = estado.mensajes.length
  const ultimoRol = estado.mensajes[nMensajes - 1]?.rol ?? null
  const { lista, relleno, anclarProximo, alGesto, alScroll, irAlFinal, irAlInicio, reiniciar, lejos, hayNuevo } = useScrollChat({
    activo: !verPerfil, nMensajes, pensando, ultimoRol, clave,
  })

  // Con el chat vacío el plan queda arriba a la vista; con mensajes no se mueve el scroll.
  const creadoPlan = plan?.actual?.creado
  useEffect(() => {
    if (creadoPlan && nMensajes === 0) irAlInicio()
    // Solo al llegar un plan: los mensajes no deben re-disparar el salto.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [creadoPlan])

  /** Acepta solo un plan de hoy con forma válida; lo demás cuenta como fallo. */
  const aplicarPlan = (r: unknown, accion: FalloPlan['accion']): boolean => {
    const v = planValido(r)
    if (!v || v.fecha !== p.hoy) {
      setFalloPlanUi({ texto: textoFalloPlan(), reintentable: true, accion })
      return false
    }
    setPlan(v)
    setFalloPlanUi(null)
    return true
  }

  const leerPlan = async () => {
    if (!p.conSesion) return
    // Con un POST en vuelo el GET sobra: el POST trae el estado más nuevo.
    if (postEnVuelo.current === clave) return
    const n = ++peticionGet.current
    const post0 = peticionPost.current
    const vigente = () => getVigente({ get: n, post: post0, clave }, { get: peticionGet.current, post: peticionPost.current, clave: claveActual.current })
    try {
      const r = await leerPlanDia(p.hoy, p.sub)
      if (!vigente()) return
      setEstadoPlan(aplicarPlan(r, 'leer') ? 'listo' : 'fallo')
    } catch (e) {
      if (!vigente()) return
      setEstadoPlan('fallo')
      setFalloPlanUi({ ...falloPlan(e), accion: 'leer' })
    }
  }

  /** POST del plan (o de otra opción). El servidor devuelve el existente sin gastar modelo si ya hay plan. */
  const pedirPlan = async (otraOpcion: boolean) => {
    if (!p.conSesion || pidiendo) return
    const n = ++peticionPost.current
    const vigente = () => n === peticionPost.current && claveActual.current === clave
    postEnVuelo.current = clave
    setPidiendo(true)
    setFalloPlanUi(null)
    // El de este envío, no el del momento de la respuesta: si cambia en vuelo, el efecto de idioma vuelve a pedir.
    const idiomaEnvio = idiomaApp()
    try {
      // numero: cuántas variaciones ve la app; si el servidor ya tiene más, un reintento no genera otra.
      const numero = otraOpcion && plan ? plan.numero : undefined
      const r = await pedirPlanDia({ hoy: p.hoy, offsetMin: offsetMin(), otraOpcion, numero }, p.sub)
      if (!vigente()) return
      const ok = aplicarPlan(r, otraOpcion ? 'otra' : 'pedir')
      if (ok) setEstado((e) => ({ ...e, idiomaPlan: idiomaEnvio }))
      setEstadoPlan(ok ? 'listo' : 'fallo')
    } catch (e) {
      if (!vigente()) return
      const f = falloPlan(e)
      setFalloPlanUi({ ...f, accion: otraOpcion ? 'otra' : 'pedir' })
      // Tras el tope no quedan opciones: el botón se apaga sin esperar otro GET.
      if (!f.reintentable && otraOpcion) setPlan((x) => (x ? { ...x, restantes: 0 } : x))
    } finally {
      if (n === peticionPost.current) postEnVuelo.current = null
      if (vigente()) setPidiendo(false)
    }
  }

  // GET al montar, al cambiar cuenta/día, al recuperar la sesión y al volver a primer plano.
  const leerAlVolver = useEffectEvent(() => void leerPlan())
  useEffect(() => {
    // Diferido un tick: la lectura actualiza estado al terminar, no al montar.
    const id = setTimeout(leerAlVolver, 0)
    return () => clearTimeout(id)
  }, [clave, p.conSesion])
  useEffect(() => {
    if (!p.conSesion) return
    const visible = () => document.visibilityState === 'visible' && leerAlVolver()
    document.addEventListener('visibilitychange', visible)
    return () => document.removeEventListener('visibilitychange', visible)
  }, [clave, p.conSesion])

  // Otro dispositivo (o una petición anterior) lo está generando: sondea hasta que llegue.
  const generando = !!plan?.generando
  const sondear = useEffectEvent(() => void leerPlan())
  useEffect(() => {
    if (!generando || !p.conSesion) return
    const inicio = Date.now()
    const id = setInterval(() => {
      // En segundo plano los timers se congelan o se amontonan: se retoma al volver (visibilitychange hace el GET).
      if (document.visibilityState !== 'visible') return
      if (Date.now() - inicio >= SONDEO_MAX_MS) {
        clearInterval(id)
        setPlan((x) => (x ? { ...x, generando: false } : x))
        setFalloPlanUi({ texto: t().tarda, reintentable: true, accion: 'leer' })
        return
      }
      sondear()
    }, SONDEO_MS)
    return () => clearInterval(id)
  }, [generando, p.conSesion, clave])

  // POST automático: solo pasada la hora, una vez por clave y solo si el GET confirmó que no hay plan.
  const pedirSolo = useEffectEvent(() => {
    if (!debePedirPlanSolo({
      auto: p.autoPlan, conSesion: p.conSesion, leido: estadoPlan === 'listo', hayPlan: !!plan?.actual,
      generando, pidiendo, yaPedido: planPedido.current === clave,
    })) return
    planPedido.current = clave
    void pedirPlan(false)
  })
  useEffect(() => pedirSolo(), [p.autoPlan, p.conSesion, estadoPlan, plan, pidiendo, clave])

  // El plan se mostró con la pestaña a la vista (autoPlan solo está encendido ahí): apaga el punto de aviso.
  const planVisto = useEffectEvent(() => {
    if (!p.autoPlan || !plan?.actual) return
    // Solo a disco: guardarConversacion no deja retroceder la marca al guardar el estado.
    marcarAsesoria(almacenLocal(), p.sub, p.hoy)
    p.alPlanVisto()
  })
  useEffect(() => planVisto(), [p.autoPlan, plan])

  // Plan de hoy pedido en otro idioma: un solo POST con el nuevo; el backend decide si regenera.
  // La marca se mueve antes de pedir: si falla o vuelve el plan en el idioma viejo, no se reintenta solo.
  const repedirPorIdioma = useEffectEvent(() => {
    if (!debeRepedirPorIdioma({
      conSesion: p.conSesion, leido: estadoPlan === 'listo', hayPlan: !!plan?.actual,
      generando, pidiendo, idiomaPlan: estado.idiomaPlan, idioma,
    })) return
    setEstado((e) => ({ ...e, idiomaPlan: idioma }))
    void pedirPlan(false)
  })
  useEffect(() => repedirPorIdioma(), [idioma, p.conSesion, estadoPlan, plan, generando, pidiendo, estado.idiomaPlan])

  // Cuenta los segundos de espera solo mientras el coach piensa.
  useEffect(() => {
    if (!pensando) return
    const id = setInterval(() => setSegundos((s) => s + 1), 1000)
    return () => {
      clearInterval(id)
      setSegundos(0)
    }
  }, [pensando])

  /** `previos`: la conversación sobre la que va el mensaje (Reintentar la pasa sin el fallido). */
  const enviar = async (escrito: string, previos = estado.mensajes) => {
    // Lo que el backend rechaza se quita antes: un texto que queda vacío no se manda.
    const limpio = limpiarTexto(escrito).trim()
    if (!limpio || pensando || !p.conSesion) return
    anclarProximo()
    const gen = generacion.current
    const vigente = () => gen === generacion.current && claveActual.current === clave
    const ts = ahoraIso()
    const mensajes: MensajeCoach[] = [...previos, { rol: 'usuario', texto: limpio.slice(0, MAX_TEXTO), ts }]
    setEstado((e) => ({ ...e, mensajes }))
    setPensando(true)
    try {
      const r = await preguntarCoach(paraEnviar(mensajes), p.hoy, p.sub, offsetMin())
      if (!vigente()) return
      const respuesta: MensajeCoach = {
        rol: 'coach',
        texto: typeof r.respuesta === 'string' && r.respuesta.trim() ? r.respuesta : t().noRespondio,
        ts: ahoraIso(),
        herramientas: herramientasValidas(r.herramientas),
      }
      const rutina = r.rutina === undefined ? null : rutinaValida(r.rutina)
      if (rutina) respuesta.rutina = rutina
      const programa = r.programa == null ? null : programaValido(r.programa)
      if (programa) respuesta.programa = programa
      setEstado((e) => ({ ...e, mensajes: [...e.mensajes, respuesta] }))
    } catch (e) {
      if (!vigente()) return
      const error = textoError(e)
      setEstado((s) => ({ ...s, mensajes: s.mensajes.map((m) => (m.ts === ts && m.rol === 'usuario' ? { ...m, error } : m)) }))
    } finally {
      if (vigente()) setPensando(false)
    }
  }

  const reintentar = (m: MensajeCoach) => {
    // Sale de donde estaba y vuelve a entrar como el último mensaje.
    void enviar(m.texto, estado.mensajes.filter((x) => x !== m))
  }

  const mandarCampo = () => {
    if (!limpiarTexto(texto).trim() || pensando || !p.conSesion) return
    void enviar(texto)
    setTexto('')
    // Baja el teclado: la respuesta se lee sin él; el chat ya quedó anclado al mensaje enviado.
    campo.current?.blur()
  }

  const nueva = () => {
    generacion.current++
    montados.current = 0
    reiniciar()
    setPensando(false)
    setEstado((e) => ({ ...e, mensajes: [] }))
  }

  if (verPerfil) {
    const cerrar = () => setVerPerfil(false)
    return (
      <div className="coach">
        <header className="coach-cabeza">
          <button className="cerrar" onClick={cerrar} aria-label={t().volver}>←</button>
          <h2>{t().perfilCoach}</h2>
        </header>
        <div className="coach-zona">
          <div className="coach-lista">
            {typeof p.perfil === 'function' ? p.perfil(cerrar) : p.perfil}
            <p className="version">{textoVersionApp()}</p>
          </div>
        </div>
      </div>
    )
  }

  const vacia = estado.mensajes.length === 0
  const llaves = claves(estado.mensajes)
  return (
    <div className="coach">
      <header className="coach-cabeza">
        <h2>Coach</h2>
        <button className="chip" onClick={() => setVerPerfil(true)}>{t().perfil}</button>
        {!vacia && <button className="chip" onClick={nueva} disabled={pensando}>{t().nueva}</button>}
      </header>

      {!p.conSesion && (
        <p className="aviso sesion" role="status">{t().sinSesion}</p>
      )}

      <div className="coach-zona">
        <div className="coach-lista" ref={lista} onScroll={alScroll} onTouchStart={alGesto} onWheel={alGesto} onPointerDown={alGesto}>
          <PlanDelDia
            plan={plan}
            estado={estadoPlan}
            pidiendo={pidiendo}
            fallo={falloPlanUi}
            conSesion={p.conSesion}
            nombre={p.nombre}
            sub={p.sub}
            alEmpezar={p.alEmpezarRutina}
            alOtraOpcion={() => void pedirPlan(true)}
            alReintentar={() => void (falloPlanUi?.accion === 'leer' ? leerPlan() : pedirPlan(falloPlanUi?.accion === 'otra'))}
          />

          <ol className="mensajes" aria-live="polite">
            {estado.mensajes.map((m, i) => (
              <li
                key={llaves[i]}
                className={`mensaje ${m.rol}${m.error ? ' fallido' : ''}${i >= montados.current ? ' nuevo' : ''}`}
                // Animado una vez: sin la clase, volver a la pestaña (display none → visible) no lo re-anima.
                onAnimationEnd={(e) => {
                  e.currentTarget.classList.remove('nuevo')
                  montados.current = Math.max(montados.current, i + 1)
                }}
              >
                <p className="burbuja">{m.texto}</p>
                {m.rol === 'coach' && m.herramientas && m.herramientas.length > 0 && (
                  <p className="consulto">{t().consulto(m.herramientas.map(etiquetaHerramienta).join(', '))}</p>
                )}
                {m.rutina && <TarjetaRutina rutina={m.rutina} nombre={p.nombre} sub={p.sub} alEmpezar={p.alEmpezarRutina} />}
                {m.programa && <TarjetaPrograma programa={m.programa} nombre={p.nombre} sub={p.sub} alEmpezar={p.alEmpezarRutina} />}
                {m.error && (
                  <div className="fallo">
                    <p className="error" role="alert">{m.error}</p>
                    <button onClick={() => reintentar(m)} disabled={pensando || !p.conSesion}>{t().reintentar}</button>
                  </div>
                )}
              </li>
            ))}
            {pensando && <Pensando segundos={segundos} />}
          </ol>

          {vacia && !pensando && (
            <div className="sugerencias" role="group" aria-label={t().sugerencias}>
              {SUGERENCIAS.map((s) => {
                // El chip del plan no es un mensaje: lo pide (antes de la hora no hay POST automático); con plan sobra.
                if (s.id === 'plan') {
                  return plan?.actual ? null : (
                    <button key={s.id} className="chip" onClick={() => void pedirPlan(false)} disabled={!p.conSesion || pidiendo || generando}>{s.texto()}</button>
                  )
                }
                return <button key={s.id} className="chip" onClick={() => void enviar(s.texto())} disabled={!p.conSesion}>{s.texto()}</button>
              })}
            </div>
          )}

          <div className="coach-relleno" ref={relleno} />
        </div>
        {lejos && (
          <button
            className={'ir-al-final' + (hayNuevo ? ' con-nuevo' : '')}
            aria-label={hayNuevo ? t().irFinalNuevo : t().irFinal}
            onClick={irAlFinal}
          >
            <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 5v14M5 12l7 7 7-7" /></svg>
          </button>
        )}
      </div>

      <form className="redactar" onSubmit={(e) => {
          e.preventDefault()
          mandarCampo()
        }}
      >
        <textarea
          ref={campo}
          aria-label={t().mensaje}
          placeholder={p.conSesion ? t().pregunta : t().reconecta}
          rows={1}
          maxLength={MAX_TEXTO}
          enterKeyHint="send"
          value={texto}
          readOnly={!p.conSesion}
          onChange={(e) => setTexto(e.target.value)}
          onKeyDown={(e) => {
            // Enter manda; Shift+Enter deja el salto de línea.
            if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) {
              e.preventDefault()
              mandarCampo()
            }
          }}
        />
        <button type="submit" className="primario" onPointerDown={(e) => e.preventDefault()} aria-label={t().enviar} disabled={!p.conSesion || pensando || !texto.trim()}>
          <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 19V5M5 12l7-7 7 7" /></svg>
        </button>
      </form>
    </div>
  )
}
