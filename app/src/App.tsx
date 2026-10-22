import { useCallback, useEffect, useMemo, useRef, useState, useSyncExternalStore, type ComponentProps } from 'react'
import {
  borrarSerie,
  clasificarFallo,
  EVENTO_NO_AUTENTICADO,
  guardarSerie,
  listarEjercicios,
  listarRecords,
  listarSeries,
  type Ejercicio,
  type EjercicioRutina,
  type RecordEjercicio,
  type Rutina,
  type Serie,
  type TipoSerie,
} from './api'
import { confirmarDescartar, profundidad, retroceder, useBotonAtras } from './atras'
import {
  CLIENT_ID, cerrarSesionManual, leerCuentaRecordada, limpiarLegado, mostrarBotonGoogle, recordarCuenta, restaurarSesion,
  vigilarSesion, type CuentaRecordada, type Sesion,
} from './auth'
import { guardarDescanso, leerDescanso } from './contador'
import {
  descansoPara, guardarDescansoActivo, guardarDescansos, leerDescansoActivo, leerDescansos, recordarDescanso, type DescansoActivo,
} from './descanso'
import { guardarCatalogo, leerCatalogo, nombreEjercicio } from './ejercicios'
import { fijarIdioma, guardarIdioma, guardarNombres, leerPrefsIdioma, textos, useIdioma, type Idioma, type NombresEj } from './i18n'
import { msHastaAsesoria } from './asesoria'
import { claveCoach, leerConversacion, tocaAsesoria } from './coach'
import { claveUi, conBorrador, guardarUi, leerUi } from './estadoUi'
import { guardarPlan, leerPlan, moverEnPlan, type Plan } from './plan'
import { fechaLocal, formatoSerie, sumarDias } from './prefill'
import { mensajeFallo } from './presentacion'
import { e1rm, fusionarRecords, recordsBatidos, textoRecord, tipoHeredado } from './records'
import { BACKOFF_ERROR_MS, msHastaMedianoche, proximoReenvio } from './reenvio'
import { claveRutinasCache } from './rutinasCache'
import { derivarSesionUI, type Verificada } from './sesionUI'
import { AlmacenSeries, claveBorrarPendiente, claveSeriesCache, claveSinSubir, type SerieLocal } from './series'
import { crearAvisador } from './sonido'
import { useTeclado } from './teclado'
import {
  agregarAlPlan, guardarAgregados, guardarOrdenInicial, leerAgregados, leerOrdenInicial, nuevosParaRutina, ordenCambiado,
} from './terminarRutina'
import { guardarUnidades, leerUnidades, pesoEnTexto, type Unidad } from './unidades'
import Coach from './pantallas/Coach'
import Configuracion from './pantallas/Configuracion'
import Cuenta from './pantallas/Cuenta'
import HojaSerie from './pantallas/HojaSerie'
import Historial from './pantallas/Historial'
import Hoy from './pantallas/Hoy'
import Mas from './pantallas/Mas'
import MisEjercicios from './pantallas/MisEjercicios'
import ImportarHevy from './pantallas/ImportarHevy'
import { useImportacionHevy } from './useImportacionHevy'
import Navegacion, { type Pestana } from './pantallas/Navegacion'
import PerfilCoach from './pantallas/PerfilCoach'
import Progreso from './pantallas/Progreso'
import Reconectar from './pantallas/Reconectar'
import Records, { type RecordVista } from './pantallas/Records'
import { BarraDescanso } from './pantallas/Relojes'
import Rutinas from './pantallas/Rutinas'
import SelectorEjercicio from './pantallas/SelectorEjercicio'
import TerminarRutina from './pantallas/TerminarRutina'

const t = textos(
  {
    falta: 'Falta VITE_GOOGLE_CLIENT_ID en app/.env.local',
    noEntro: 'No se pudo iniciar sesión. Revisa tu conexión.',
    entra: 'Entra con tu cuenta de Google para registrar tus series.',
    noBorro: 'No se pudo borrar',
    sinLastre: 'Sin lastre',
    yaTienes: (actual: string, nueva: string) => `Ya tienes "${actual}" en curso. ¿Empezar "${nueva}" en su lugar?`,
    omitido: (n: number) => (n === 1 ? 'Se omitió 1 ejercicio que ya no existe' : `Se omitieron ${n} ejercicios que ya no existen`),
    sinEspacio: 'El celular no tiene espacio: las series sin subir podrían perderse. Libera espacio o reconecta.',
    cargasSeguidas: 'Demasiadas cargas seguidas; espera un momento.',
    noHistorial: 'No se pudo cargar el historial.',
    reintentar: 'Reintentar',
    noCargo: 'No se pudo cargar. Revisa tu conexión.',
    cargando: 'Cargando…',
    reconecta: 'Reconecta para cargar los ejercicios.',
    reconectaRecords: 'Reconecta para ver tus récords.',
    ejercicios: 'Ejercicios',
    cargandoEjercicios: 'Cargando ejercicios; intenta de nuevo en un momento',
    deshacer: 'Deshacer',
    guardada: (nombre: string, serie: string) => `${nombre}: ${serie} guardada`,
  },
  {
    falta: 'VITE_GOOGLE_CLIENT_ID is missing in app/.env.local',
    noEntro: "Couldn't sign in. Check your connection.",
    entra: 'Sign in with your Google account to log your sets.',
    noBorro: "Couldn't delete",
    sinLastre: 'No added weight',
    yaTienes: (actual, nueva) => `You already have "${actual}" in progress. Start "${nueva}" instead?`,
    omitido: (n) => (n === 1 ? 'Skipped 1 exercise that no longer exists' : `Skipped ${n} exercises that no longer exist`),
    sinEspacio: 'Your phone is out of space: unsent sets could be lost. Free up space or reconnect.',
    cargasSeguidas: 'Too many loads in a row; wait a moment.',
    noHistorial: "Couldn't load your history.",
    reintentar: 'Retry',
    noCargo: "Couldn't load. Check your connection.",
    cargando: 'Loading…',
    reconecta: 'Reconnect to load exercises.',
    reconectaRecords: 'Reconnect to see your records.',
    ejercicios: 'Exercises',
    cargandoEjercicios: 'Loading exercises; try again in a moment',
    deshacer: 'Undo',
    guardada: (nombre, serie) => `${nombre}: ${serie} saved`,
  },
)

// Importar de Hevy está oculto desde el 2026-10-19; true lo vuelve a mostrar en Más → Datos.
const MOSTRAR_IMPORTAR_HEVY: boolean = false

const DIAS_HISTORIAL = 30
// Por cuenta: en un dispositivo compartido los recientes de uno no se ven en la sesión del otro.
const claveRecientes = (sub: string) => 'gc.recientes:' + sub
const MAX_RECIENTES = 8
// Un segundo toque de "Repetir" tan seguido es casi siempre un doble toque sin querer.
const ANTIRREBOTE_REPETIR_MS = 800

const almacenLocal = (): Storage | null => {
  try {
    return window.localStorage
  } catch {
    return null
  }
}

const leerRecientes = (sub: string): string[] => {
  try {
    const v: unknown = JSON.parse(almacenLocal()?.getItem(claveRecientes(sub)) ?? '[]')
    return Array.isArray(v) ? v.filter((x): x is string => typeof x === 'string') : []
  } catch {
    return []
  }
}

// Versiones anteriores sin dueño (de antes de separar por cuenta). Se descartan.
const CLAVES_VIEJAS = ['gc.sinSubir', 'gc.recientes']

export default function App() {
  // Nombres de ejercicios: preferencia propia, aparte del idioma de la app. La migración corre una vez y fija el idioma
  // (antes de useIdioma, para que el primer render ya lo vea).
  const [nombres, setNombres] = useState<NombresEj>(() => {
    const prefs = leerPrefsIdioma(almacenLocal(), typeof navigator !== 'undefined' ? navigator.language : '')
    fijarIdioma(prefs.idioma)
    return prefs.nombres
  })
  // Un solo useIdioma en la raíz: cambiar de idioma re-renderiza todo el árbol (no hay React.memo).
  useIdioma()
  // La sesión vive en una cookie HttpOnly: aquí solo se sabe quién es, tras preguntarle a la API.
  const [sesion, setSesion] = useState<Sesion | null>(null)
  // pendiente: aún sin respuesta (no se muestra la banda); sin_red: no se pudo preguntar, se confía en la cuenta recordada.
  const [verificada, setVerificada] = useState<Verificada>('pendiente')
  // Última cuenta que entró, recordada en localStorage: si el sistema mata la pestaña o la sesión vence,
  // Principal arranca igual con su estado local y una banda para reconectar. Otra cuenta cambia key y lo rearma.
  const [cuenta, setCuenta] = useState<CuentaRecordada | null>(leerCuentaRecordada)
  // Salir a mano mientras la verificación del arranque vuela: su respuesta no debe resucitar la cuenta.
  const salio = useRef(false)

  useEffect(() => {
    limpiarLegado()
    try {
      for (const k of CLAVES_VIEJAS) localStorage.removeItem(k)
    } catch {
      // sin almacenamiento: nada que limpiar
    }
    let vivo = true
    const aplicar = (r: Sesion | null) => {
      setSesion(r)
      setVerificada(r ? 'ok' : 'ausente')
      // Sin cuenta recordada, la de la cookie. Con otra distinta no se cambia sola: queda Reconectar.
      if (r && !leerCuentaRecordada()) {
        recordarCuenta(r)
        setCuenta(r)
      }
    }
    void restaurarSesion().then((r) => {
      if (!vivo || salio.current) return
      // Sin red tarde no pisa un 'ok' (alEntrar) ni un 'ausente' ya conocidos.
      if (r === 'sin_red') return setVerificada((v) => (v === 'pendiente' ? 'sin_red' : v))
      aplicar(r)
    })
    // Al volver al frente tras mucho tiempo. Sin red no se sabe nada nuevo: se deja lo que había.
    const soltar = vigilarSesion((r) => {
      if (!vivo || salio.current || r === 'sin_red') return
      aplicar(r)
    })
    // Llega muchas veces seguidas (cada reintento sin sesión): idempotente.
    const salir = () => {
      setSesion(null)
      setVerificada('ausente')
    }
    window.addEventListener(EVENTO_NO_AUTENTICADO, salir)
    return () => {
      vivo = false
      soltar()
      window.removeEventListener(EVENTO_NO_AUTENTICADO, salir)
    }
  }, [])

  const alEntrar = useCallback((s: Sesion) => {
    salio.current = false
    setSesion(s)
    setCuenta(s)
    setVerificada('ok')
  }, [])

  // Salida manual: cuenta = null desmonta Principal y se borran los cachés de lectura de esta cuenta.
  // Lo sin subir (gc.sinSubir, gc.borrarPendiente), el plan y el descanso se quedan: son datos, no caché.
  const alSalir = useCallback(() => {
    cerrarSesionManual()
    if (cuenta) {
      for (const k of [claveSeriesCache(cuenta.sub), claveRutinasCache(cuenta.sub), claveUi(cuenta.sub), claveCoach(cuenta.sub)]) {
        try {
          localStorage.removeItem(k)
        } catch {
          // sin almacenamiento: nada que borrar
        }
      }
    }
    salio.current = true
    setSesion(null)
    setVerificada('ausente')
    setCuenta(null)
  }, [cuenta])

  const { delaCuenta, conSesion, mostrarReconectar, esperando } = derivarSesionUI(verificada, sesion, cuenta)

  return cuenta ? (
    <Principal
      key={cuenta.sub}
      sub={cuenta.sub}
      perfil={cuenta}
      sesion={delaCuenta ? sesion : null}
      conSesion={conSesion}
      mostrarReconectar={mostrarReconectar}
      esperando={esperando}
      alSalir={alSalir}
      alEntrar={alEntrar}
      nombres={nombres}
      alCambiarNombres={(n) => {
        setNombres(n)
        guardarNombres(almacenLocal(), n)
      }}
    />
  ) : (
    <Login alEntrar={alEntrar} />
  )
}

function Login({ alEntrar }: { alEntrar: (s: Sesion) => void }) {
  const boton = useRef<HTMLDivElement>(null)
  const [error, setError] = useState<string | null>(CLIENT_ID ? null : t().falta)

  useEffect(() => {
    if (!boton.current || !CLIENT_ID) return
    mostrarBotonGoogle(boton.current, alEntrar, {
      alFallar: () => setError(t().noEntro),
    }).catch((e: Error) => setError(e.message))
  }, [alEntrar])

  return (
    <div className="login" role="dialog" aria-modal="true">
      <h1>gym-coach</h1>
      <p>{t().entra}</p>
      <div ref={boton} />
      {error && <p className="error">{error}</p>}
    </div>
  )
}

interface Hoja {
  ejercicioId: string
  editar?: SerieLocal
}
interface Toast {
  texto: string
  deshacer?: () => void
}
type Carga = 'cargando' | 'ok' | 'error'

interface PropsPrincipal {
  sub: string
  /** Lo recordado de la cuenta: sin sesión verificada, el perfil sale de aquí. */
  perfil: { nombre: string | null; foto: string | null }
  sesion: Sesion | null
  conSesion: boolean
  mostrarReconectar: boolean
  /** Aún sin saber si hay sesión: "Cargando…" en vez de pedir reconectar. */
  esperando: boolean
  alSalir: () => void
  alEntrar: (s: Sesion) => void
  nombres: NombresEj
  alCambiarNombres: (n: NombresEj) => void
}

function Principal({ sub, sesion, conSesion, mostrarReconectar, esperando, alSalir, alEntrar, nombres, alCambiarNombres, ...p }: PropsPrincipal) {
  // Solo para las dependencias de los memos que producen texto; el re-render ya lo provoca App.
  const idioma = useIdioma()
  // Un almacén por cuenta (Principal se rearma con key={sub}): los pendientes de otra cuenta no se ven ni se reenvían.
  const [almacen] = useState(
    () =>
      new AlmacenSeries({
        put: (s) => guardarSerie(s, sub),
        del: (id) => borrarSerie(id, sub),
        clasificar: clasificarFallo,
        almacen: almacenLocal(),
        clave: claveSinSubir(sub),
        claveBorrar: claveBorrarPendiente(sub),
        claveCache: claveSeriesCache(sub),
      }),
  )
  const series = useSyncExternalStore(almacen.suscribir, almacen.lista)

  // El último catálogo bajado deja registrar sin sesión; con sesión se refresca igual.
  const [ejercicios, setEjercicios] = useState<Ejercicio[] | null>(() => leerCatalogo(almacenLocal()))
  const [cargaEjercicios, setCargaEjercicios] = useState<Carga>('cargando')
  const [cargaSeries, setCargaSeries] = useState<Carga>('cargando')
  // Tras un 429 al cargar: Reintentar queda deshabilitado hasta esta hora (epoch ms).
  const [recargaSeries, setRecargaSeries] = useState(0)
  const [esperaSeriesHasta, setEsperaSeriesHasta] = useState<number | null>(null)
  const [hoy, setHoy] = useState(fechaLocal)
  // Lo que había en pantalla cuando el sistema mató la pestaña (solo si es del mismo día).
  const [ui0] = useState(() => leerUi(almacenLocal(), sub, hoy))
  const [pestana, setPestana] = useState<Pestana>(ui0.pestana)
  // Coach se monta la primera vez que se visita y queda montado: conserva su chat al cambiar de pestaña.
  const [coachVisitado, setCoachVisitado] = useState(ui0.pestana === 'coach')
  if (pestana === 'coach' && !coachVisitado) setCoachVisitado(true)
  // Asesoría de las 7:30: se recalcula al montar y al volver a la app (los timers se congelan en segundo plano).
  const [ultimaAsesoria, setUltimaAsesoria] = useState(() => leerConversacion(almacenLocal(), sub, hoy).asesoria)
  const [ahora, setAhora] = useState(() => new Date())
  useEffect(() => {
    const alVisible = () => document.visibilityState === 'visible' && setAhora(new Date())
    document.addEventListener('visibilitychange', alVisible)
    return () => document.removeEventListener('visibilitychange', alVisible)
  }, [])
  // Con la app abierta en primer plano al pasar las 7:30: sin esto solo se enteraría al volver de segundo plano.
  useEffect(() => {
    const ms = msHastaAsesoria(new Date(), hoy)
    if (ms === null) return
    const t = setTimeout(() => setAhora(new Date()), ms + 500)
    return () => clearTimeout(t)
  }, [hoy])
  const asesoriaPendiente = tocaAsesoria(ahora, hoy, ultimaAsesoria)
  // El selector de ejercicio va encima de Entreno (sin barra inferior).
  const [selector, setSelector] = useState(false)
  const teclado = useTeclado()
  // Cambiarla remonta Rutinas: así el atrás cierra el editor, que guarda su estado dentro de Rutinas.
  const [claveRutinas, setClaveRutinas] = useState(0)
  // null = aún sin base del servidor: sin ella no se avisan récords (se compararía solo con 30 días locales).
  const [records, setRecords] = useState<RecordEjercicio[] | null>(null)
  const [errorRecords, setErrorRecords] = useState<string | null>(null)
  const cargaRecords = useRef(0)
  const [hoja, setHoja] = useState<Hoja | null>(ui0.hoja ? { ejercicioId: ui0.hoja } : null)
  const [toast, setToast] = useState<Toast | null>(null)
  const [recientes, setRecientes] = useState(() => leerRecientes(sub))
  const [unidades, setUnidades] = useState(() => leerUnidades(almacenLocal(), sub))
  const [descansoDefecto, setDescansoDefecto] = useState(() => leerDescanso(almacenLocal()))
  const [plan, setPlan] = useState<Plan | null>(() => leerPlan(almacenLocal(), sub, hoy))
  const [agregados, setAgregados] = useState(() => leerAgregados(almacenLocal(), sub, hoy))
  // Por cuenta y persistido: recargar a mitad de descanso lo retoma (el reloj sale de la hora de inicio).
  const [descanso, setDescanso] = useState<DescansoActivo | null>(() => leerDescansoActivo(sub))
  const [prefsDescanso, setPrefsDescanso] = useState(() => leerDescansos(sub))
  const [terminando, setTerminando] = useState<{ nuevos: EjercicioRutina[]; ordenCambiado: boolean } | null>(null)
  const [enfocar, setEnfocar] = useState<string | null>(ui0.ultimoEjercicio)
  // Lo escrito en las filas de Hoy sin guardar, y el último ejercicio tocado (para volver a él).
  const [filas, setFilas] = useState(ui0.filas)
  const [ultimoEjercicio, setUltimoEjercicio] = useState(ui0.ultimoEjercicio)
  const [editandoRutina, setEditandoRutina] = useState(false)
  // Ref: solo se lee al llegar el atrás, no necesita re-render.
  const estadoEditor = useRef({ sucio: false, ocupado: false })
  const alEditandoRutina = useCallback((v: boolean, estado?: { sucio: boolean; ocupado: boolean }) => {
    setEditandoRutina(v)
    estadoEditor.current = v && estado ? estado : { sucio: false, ocupado: false }
  }, [])
  // Uno por Principal: el audio se desbloquea con el toque de guardar y se reutiliza.
  const [avisador] = useState(() => crearAvisador())
  const ultimoRepetir = useRef(new Map<string, number>())

  // El plan en curso sigue hasta Terminar aunque pase la medianoche: sus series van al día en que empezó.
  // (Al recargar, leerPlan sí descarta el de otro día.)
  const diaEntreno = plan?.fecha ?? hoy
  // Los borradores son del día del entreno: al cambiar (medianoche sin plan) se descartan.
  const [diaFilas, setDiaFilas] = useState(diaEntreno)
  if (diaFilas !== diaEntreno) {
    setDiaFilas(diaEntreno)
    setFilas({})
  }
  // Síncrono en cada cambio: el sistema puede matar la pestaña en cualquier momento.
  useEffect(
    () =>
      guardarUi(almacenLocal(), sub, {
        fecha: diaEntreno,
        pestana,
        hoja: hoja && !hoja.editar ? hoja.ejercicioId : null,
        ultimoEjercicio,
        filas,
      }),
    [sub, diaEntreno, pestana, hoja, ultimoEjercicio, filas],
  )

  const porId = useMemo(() => new Map((ejercicios ?? []).map((e) => [e.id, e])), [ejercicios])
  const corporales = useMemo(() => new Set((ejercicios ?? []).filter((e) => e.cargaCorporal).map((e) => e.id)), [ejercicios])
  const nombre = useCallback(
    (id: string) => {
      const e = porId.get(id)
      return e ? nombreEjercicio(e, nombres, idioma) : id
    },
    [porId, nombres, idioma],
  )
  // hasOwn: un id como 'toString' no debe devolver lo heredado de Object.prototype.
  const unidadDe = useCallback((id: string): Unidad => (Object.hasOwn(unidades, id) ? unidades[id] : 'kg'), [unidades])
  const cambiarUnidad = useCallback((id: string, u: Unidad) => setUnidades((m) => ({ ...m, [id]: u })), [])
  // Se persiste fuera del updater (debe ser puro). Al montar reescribe lo recién leído: inocuo.
  useEffect(() => guardarUnidades(almacenLocal(), sub, unidades), [unidades, sub])
  useEffect(() => guardarDescansoActivo(sub, descanso), [descanso, sub])

  // Solo cuenta la última respuesta: una lenta no pisa a una más nueva.
  const recargarRecords = useCallback(() => {
    if (!conSesion) return
    const id = ++cargaRecords.current
    listarRecords(sub)
      .then((r) => {
        if (id !== cargaRecords.current) return
        setRecords(r)
        setErrorRecords(null)
      })
      .catch((e: unknown) => {
        if (id !== cargaRecords.current || clasificarFallo(e).tipo === 'noAutenticado') return
        setErrorRecords(mensajeFallo(e))
      })
  }, [conSesion, sub])
  useEffect(() => recargarRecords(), [recargarRecords])

  // "hoy" cambia a medianoche con la app abierta; al volver de segundo plano se corrige (los timers se congelan).
  useEffect(() => {
    const actualizar = () => setHoy(fechaLocal())
    const t = setTimeout(actualizar, msHastaMedianoche(new Date()) + 1000)
    const alVisible = () => document.visibilityState === 'visible' && actualizar()
    document.addEventListener('visibilitychange', alVisible)
    return () => {
      clearTimeout(t)
      document.removeEventListener('visibilitychange', alVisible)
    }
  }, [hoy])

  // Con sesión (al montar o al volver a entrar), al recuperar red y al volver a la app: reenviar lo que quedó sin subir.
  useEffect(() => {
    if (!conSesion) return
    void almacen.reenviarPendientes()
    const reenviar = () => void almacen.reenviarPendientes()
    const alVisible = () => document.visibilityState === 'visible' && reenviar()
    window.addEventListener('online', reenviar)
    document.addEventListener('visibilitychange', alVisible)
    return () => {
      window.removeEventListener('online', reenviar)
      document.removeEventListener('visibilitychange', alVisible)
    }
  }, [conSesion, almacen])

  // Y por tiempo: al vencer el Retry-After más cercano, o con backoff fijo si hay series en error.
  // Se reprograma con cada cambio de las series (cada intento fallido vuelve a emitir).
  useEffect(() => {
    if (!conSesion) return
    let crudo: string | null = null
    let hayBorrados = false
    try {
      crudo = almacenLocal()?.getItem(claveSinSubir(sub)) ?? null
      // Un DELETE pendiente sin series por subir también se reintenta con el backoff fijo.
      const borrar: unknown = JSON.parse(almacenLocal()?.getItem(claveBorrarPendiente(sub)) ?? '[]')
      hayBorrados = Array.isArray(borrar) && borrar.length > 0
    } catch {
      return
    }
    const ahora = Date.now()
    const cuando = proximoReenvio(crudo, ahora) ?? (hayBorrados ? ahora + BACKOFF_ERROR_MS : null)
    if (cuando === null) return
    const t = setTimeout(() => void almacen.reenviarPendientes(), Math.max(0, cuando - Date.now()))
    return () => clearTimeout(t)
  }, [conSesion, almacen, series, sub])

  // Al volver la sesión (token vencido a mitad de uso) se refresca lo ya cargado; al montar ya está en 'cargando'.
  const [sesionPrevia, setSesionPrevia] = useState(conSesion)
  if (sesionPrevia !== conSesion) {
    setSesionPrevia(conSesion)
    if (conSesion) {
      setCargaEjercicios('cargando')
      setCargaSeries('cargando')
    }
  }

  // Catálogo y series cargan por separado: sin historial igual se puede registrar.
  // Solo cargan en estado 'cargando'; Reintentar vuelve a ese estado.
  useEffect(() => {
    if (!conSesion || cargaEjercicios !== 'cargando') return
    let vivo = true
    listarEjercicios()
      .then((ejs) => {
        if (!vivo) return
        setEjercicios(ejs)
        guardarCatalogo(almacenLocal(), ejs)
        setCargaEjercicios('ok')
      })
      .catch((e: unknown) => vivo && clasificarFallo(e).tipo !== 'noAutenticado' && setCargaEjercicios('error'))
    return () => {
      vivo = false
    }
  }, [conSesion, cargaEjercicios])

  // Hoy e Historial salen de una sola carga de los últimos 30 días.
  useEffect(() => {
    if (!conSesion || cargaSeries !== 'cargando') return
    let vivo = true
    // Lo que cambie localmente mientras el GET vuela no se pisa con su respuesta.
    const generacion = almacen.generacion()
    const desde = sumarDias(hoy, -(DIAS_HISTORIAL - 1))
    listarSeries(desde, hoy, sub)
      .then((srv) => {
        if (!vivo) return
        // Con el rango del GET: lo que el servidor ya no tiene sale también del caché.
        almacen.fusionarServidor(srv, generacion, { desde, hasta: hoy })
        setCargaSeries('ok')
      })
      .catch((e: unknown) => {
        if (!vivo) return
        const f = clasificarFallo(e)
        if (f.tipo === 'noAutenticado') return // se reintenta solo al volver a entrar
        if (f.tipo === 'limite') setEsperaSeriesHasta(Date.now() + f.esperaMs)
        setCargaSeries('error')
      })
    return () => {
      vivo = false
    }
  }, [conSesion, cargaSeries, recargaSeries, almacen, hoy, sub])

  useEffect(() => {
    if (esperaSeriesHasta === null) return
    const t = setTimeout(() => setEsperaSeriesHasta(null), Math.max(0, esperaSeriesHasta - Date.now()))
    return () => clearTimeout(t)
  }, [esperaSeriesHasta])

  useEffect(() => {
    if (!toast) return
    const t = setTimeout(() => setToast(null), 5000)
    return () => clearTimeout(t)
  }, [toast])

  const reintentarCarga = () => {
    setCargaEjercicios((c) => (c === 'error' ? 'cargando' : c))
    setCargaSeries((c) => (c === 'error' ? 'cargando' : c))
  }

  const guardar = useCallback(
    (s: Serie) => {
      const subida = almacen.guardar(s)
      setRecientes((rs) => {
        const nuevos = [s.ejercicioId, ...rs.filter((r) => r !== s.ejercicioId)].slice(0, MAX_RECIENTES)
        try {
          localStorage.setItem(claveRecientes(sub), JSON.stringify(nuevos))
        } catch {
          // sin almacenamiento: los recientes duran la sesión
        }
        return nuevos
      })
      return subida
    },
    [almacen, sub],
  )

  const borrar = useCallback(
    (id: string) => {
      void almacen.borrar(id).then((ok) => {
        if (!ok) setToast({ texto: t().noBorro })
        else recargarRecords()
      })
    },
    [almacen, recargarRecords],
  )

  /** Aviso de récord de una serie NUEVA (antes de guardarla: compara contra lo anterior), o null. */
  const avisoRecord = (s: Serie): string | null => {
    if (records === null) return null
    const corporal = corporales.has(s.ejercicioId)
    const tipos = recordsBatidos(s, records, almacen.lista(), corporal)
    if (tipos.length === 0) return null
    const u = unidadDe(s.ejercicioId)
    const kg = e1rm(s.pesoKg, s.reps)
    const texto = textoRecord({
      tipos,
      reps: s.reps,
      pesoTexto: corporal && s.pesoKg === 0 ? null : `${pesoEnTexto(s.pesoKg, u)} ${u}`,
      e1rmTexto: kg === null ? null : `${pesoEnTexto(kg, u)} ${u}`,
    })
    return `${nombre(s.ejercicioId)}: ${texto}`
  }

  /** Serie NUEVA (no edición): se llama dentro del toque, desbloquea el audio y arranca el descanso. Devuelve el aviso de récord, si hubo. */
  const registrar = (s: Serie): string | null => {
    avisador.desbloquear()
    const aviso = avisoRecord(s)
    void guardar(s)
    const duracionS = descansoPara(s.ejercicioId, prefsDescanso, plan?.ejercicios ?? null, descansoDefecto)
    // 0 = sin descanso: ni cuenta ni sonido.
    setDescanso(duracionS > 0 ? { ejercicioId: s.ejercicioId, inicio: Date.now(), duracionS } : null)
    if (aviso) {
      navigator.vibrate?.(40)
      setToast({
        texto: aviso,
        deshacer: () => {
          borrar(s.id)
          setDescanso(null)
        },
      })
    }
    return aviso
  }

  // Cada cambio de duración (±15, preset) queda como preferencia de ESE ejercicio.
  const cambiarDescanso = (d: DescansoActivo) => {
    setDescanso(d)
    if (descanso && d.duracionS !== descanso.duracionS) {
      const nuevas = recordarDescanso(prefsDescanso, d.ejercicioId, d.duracionS)
      if (nuevas !== prefsDescanso) {
        setPrefsDescanso(nuevas)
        guardarDescansos(sub, nuevas)
      }
    }
  }

  const rutinaDe = (ejercicioId: string) =>
    plan?.ejercicios.some((e) => e.id === ejercicioId) ? { rutina: plan.nombre } : {}

  // Con plan en curso, toda serie nueva va a su día (si no, tras la medianoche desaparecería de Hoy).
  const delEntreno = (s: Serie): Serie => ({ ...s, fecha: plan ? plan.fecha : s.fecha, ...rutinaDe(s.ejercicioId) })

  const nuevaSerie = (ejercicioId: string, pesoKg: number, reps: number, ahora = Date.now(), tipoSerie?: TipoSerie): Serie =>
    delEntreno({
      id: crypto.randomUUID(),
      ejercicioId,
      pesoKg,
      reps,
      fecha: fechaLocal(),
      ts: new Date(ahora).toISOString(),
      ...(tipoSerie && tipoSerie !== 'normal' ? { tipoSerie } : {}),
    })

  const repetir = (bloque: SerieLocal[]) => {
    const ultima = bloque[bloque.length - 1]
    const ahora = Date.now()
    if (ahora - (ultimoRepetir.current.get(ultima.ejercicioId) ?? 0) < ANTIRREBOTE_REPETIR_MS) return
    ultimoRepetir.current.set(ultima.ejercicioId, ahora)

    // Solo el calentamiento se repite como tal; fallo y drop dependen del día.
    const nueva = nuevaSerie(ultima.ejercicioId, ultima.pesoKg, ultima.reps, ahora, tipoHeredado(ultima.tipoSerie))
    // Con récord, registrar ya dejó su aviso (con Deshacer) en el toast.
    if (registrar(nueva)) return
    setToast({
      texto: t().guardada(nombre(nueva.ejercicioId), formatoSerie(nueva, unidadDe(nueva.ejercicioId))),
      deshacer: () => {
        borrar(nueva.id)
        setDescanso(null)
      },
    })
  }

  const cambiarPlan = (p: Plan | null, ids: string[]) => {
    setPlan(p)
    setAgregados(ids)
    guardarPlan(almacenLocal(), sub, p)
    guardarAgregados(almacenLocal(), sub, p ? { fecha: p.fecha, ids } : null)
  }

  const empezar = (r: Rutina, propuesta = false) => {
    if (plan && !confirm(t().yaTienes(plan.nombre, r.nombre))) return
    // Un ejercicio que salió del catálogo no se puede registrar (la API lo rechaza): fuera del plan.
    const vigentes = r.ejercicios.filter((e) => porId.has(e.id))
    const omitidos = r.ejercicios.length - vigentes.length
    if (omitidos > 0) {
      setToast({ texto: t().omitido(omitidos) })
    }
    cambiarPlan({ fecha: hoy, rutinaId: r.id, nombre: r.nombre, ejercicios: vigentes, inicio: Date.now(), ...(propuesta ? { propuesta: true as const } : {}) }, [])
    // Para ofrecer al terminar el nuevo orden, si lo cambia.
    guardarOrdenInicial(almacenLocal(), sub, { fecha: hoy, ids: vigentes.map((e) => e.id) })
    setDescanso(null)
    // Lo escrito a medias era de la rutina anterior.
    setFilas({})
    setUltimoEjercicio(null)
    setPestana('entreno')
  }

  // Solo el plan del día; la rutina guardada cambia únicamente si el usuario lo acepta al terminar.
  const moverEjercicio = (id: string, dir: -1 | 1) => {
    if (!plan) return
    const nuevo = moverEnPlan(plan, id, dir)
    if (nuevo === plan) return
    cambiarPlan(nuevo, agregados)
    setEnfocar(id)
  }

  const elegirEjercicio = (id: string) => {
    setSelector(false)
    if (plan) {
      const con = agregarAlPlan(plan, id, descansoDefecto)
      if (plan.ejercicios.some((e) => e.id === id) || con !== plan) {
        // Ya estaba: solo se enfoca. Nuevo: al final del plan, recordado aparte para ofrecerlo al terminar.
        if (con !== plan) cambiarPlan(con, [...agregados, id])
        setEnfocar(id)
        return
      }
      // Plan lleno (15): se registra fuera del plan, como sin rutina.
    }
    setHoja({ ejercicioId: id })
  }

  const avisoSeries = almacen.sinEspacio ? (
    <div className="aviso" role="alert">
      <span>{t().sinEspacio}</span>
    </div>
  ) : cargaSeries === 'error' ? (
      <div className="aviso">
        <span>
          {esperaSeriesHasta !== null ? t().cargasSeguidas : t().noHistorial}
        </span>
        <button onClick={reintentarCarga} disabled={esperaSeriesHasta !== null}>
          {t().reintentar}
        </button>
      </div>
    ) : null

  const editar = (s: SerieLocal) => setHoja({ ejercicioId: s.ejercicioId, editar: s })
  // Estable: Hoy lo usa como dependencia de su efecto de scroll.
  const alEnfocado = useCallback(() => setEnfocar(null), [])

  const perfil = sesion ?? p.perfil
  const pendientes = series.filter((s) => s.estado === 'pendiente' || s.estado === 'error').length

  const vistaRecords = useMemo(
    (): RecordVista[] =>
      fusionarRecords(records ?? [], series).map((r) => {
        const kg = (v: number) => {
          const u = unidadDe(r.ejercicioId)
          // Carga corporal sin lastre: 0 kg no dice nada.
          return corporales.has(r.ejercicioId) && v === 0 ? t().sinLastre : `${pesoEnTexto(v, u)} ${u}`
        }
        return {
          ejercicioId: r.ejercicioId,
          nombre: nombre(r.ejercicioId),
          pesoMax: { texto: kg(r.pesoMax.pesoKg), reps: r.pesoMax.reps, fecha: r.pesoMax.fecha },
          e1rm: r.e1rm ? { texto: kg(r.e1rm.kg) } : null,
          repsMax: { reps: r.repsMax.reps, texto: kg(r.repsMax.pesoKg), fecha: r.repsMax.fecha },
        }
      }),
    // nombre cambia con el idioma, así que también renueva los textos de aquí.
    [records, series, nombre, unidadDe, corporales],
  )

  // Aquí (no en Más) para que la importación siga corriendo al cambiar de pestaña.
  const importacion = useImportacionHevy(sub, series, ({ unidad, ejerciciosImportados }) => {
    // Mismo mecanismo que la carga inicial; el contador fuerza una nueva aunque ya hubiera un GET en vuelo.
    setCargaSeries('cargando')
    setRecargaSeries((n) => n + 1)
    recargarRecords()
    if (unidad === 'lb') {
      // Solo los que no tenían preferencia: no se pisa lo que el usuario ya eligió.
      setUnidades((m) => {
        const nuevo = { ...m }
        for (const id of ejerciciosImportados) if (!Object.hasOwn(nuevo, id)) nuevo[id] = 'lb'
        return nuevo
      })
    }
  })

  // Coach va aparte (siempre montado, oculto): no depende de que el catálogo haya cargado.
  const contenido =
    pestana === 'coach' ? null : pestana === 'mas' ? (
      <Mas
        configuracion={
          <Configuracion
            idioma={idioma}
            alCambiarIdioma={(i: Idioma) => {
              fijarIdioma(i)
              guardarIdioma(almacenLocal(), i)
            }}
            nombres={nombres}
            alCambiarNombres={alCambiarNombres}
            descansoS={descansoDefecto}
            alCambiarDescanso={(s) => {
              setDescansoDefecto(s)
              guardarDescanso(almacenLocal(), s)
            }}
          />
        }
        datos={MOSTRAR_IMPORTAR_HEVY ? <ImportarHevy imp={importacion} /> : undefined}
        cuenta={<Cuenta nombre={perfil.nombre} foto={perfil.foto} pendientes={pendientes} alSalir={alSalir} />}
      />
    ) : ejercicios === null ? (
      cargaEjercicios === 'error' ? (
        <div className="vacio">
          <p>{t().noCargo}</p>
          <button className="primario" onClick={reintentarCarga}>
            {t().reintentar}
          </button>
        </div>
      ) : conSesion || esperando ? (
        <p className="vacio">{t().cargando}</p>
      ) : (
        <p className="vacio">{t().reconecta}</p>
      )
    ) : selector ? (
      <SelectorEjercicio
        ejercicios={ejercicios}
        nombres={nombres}
        recientes={recientes}
        alElegir={elegirEjercicio}
        alCerrar={() => setSelector(false)}
      />
    ) : pestana === 'rutinas' ? (
      <Rutinas
        key={claveRutinas}
        sub={sub}
        ejercicios={ejercicios}
        nombres={nombres}
        nombre={nombre}
        unidadDe={unidadDe}
        recientes={recientes}
        conSesion={conSesion}
        alEmpezar={(r) => empezar(r)}
        alEditando={alEditandoRutina}
      />
    ) : pestana === 'progreso' ? (
      <Progreso
        historial={
          <>
            {avisoSeries}
            <Historial
              series={series}
              ejercicios={porId}
              nombre={nombre}
              unidadDe={unidadDe}
              hoy={hoy}
              dias={DIAS_HISTORIAL}
              alEditar={editar}
              cargarVentana={conSesion ? (d, h) => listarSeries(d, h, sub) : undefined}
              cargando={cargaSeries === 'cargando' && conSesion}
            />
          </>
        }
        records={
          <VistaRecords
            recargar={recargarRecords}
            records={vistaRecords}
            cargando={records === null && errorRecords === null && (conSesion || esperando)}
            error={records === null ? (conSesion ? errorRecords : esperando ? null : t().reconectaRecords) : null}
          />
        }
        extra={[
          { clave: 'ejercicios', titulo: t().ejercicios, nodo: <MisEjercicios sub={sub} conSesion={conSesion} hoy={hoy} unidadDe={unidadDe} /> },
        ]}
      />
    ) : (
      <>
        {avisoSeries}
        <Hoy
          series={series.filter((s) => s.fecha === diaEntreno)}
          historico={series}
          ejercicios={porId}
          nombre={nombre}
          unidadDe={unidadDe}
          alCambiarUnidad={cambiarUnidad}
          plan={plan}
          hoy={diaEntreno}
          enfocar={enfocar}
          alEnfocado={alEnfocado}
          alRepetir={repetir}
          alAgregarSerie={(ejercicioId) => setHoja({ ejercicioId })}
          alGuardarFila={(ejercicioId, pesoKg, reps, tipo) => {
            setUltimoEjercicio(ejercicioId)
            void registrar(nuevaSerie(ejercicioId, pesoKg, reps, Date.now(), tipo))
          }}
          borradores={filas}
          alBorrador={(id, i, b) => {
            setFilas((f) => conBorrador(f, id, i, b))
            setUltimoEjercicio(id)
          }}
          alMover={moverEjercicio}
          alEditar={editar}
          alAgregarEjercicio={() => {
            setEnfocar(null)
            setSelector(true)
          }}
          alTerminar={() =>
            plan &&
            setTerminando({
              nuevos: nuevosParaRutina(series, plan, agregados),
              ordenCambiado: ordenCambiado(leerOrdenInicial(almacenLocal(), sub, plan.fecha), plan),
            })
          }
        />
      </>
    )

  // Si el ejercicio no está en el catálogo (p.ej. una serie rechazada por eso), la hoja igual abre para editarla o borrarla.
  const idHoja = hoja?.ejercicioId
  const ejercicioHoja = useMemo(
    (): Ejercicio | undefined =>
      idHoja === undefined
        ? undefined
        : (porId.get(idHoja) ?? { id: idHoja, nombre: idHoja, nombreEn: idHoja, grupo: 'otro', equipo: 'libre' }),
    [idHoja, porId],
  )
  const finDescanso = useCallback(
    (avisar: boolean) => {
      // Un descanso recuperado que terminó hace rato se quita sin sonar.
      if (avisar) avisador.sonar()
      setDescanso(null)
    },
    [avisador],
  )

  // Atrás de Android: retrocede dentro de la app. Sin nada abierto en Entreno, sale como siempre.
  const editorAbierto = pestana === 'rutinas' && editandoRutina
  // Con teclado la nav estorba (interactive-widget=resizes-content ya achica la pantalla): mismo mecanismo que selector/editor.
  const barraOculta = selector || editorAbierto || teclado
  const sobrepuesta = selector || editorAbierto || hoja !== null || terminando !== null
  useBotonAtras(profundidad(pestana !== 'entreno', sobrepuesta), (nuevo) => {
    const r = retroceder(pestana !== 'entreno', sobrepuesta, nuevo)
    if (editorAbierto && !r.sobrepuesta) {
      // Mismo trato que la flecha ← del editor: guardando no se sale; con cambios, se pregunta (false = el hook repone el nivel).
      const e = estadoEditor.current
      if (e.ocupado || (e.sucio && !confirm(confirmarDescartar()))) return false
    }
    if (!r.sobrepuesta) {
      setSelector(false)
      setHoja(null)
      setTerminando(null)
      if (editorAbierto) setClaveRutinas((k) => k + 1)
    }
    if (!r.enOtraPestana) setPestana('entreno')
  })

  return (
    <div className={`app${pestana === 'coach' ? ' en-coach' : ''}${barraOculta ? ' sin-barra' : ''}${descanso ? ' con-descanso' : ''}`}>
      {mostrarReconectar && <Reconectar alEntrar={alEntrar} />}
      <main>
        {contenido}
        {coachVisitado && (
          <div className="coach-caja" hidden={pestana !== 'coach'}>
            <Coach
              sub={sub}
              conSesion={conSesion}
              hoy={hoy}
              nombre={nombre}
              autoPlan={asesoriaPendiente && pestana === 'coach'}
              alPlanVisto={() => setUltimaAsesoria(hoy)}
              // Sin catálogo, empezar() descartaría todos los ejercicios por "inexistentes".
              alEmpezarRutina={(r: Rutina, guardada?: boolean) => (ejercicios === null ? setToast({ texto: t().cargandoEjercicios }) : empezar(r, !guardada))}
              perfil={(cerrar) => <PerfilCoach sub={sub} conSesion={conSesion} alCerrar={cerrar} />}
            />
          </div>
        )}
      </main>
      {descanso && (
        <BarraDescanso descanso={descanso} alCambiar={cambiarDescanso} alSaltar={() => setDescanso(null)} alFin={finDescanso} />
      )}
      {/* Antes de las hojas: comparten z-index y el fondo de la hoja debe quedar encima. */}
      <Navegacion activa={pestana} alElegir={setPestana} oculta={barraOculta} avisoCoach={asesoriaPendiente && conSesion && pestana !== 'coach'} />

      {hoja && ejercicioHoja && (
        <HojaSerie
          ejercicio={ejercicioHoja}
          nombre={nombre(ejercicioHoja.id)}
          unidad={unidadDe(ejercicioHoja.id)}
          alCambiarUnidad={(u) => cambiarUnidad(ejercicioHoja.id, u)}
          editar={hoja.editar}
          locales={series.filter((s) => s.ejercicioId === hoja.ejercicioId)}
          hoy={diaEntreno}
          sub={sub}
          conSesion={conSesion}
          alGuardar={(s) => {
            if (hoja.editar) {
              // Los récords del servidor se recalculan solo si la edición llegó.
              void guardar(s).then(() => almacen.lista().find((x) => x.id === s.id)?.estado === 'ok' && recargarRecords())
            } else void registrar(delEntreno(s))
            setHoja(null)
          }}
          alBorrar={(id) => {
            borrar(id)
            setHoja(null)
          }}
          alCerrar={() => setHoja(null)}
        />
      )}

      {terminando && plan && (
        <TerminarRutina
          sub={sub}
          plan={plan}
          series={series}
          hoy={diaEntreno}
          nuevos={terminando.nuevos}
          ordenCambiado={terminando.ordenCambiado}
          corporales={corporales}
          nombre={nombre}
          unidadDe={unidadDe}
          alSeguir={() => setTerminando(null)}
          alListo={() => {
            cambiarPlan(null, [])
            setFilas({})
            guardarOrdenInicial(almacenLocal(), sub, null)
            setTerminando(null)
            setDescanso(null)
          }}
        />
      )}

      {toast && (
        <div className="toast" role="status">
          <span>{toast.texto}</span>
          {toast.deshacer && (
            <button
              onClick={() => {
                toast.deshacer?.()
                setToast(null)
              }}
            >
              {t().deshacer}
            </button>
          )}
        </div>
      )}
    </div>
  )
}

/** Pide los récords al abrirse: Progreso solo monta esta vista cuando se elige Récords. */
function VistaRecords({ recargar, ...p }: { recargar: () => void } & ComponentProps<typeof Records>) {
  useEffect(() => recargar(), [recargar])
  return <Records {...p} />
}
