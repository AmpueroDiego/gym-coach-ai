import { afterEach, describe, expect, it } from 'vitest'
import { DemasiadasPeticiones, NoAutenticado, Rechazada, type RutinaPropuesta } from './api'
import {
  claveCoach,
  debePedirPlanSolo,
  debeRepedirPorIdioma,
  etiquetaHerramienta,
  falloPlan,
  getVigente,
  guardarConversacion,
  herramientasValidas,
  leerConversacion,
  limpiarTexto,
  marcarAsesoria,
  paraEnviar,
  planValido,
  programaValido,
  rutinaValida,
  SUGERENCIAS,
  textoEspera,
  textoError,
  tocaAsesoria,
  type MensajeCoach,
} from './coach'
import { fijarIdioma } from './i18n'

const HOY = '2026-10-09'
const AYER = '2026-10-08'

const almacen = (texto?: string) => {
  const m = new Map<string, string>()
  if (texto !== undefined) m.set(claveCoach('u1'), texto)
  return { getItem: (k: string) => m.get(k) ?? null, setItem: (k: string, v: string) => void m.set(k, v), m }
}

let n = 0
const msg = (rol: MensajeCoach['rol'], texto: string, extra: Partial<MensajeCoach> = {}): MensajeCoach => ({
  rol, texto, ts: new Date(Date.UTC(2026, 9, 9, 10, 0, 0, n++)).toISOString(), ...extra,
})

const rutina: RutinaPropuesta = {
  id: 'r1', nombre: 'Empuje', actualizada: '2026-10-09T10:00:00Z', minutosEstimados: 26,
  ejercicios: [{ id: 'press-banca', series: 4, reps: 8, pesoKg: 60, descansoS: 90 }],
}

describe('leerConversacion / guardarConversacion', () => {
  it('hace ida y vuelta', () => {
    const a = almacen()
    const estado = { fecha: HOY, mensajes: [msg('usuario', 'hola'), msg('coach', 'qué tal', { herramientas: ['progreso'], rutina })] }
    guardarConversacion(a, 'u1', estado)
    expect(leerConversacion(a, 'u1', HOY)).toEqual(estado)
  })
  it('basura, JSON roto o sin almacenamiento dan la vacía', () => {
    expect(leerConversacion(almacen('{roto'), 'u1', HOY)).toEqual({ fecha: HOY, mensajes: [] })
    expect(leerConversacion(almacen('[1,2]'), 'u1', HOY)).toEqual({ fecha: HOY, mensajes: [] })
    expect(leerConversacion(null, 'u1', HOY)).toEqual({ fecha: HOY, mensajes: [] })
    const t = JSON.stringify({ fecha: HOY, mensajes: [7, { rol: 'sistema', texto: 'x', ts: 't' }, { rol: 'usuario', texto: 3, ts: 't' }, msg('usuario', 'ok')] })
    expect(leerConversacion(almacen(t), 'u1', HOY).mensajes.map((m) => m.texto)).toEqual(['ok'])
  })
  it('otro día descarta los mensajes pero conserva la asesoría', () => {
    const a = almacen()
    guardarConversacion(a, 'u1', { fecha: AYER, mensajes: [msg('usuario', 'viejo')], asesoria: AYER })
    expect(leerConversacion(a, 'u1', HOY)).toEqual({ fecha: HOY, mensajes: [], asesoria: AYER })
  })
  it('otra cuenta no ve la conversación', () => {
    const a = almacen()
    guardarConversacion(a, 'u1', { fecha: HOY, mensajes: [msg('usuario', 'mío')] })
    expect(leerConversacion(a, 'u2', HOY).mensajes).toEqual([])
  })
  it('guarda como mucho los últimos 40', () => {
    const a = almacen()
    const mensajes = Array.from({ length: 45 }, (_, i) => msg('usuario', `m${i}`))
    guardarConversacion(a, 'u1', { fecha: HOY, mensajes })
    const leidos = leerConversacion(a, 'u1', HOY).mensajes
    expect(leidos).toHaveLength(40)
    expect(leidos[0].texto).toBe('m5')
    expect(leidos[39].texto).toBe('m44')
  })
  it('una rutina malformada se descarta sin perder el mensaje', () => {
    const malas = [
      { ...rutina, ejercicios: [] },
      { ...rutina, ejercicios: Array.from({ length: 16 }, (_, i) => ({ ...rutina.ejercicios[0], id: `e${i}` })) },
      { ...rutina, ejercicios: [{ ...rutina.ejercicios[0], id: 5 }] },
      { ...rutina, minutosEstimados: '26' },
      'rutina',
    ]
    for (const r of malas) {
      const t = JSON.stringify({ fecha: HOY, mensajes: [{ ...msg('coach', 'toma'), rutina: r }] })
      const [m] = leerConversacion(almacen(t), 'u1', HOY).mensajes
      expect(m.texto).toBe('toma')
      expect(m.rutina).toBeUndefined()
    }
  })
  it('la marca de asesoría no retrocede al guardar un estado sin ella', () => {
    const a = almacen()
    marcarAsesoria(a, 'u1', HOY)
    guardarConversacion(a, 'u1', { fecha: HOY, mensajes: [msg('usuario', 'hola')] })
    expect(leerConversacion(a, 'u1', HOY).asesoria).toBe(HOY)
  })
})

describe('marcarAsesoria', () => {
  it('significa plan visto hoy: persiste la fecha y conserva los mensajes de hoy', () => {
    const a = almacen()
    guardarConversacion(a, 'u1', { fecha: HOY, mensajes: [msg('usuario', 'hola')] })
    marcarAsesoria(a, 'u1', HOY)
    const e = leerConversacion(a, 'u1', HOY)
    expect(e.asesoria).toBe(HOY)
    expect(e.mensajes.map((m) => m.texto)).toEqual(['hola'])
  })
})

describe('tocaAsesoria', () => {
  const a = (h: number, m: number) => new Date(2026, 9, 9, h, m)
  it('antes de las 7:30 no toca', () => {
    expect(tocaAsesoria(a(7, 29), HOY, undefined)).toBe(false)
  })
  it('desde las 7:30 toca si hoy no se hizo', () => {
    expect(tocaAsesoria(a(7, 30), HOY, undefined)).toBe(true)
    expect(tocaAsesoria(a(22, 0), HOY, null)).toBe(true)
  })
  it('ya hecha hoy no toca', () => {
    expect(tocaAsesoria(a(9, 0), HOY, HOY)).toBe(false)
  })
  it('la de otro día no cuenta', () => {
    expect(tocaAsesoria(a(9, 0), HOY, AYER)).toBe(true)
  })
})

describe('paraEnviar', () => {
  it('quita los fallidos, deja los últimos 10 y termina en el usuario', () => {
    const ms = [
      ...Array.from({ length: 12 }, (_, i) => msg(i % 2 ? 'coach' : 'usuario', `m${i}`)),
      msg('usuario', 'falló', { error: 'El coach no respondió. Reintenta.' }),
      msg('usuario', 'último'),
    ]
    const r = paraEnviar(ms)
    expect(r).toHaveLength(10)
    expect(r.some((m) => m.texto === 'falló')).toBe(false)
    expect(r[9]).toEqual({ rol: 'usuario', texto: 'último' })
  })
  it('corta al coach a 2000 y solo manda rol y texto', () => {
    const r = paraEnviar([msg('usuario', 'a'), msg('coach', 'x'.repeat(2500), { herramientas: ['progreso'], rutina }), msg('usuario', 'b')])
    expect(r[1]).toEqual({ rol: 'coach', texto: 'x'.repeat(2000) })
  })
  it('sin respuesta del coach al final y sin textos vacíos', () => {
    expect(paraEnviar([msg('usuario', 'a'), msg('coach', '  '), msg('coach', 'b')])).toEqual([{ rol: 'usuario', texto: 'a' }])
  })
  it('quita lo que el backend rechaza (ancho cero, bidi, surrogate partido) pero deja los saltos de línea', () => {
    const r = paraEnviar([msg('usuario', 'a​b‮c\nd'), msg('coach', 'x'.repeat(1999) + '😀'), msg('usuario', 'z')])
    expect(r[0].texto).toBe('abc\nd')
    expect(r[1].texto).toBe('x'.repeat(1999))
  })
})

describe('textoError', () => {
  it('cada fallo con su texto', () => {
    expect(textoError(new NoAutenticado())).toBe('Reconecta para hablar con el coach.')
    expect(textoError(new DemasiadasPeticiones(12_000))).toBe('Espera 12 s.')
    expect(textoError(new DemasiadasPeticiones(300_000))).toBe('Llegaste al tope de mensajes de hoy. Vuelve más tarde.')
    expect(textoError(new Error('coach_no_configurado'))).toBe('El coach aún no está disponible.')
    expect(textoError(new Error('HTTP 503'))).toBe('El coach no respondió. Reintenta.')
  })
})

it('hay 3 sugerencias y la primera es el chip del plan, no un mensaje de chat', () => {
  expect(SUGERENCIAS).toHaveLength(3)
  expect(SUGERENCIAS[0].id).toBe('plan')
  expect(SUGERENCIAS[0].texto()).toBe('Tu entreno de hoy')
})

describe('en inglés', () => {
  afterEach(() => fijarIdioma('es'))
  it('sugerencias con el mismo id y texto en inglés', () => {
    fijarIdioma('en')
    expect(SUGERENCIAS.map((s) => s.id)).toEqual(['plan', 'banca', 'descuido'])
    expect(SUGERENCIAS[0].texto()).toBe("Today's workout")
  })
  it('errores del chat, del plan, espera y herramientas', () => {
    fijarIdioma('en')
    expect(textoError(new NoAutenticado())).toBe('Reconnect to talk to the coach.')
    expect(textoError(new DemasiadasPeticiones(12_000))).toBe('Wait 12 s.')
    expect(textoError(new Error('HTTP 503'))).toBe("The coach didn't respond. Try again.")
    expect(falloPlan(new Rechazada('tope_variaciones'))).toEqual({ texto: "You've used today's 3 options.", reintentable: false })
    expect(falloPlan(new Error('HTTP 503')).texto).toBe("Couldn't prepare today's workout.")
    expect(textoEspera(0)).toBe('Thinking…')
    expect(etiquetaHerramienta('mis_ejercicios')).toBe('your exercises')
    expect(etiquetaHerramienta('constructor')).toBe('constructor')
  })
})

describe('idiomaPlan', () => {
  it('se guarda solo para el día y se conserva al marcar la asesoría', () => {
    const t = almacen()
    guardarConversacion(t, 'u1', { fecha: HOY, mensajes: [], idiomaPlan: 'en' })
    marcarAsesoria(t, 'u1', HOY)
    expect(leerConversacion(t, 'u1', HOY).idiomaPlan).toBe('en')
    expect(leerConversacion(t, 'u1', '2026-10-10').idiomaPlan).toBeUndefined()
  })
  it('un valor basura se ignora', () => {
    const t = almacen(JSON.stringify({ fecha: HOY, mensajes: [], idiomaPlan: 'fr' }))
    expect(leerConversacion(t, 'u1', HOY).idiomaPlan).toBeUndefined()
  })
})

it('mis_ejercicios tiene etiqueta propia', () => {
  expect(etiquetaHerramienta('mis_ejercicios')).toBe('tus ejercicios')
})

describe('planValido', () => {
  const rut = { id: 'r1', nombre: 'Empuje', actualizada: '', minutosEstimados: 26, ejercicios: [{ id: 'press-banca', series: 4, reps: 8, pesoKg: null, descansoS: 90 }] }
  const bueno = { fecha: HOY, numero: 1, restantes: 2, generando: false, actual: { texto: 'Hola', herramientas: ['progreso'], rutina: rut, creado: '2026-10-09T13:00:00Z' } }
  it('acepta un plan bueno, uno sin plan y uno generando', () => {
    expect(planValido(bueno)?.actual?.rutina?.nombre).toBe('Empuje')
    expect(planValido({ ...bueno, actual: null, numero: 0, restantes: 3, generando: true })).toEqual({ fecha: HOY, actual: null, numero: 0, restantes: 3, generando: true })
  })
  it('rechaza formas rotas', () => {
    expect(planValido(null)).toBeNull()
    expect(planValido({ ...bueno, fecha: 'hoy' })).toBeNull()
    expect(planValido({ ...bueno, generando: 'no' })).toBeNull()
    expect(planValido({ ...bueno, restantes: 4 })).toBeNull()
    expect(planValido({ ...bueno, numero: 1.5 })).toBeNull()
    expect(planValido({ ...bueno, actual: { ...bueno.actual, texto: '  ' } })).toBeNull()
    expect(planValido({ ...bueno, actual: { ...bueno.actual, texto: 'x'.repeat(4001) } })).toBeNull()
    expect(planValido({ ...bueno, actual: 'plan' })).toBeNull()
  })
  it('una rutina inválida pierde solo la tarjeta', () => {
    const p = planValido({ ...bueno, actual: { ...bueno.actual, rutina: { ...rut, ejercicios: [] } } })
    expect(p?.actual?.texto).toBe('Hola')
    expect(p?.actual?.rutina).toBeNull()
  })
})

describe('debePedirPlanSolo', () => {
  const base = { auto: true, conSesion: true, leido: true, hayPlan: false, generando: false, pidiendo: false, yaPedido: false }
  it('pasada la hora, sin plan y con el GET leído: pide', () => {
    expect(debePedirPlanSolo(base)).toBe(true)
  })
  it('antes de la hora (auto apagado) no pide: lo pide el chip', () => {
    expect(debePedirPlanSolo({ ...base, auto: false })).toBe(false)
  })
  it('con plan, generando, pidiendo o ya pedido hoy no repite', () => {
    expect(debePedirPlanSolo({ ...base, hayPlan: true })).toBe(false)
    expect(debePedirPlanSolo({ ...base, generando: true })).toBe(false)
    expect(debePedirPlanSolo({ ...base, pidiendo: true })).toBe(false)
    expect(debePedirPlanSolo({ ...base, yaPedido: true })).toBe(false)
  })
  it('si el GET falló (no leído) o no hay sesión, nunca pide solo', () => {
    expect(debePedirPlanSolo({ ...base, leido: false })).toBe(false)
    expect(debePedirPlanSolo({ ...base, conSesion: false })).toBe(false)
  })
})

describe('debeRepedirPorIdioma', () => {
  const base = { conSesion: true, leido: true, hayPlan: true, generando: false, pidiendo: false, idiomaPlan: 'es' as const, idioma: 'en' as const }
  it('plan pedido en otro idioma: pide; sin dato cuenta como es', () => {
    expect(debeRepedirPorIdioma(base)).toBe(true)
    expect(debeRepedirPorIdioma({ ...base, idiomaPlan: undefined })).toBe(true)
    expect(debeRepedirPorIdioma({ ...base, idiomaPlan: undefined, idioma: 'es' })).toBe(false)
  })
  it('mismo idioma, sin plan, sin leer, sin sesión, generando o pidiendo: no pide', () => {
    expect(debeRepedirPorIdioma({ ...base, idiomaPlan: 'en' })).toBe(false)
    expect(debeRepedirPorIdioma({ ...base, hayPlan: false })).toBe(false)
    expect(debeRepedirPorIdioma({ ...base, leido: false })).toBe(false)
    expect(debeRepedirPorIdioma({ ...base, conSesion: false })).toBe(false)
    expect(debeRepedirPorIdioma({ ...base, generando: true })).toBe(false)
    expect(debeRepedirPorIdioma({ ...base, pidiendo: true })).toBe(false)
  })
  it('un pedido por cambio: el plan viejo de vuelta o un fallo no lo repiten; volver al idioma pide otra vez', () => {
    // Igual que Coach: al pedir fija idiomaPlan = idioma antes de enviar.
    let idiomaPlan: 'es' | 'en' | undefined = 'es'
    let pedidos = 0
    const ciclo = (idioma: 'es' | 'en') => {
      for (let i = 0; i < 5; i++) {
        if (!debeRepedirPorIdioma({ ...base, idiomaPlan, idioma })) continue
        idiomaPlan = idioma
        pedidos++
      }
    }
    ciclo('en')
    expect(pedidos).toBe(1)
    ciclo('en')
    expect(pedidos).toBe(1)
    ciclo('es')
    expect(pedidos).toBe(2)
  })
})

describe('falloPlan', () => {
  it('el tope, el coach sin configurar y la sesión no se reintentan', () => {
    expect(falloPlan(new Rechazada('tope_variaciones'))).toEqual({ texto: 'Ya usaste las 3 opciones de hoy.', reintentable: false })
    expect(falloPlan(new Error('coach_no_configurado')).reintentable).toBe(false)
    expect(falloPlan(new NoAutenticado()).reintentable).toBe(false)
  })
  it('el 503 y otros fallos transitorios sí', () => {
    expect(falloPlan(new Error('HTTP 503'))).toEqual({ texto: 'No se pudo preparar tu entreno de hoy.', reintentable: true })
    expect(falloPlan(new DemasiadasPeticiones(12_000)).texto).toBe('Espera 12 s.')
    expect(falloPlan(new DemasiadasPeticiones(300_000)).reintentable).toBe(false)
  })
})

describe('programaValido', () => {
  const prog = (n: number) => ({ nombre: 'Full body', rutinas: Array.from({ length: n }, (_, i) => ({ ...rutina, id: `r${i}` })) })
  it('acepta de 2 a 4 rutinas y rechaza lo demás', () => {
    expect(programaValido(prog(2))).toEqual(prog(2))
    expect(programaValido(prog(1))).toBeNull()
    expect(programaValido(prog(5))).toBeNull()
    expect(programaValido({ ...prog(2), nombre: '  ' })).toBeNull()
    expect(programaValido({ ...prog(2), rutinas: [rutina, rutina] })).toBeNull()
    expect(programaValido({ ...prog(2), rutinas: [rutina, { ...rutina, id: 'r2', ejercicios: [] }] })).toBeNull()
    expect(programaValido(null)).toBeNull()
  })
  it('leerConversacion conserva el programa y uno roto no tira el mensaje', () => {
    const a = almacen()
    const bueno = msg('coach', 'va', { programa: prog(3) })
    guardarConversacion(a, 'u1', { fecha: HOY, mensajes: [bueno] })
    expect(leerConversacion(a, 'u1', HOY).mensajes[0].programa).toEqual(prog(3))
    const roto = JSON.stringify({ fecha: HOY, mensajes: [{ ...msg('coach', 'va'), programa: prog(1) }] })
    const m = leerConversacion(almacen(roto), 'u1', HOY).mensajes
    expect(m).toHaveLength(1)
    expect(m[0].programa).toBeUndefined()
  })
  it('etiqueta de la herramienta en es y en', () => {
    expect(etiquetaHerramienta('proponer_programa')).toBe('programa')
    fijarIdioma('en')
    expect(etiquetaHerramienta('proponer_programa')).toBe('program')
    fijarIdioma('es')
  })
})

describe('rutinaValida', () => {
  const e = rutina.ejercicios[0]
  it('acepta la buena y rechaza lo que el backend no aceptaría', () => {
    expect(rutinaValida(rutina)).toEqual(rutina)
    expect(rutinaValida({ ...rutina, nombre: 'x'.repeat(41) })).toBeNull()
    expect(rutinaValida({ ...rutina, nombre: '  ' })).toBeNull()
    expect(rutinaValida({ ...rutina, ejercicios: [e, { ...e }] })).toBeNull()
    expect(rutinaValida({ ...rutina, ejercicios: [{ ...e, series: 11 }] })).toBeNull()
    expect(rutinaValida({ ...rutina, ejercicios: [{ ...e, reps: 2.5 }] })).toBeNull()
    expect(rutinaValida({ ...rutina, ejercicios: [{ ...e, pesoKg: -1 }] })).toBeNull()
    expect(rutinaValida({ ...rutina, ejercicios: [{ ...e, descansoS: 601 }] })).toBeNull()
  })
})

describe('herramientas', () => {
  it('solo strings cortos y la etiqueta no sale del prototipo', () => {
    expect(herramientasValidas(['progreso', 7, '', 'x'.repeat(51), null])).toEqual(['progreso'])
    expect(herramientasValidas('progreso')).toEqual([])
    expect(etiquetaHerramienta('volumen_por_grupo')).toBe('volumen por grupo')
    expect(etiquetaHerramienta('constructor')).toBe('constructor')
    expect(etiquetaHerramienta('__proto__')).toBe('__proto__')
    expect(etiquetaHerramienta('y'.repeat(80))).toHaveLength(30)
  })
})

it('limpiarTexto deja vacío lo que solo trae caracteres prohibidos', () => {
  expect(limpiarTexto('​‮﻿').trim()).toBe('')
})

it('textoEspera cambia con los segundos', () => {
  expect(textoEspera(0)).toBe('Pensando…')
  expect(textoEspera(3)).toBe('Revisando tus entrenos…')
  expect(textoEspera(6)).toBe('Revisando tus entrenos…')
  expect(textoEspera(7)).toBe('Armando la respuesta…')
  expect(textoEspera(20)).toBe('Armando la respuesta…')
})

describe('getVigente', () => {
  const ini = { get: 2, post: 1, clave: 'u1|d' }
  it('vale si nada cambió', () => expect(getVigente(ini, ini)).toBe(true))
  it('un POST posterior lo descarta (el POST es más nuevo)', () => expect(getVigente(ini, { ...ini, post: 2 })).toBe(false))
  it('otro GET posterior o cambio de cuenta/día lo descartan', () => {
    expect(getVigente(ini, { ...ini, get: 3 })).toBe(false)
    expect(getVigente(ini, { ...ini, clave: 'u2|d' })).toBe(false)
  })
})
