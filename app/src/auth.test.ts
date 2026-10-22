import { afterEach, describe, expect, it, vi } from 'vitest'

// CLIENT_ID se lee al importar auth: hay que fijarlo antes.
vi.hoisted(() => {
  vi.stubEnv('VITE_GOOGLE_CLIENT_ID', 'cliente-test')
})

// auth guarda estado de módulo (sesión, generación, GIS cargado): cada prueba parte de un módulo nuevo.
const cargar = async () => {
  vi.resetModules()
  return import('./auth')
}

// Como Google: JSON en UTF-8 y luego base64url sin relleno.
const b64url = (o: object) =>
  btoa(String.fromCharCode(...new TextEncoder().encode(JSON.stringify(o))))
    .replace(/\+/g, '-')
    .replace(/\//g, '_')
    .replace(/=+$/, '')
const token = (payload: object) => `${b64url({ alg: 'RS256' })}.${b64url(payload)}.firma`

const almacen = (inicial: [string, string][] = []) => {
  const m = new Map<string, string>(inicial)
  return {
    getItem: (k: string) => m.get(k) ?? null,
    setItem: (k: string, v: string) => void m.set(k, v),
    removeItem: (k: string) => void m.delete(k),
    valores: () => [...m.values()],
  }
}

const respuesta = (status: number, cuerpo?: unknown) => ({ ok: status >= 200 && status < 300, status, json: async () => cuerpo })
const diego = { sub: '42', nombre: 'Diego', foto: 'https://lh3.googleusercontent.com/a/x' }
const cuenta42 = JSON.stringify(diego)
const salio: [string, string] = ['gc.salioManual', '1']

/** Stubs frescos de almacenamiento y fetch; devuelve lo necesario para mirar qué pasó. */
const preparar = (localInicial: [string, string][] = [], fetchFalso = vi.fn()) => {
  const sesion = almacen([])
  const local = almacen(localInicial)
  vi.stubGlobal('sessionStorage', sesion)
  vi.stubGlobal('localStorage', local)
  vi.stubGlobal('fetch', fetchFalso)
  vi.stubGlobal('window', {})
  return { sesion, local, fetchFalso }
}

afterEach(() => {
  vi.unstubAllGlobals()
  vi.useRealTimers()
})

describe('crearSesion', () => {
  it('POST /api/sesion con el token en el cuerpo, CSRF y cookie; queda activa y recuerda la cuenta', async () => {
    const { sesion, local, fetchFalso } = preparar([], vi.fn().mockResolvedValue(respuesta(200, diego)))
    const timeout = vi.spyOn(AbortSignal, 'timeout')
    const auth = await cargar()
    const t = token({ sub: '42', name: 'Diego' })
    await expect(auth.crearSesion(t)).resolves.toEqual(diego)
    const [url, init] = fetchFalso.mock.calls[0]
    expect(url).toBe('/api/sesion')
    expect(init.method).toBe('POST')
    expect(init.credentials).toBe('same-origin')
    expect(init.headers['X-Requested-With']).toBe('gym-coach')
    expect(JSON.parse(init.body)).toEqual({ idToken: t })
    // Cold start de Functions: el login espera más que una petición normal.
    expect(timeout).toHaveBeenCalledWith(30_000)
    timeout.mockRestore()
    expect(auth.estadoSesion()).toEqual({ estado: 'activa', sub: '42', generacion: 1 })
    expect(JSON.parse(local.getItem('gc.cuenta')!)).toEqual(diego)
    // Ni el token ni su payload quedan en ningún almacenamiento.
    for (const v of [...sesion.valores(), ...local.valores()]) {
      expect(v).not.toContain(t)
      expect(v).not.toContain(t.split('.')[1])
    }
  })

  it('la respuesta se sanea como la cuenta recordada', async () => {
    preparar([], vi.fn().mockResolvedValue(respuesta(200, { sub: '42', nombre: '  ' + 'a'.repeat(80), foto: 'https://evil.com/x' })))
    const auth = await cargar()
    await expect(auth.crearSesion('t')).resolves.toEqual({ sub: '42', nombre: 'a'.repeat(60), foto: null })
  })

  it('no ok, cuerpo inválido o sin red: lanza y no toca el estado', async () => {
    for (const f of [
      vi.fn().mockResolvedValue(respuesta(401, { error: 'no_autenticado' })),
      vi.fn().mockResolvedValue(respuesta(200, { sub: '' })),
      vi.fn().mockRejectedValue(new TypeError('Failed to fetch')),
    ]) {
      const { local } = preparar([], f)
      const auth = await cargar()
      await expect(auth.crearSesion('t')).rejects.toThrow()
      expect(auth.estadoSesion()).toEqual({ estado: 'desconocida', sub: null, generacion: 0 })
      expect(local.getItem('gc.cuenta')).toBeNull()
    }
  })
})

describe('restaurarSesion', () => {
  it('200: sesión activa', async () => {
    const { fetchFalso } = preparar([], vi.fn().mockResolvedValue(respuesta(200, diego)))
    const auth = await cargar()
    await expect(auth.restaurarSesion()).resolves.toEqual(diego)
    expect(fetchFalso.mock.calls[0][0]).toBe('/api/sesion')
    expect(fetchFalso.mock.calls[0][1].credentials).toBe('same-origin')
    expect(fetchFalso.mock.calls[0][1].method).toBeUndefined()
    expect(auth.estadoSesion()).toMatchObject({ estado: 'activa', sub: '42' })
  })

  it('401: null y ausente', async () => {
    preparar([], vi.fn().mockResolvedValue(respuesta(401, { error: 'sin_sesion' })))
    const auth = await cargar()
    await expect(auth.restaurarSesion()).resolves.toBeNull()
    expect(auth.estadoSesion()).toMatchObject({ estado: 'ausente', sub: null })
  })

  it('otro rechazo (400, 403, 404): null y ausente; no se queda optimista para siempre', async () => {
    for (const status of [400, 403, 404]) {
      preparar([], vi.fn().mockResolvedValue(respuesta(status)))
      const auth = await cargar()
      await expect(auth.restaurarSesion()).resolves.toBeNull()
      expect(auth.estadoSesion().estado).toBe('ausente')
    }
  })

  it('ya ausente, un fallo de red da null (la UI no oscila)', async () => {
    const f = vi.fn().mockResolvedValueOnce(respuesta(401)).mockRejectedValueOnce(new TypeError('Failed to fetch'))
    preparar([], f)
    const auth = await cargar()
    await auth.restaurarSesion()
    await expect(auth.restaurarSesion()).resolves.toBeNull()
  })

  it("sin red, 5xx, 429, 408 o cuerpo raro: 'sin_red' y el estado no cambia (optimista)", async () => {
    for (const f of [
      vi.fn().mockRejectedValue(new TypeError('Failed to fetch')),
      vi.fn().mockResolvedValue(respuesta(503)),
      vi.fn().mockResolvedValue(respuesta(429)),
      vi.fn().mockResolvedValue(respuesta(408)),
      vi.fn().mockResolvedValue(respuesta(200, null)),
    ]) {
      preparar([], f)
      const auth = await cargar()
      await expect(auth.restaurarSesion()).resolves.toBe('sin_red')
      expect(auth.estadoSesion().estado).toBe('desconocida')
    }
  })

  it('tras salir a mano: null sin GET, y reintenta el DELETE con CSRF', async () => {
    const { fetchFalso } = preparar([salio], vi.fn().mockResolvedValue(respuesta(204)))
    const auth = await cargar()
    await expect(auth.restaurarSesion()).resolves.toBeNull()
    expect(fetchFalso).toHaveBeenCalledOnce()
    const [url, init] = fetchFalso.mock.calls[0]
    expect(url).toBe('/api/sesion')
    expect(init.method).toBe('DELETE')
    expect(init.headers['X-Requested-With']).toBe('gym-coach')
    expect(auth.estadoSesion().estado).toBe('ausente')
  })

  it('un 401 que llega después de un login no cierra la sesión nueva', async () => {
    let soltar: (r: unknown) => void = () => {}
    const fetchFalso = vi.fn()
      .mockImplementationOnce(() => new Promise((r) => (soltar = r)))
      .mockResolvedValueOnce(respuesta(200, diego))
    preparar([], fetchFalso)
    const auth = await cargar()
    const enVuelo = auth.restaurarSesion()
    await auth.crearSesion('t')
    soltar(respuesta(401))
    await expect(enVuelo).resolves.toEqual(diego)
    expect(auth.estadoSesion()).toMatchObject({ estado: 'activa', sub: '42' })
  })

  it('salir con la verificación en vuelo: el 200 tardío no reactiva ni re-recuerda la cuenta', async () => {
    let soltar: (r: unknown) => void = () => {}
    const fetchFalso = vi.fn().mockImplementationOnce(() => new Promise((r) => (soltar = r))).mockResolvedValue(respuesta(204))
    const { local } = preparar([['gc.cuenta', cuenta42]], fetchFalso)
    const auth = await cargar()
    const enVuelo = auth.restaurarSesion()
    auth.cerrarSesionManual()
    soltar(respuesta(200, diego))
    await expect(enVuelo).resolves.toBeNull()
    expect(auth.estadoSesion()).toMatchObject({ estado: 'ausente', sub: null })
    expect(local.getItem('gc.cuenta')).toBeNull()
  })
})

describe('marcarSinSesion', () => {
  it('misma generación: pasa a ausente una sola vez; otra generación: no toca nada', async () => {
    preparar([], vi.fn().mockResolvedValue(respuesta(200, diego)))
    const auth = await cargar()
    await auth.crearSesion('t')
    expect(auth.marcarSinSesion(0)).toBe(false)
    expect(auth.estadoSesion().estado).toBe('activa')
    expect(auth.marcarSinSesion(1)).toBe(true)
    expect(auth.estadoSesion()).toEqual({ estado: 'ausente', sub: null, generacion: 1 })
    expect(auth.marcarSinSesion(1)).toBe(false)
  })
})

describe('cerrarSesionManual', () => {
  it('ausente, olvida la cuenta, deja la bandera, apaga el auto-select y borra la cookie', async () => {
    const { local, fetchFalso } = preparar([['gc.cuenta', cuenta42]], vi.fn().mockResolvedValue(respuesta(204)))
    const disableAutoSelect = vi.fn()
    vi.stubGlobal('window', { google: { accounts: { id: { disableAutoSelect } } } })
    const auth = await cargar()
    auth.cerrarSesionManual()
    expect(auth.estadoSesion().estado).toBe('ausente')
    expect(local.getItem('gc.cuenta')).toBeNull()
    expect(local.getItem('gc.salioManual')).toBe('1')
    expect(disableAutoSelect).toHaveBeenCalledOnce()
    const [url, init] = fetchFalso.mock.calls[0]
    expect(url).toBe('/api/sesion')
    expect(init).toMatchObject({ method: 'DELETE', keepalive: true, headers: { 'X-Requested-With': 'gym-coach' } })
  })

  it('no lanza sin red, sin fetch ni GIS', async () => {
    preparar([], vi.fn().mockRejectedValue(new TypeError('Failed to fetch')))
    let auth = await cargar()
    expect(() => auth.cerrarSesionManual()).not.toThrow()
    preparar([], undefined)
    vi.stubGlobal('fetch', undefined)
    auth = await cargar()
    expect(() => auth.cerrarSesionManual()).not.toThrow()
    expect(auth.estadoSesion().estado).toBe('ausente')
  })
})

describe('vigilarSesion', () => {
  const doc = () => {
    let oyente = () => {}
    const d = {
      visibilityState: 'visible' as DocumentVisibilityState,
      addEventListener: vi.fn((_: string, f: () => void) => {
        oyente = f
      }),
      removeEventListener: vi.fn(),
      volver: () => oyente(),
    }
    return d
  }

  it('al volver al frente verifica solo si pasaron más de 12 h; no verifica al montar', async () => {
    vi.useFakeTimers({ toFake: ['Date'] })
    vi.setSystemTime(Date.parse('2026-10-10T08:00:00Z'))
    const { fetchFalso } = preparar([], vi.fn().mockResolvedValue(respuesta(200, diego)))
    const auth = await cargar()
    await auth.restaurarSesion()
    const alCambiar = vi.fn()
    const d = doc()
    const soltar = auth.vigilarSesion(alCambiar, d as never)
    expect(fetchFalso).toHaveBeenCalledOnce()

    vi.setSystemTime(Date.parse('2026-10-10T19:59:00Z'))
    d.volver()
    d.visibilityState = 'hidden'
    vi.setSystemTime(Date.parse('2026-10-10T20:01:00Z'))
    d.volver()
    expect(fetchFalso).toHaveBeenCalledOnce()

    d.visibilityState = 'visible'
    d.volver()
    await vi.waitFor(() => expect(alCambiar).toHaveBeenCalledWith(diego))
    expect(fetchFalso).toHaveBeenCalledTimes(2)
    soltar()
    expect(d.removeEventListener).toHaveBeenCalledWith('visibilitychange', expect.any(Function))
  })

  it("sin red también cuenta como verificación: no un fetch por cada foco", async () => {
    vi.useFakeTimers({ toFake: ['Date'] })
    vi.setSystemTime(Date.parse('2026-10-10T08:00:00Z'))
    const { fetchFalso } = preparar([], vi.fn().mockRejectedValue(new TypeError('Failed to fetch')))
    const auth = await cargar()
    const alCambiar = vi.fn()
    const d = doc()
    auth.vigilarSesion(alCambiar, d as never)
    d.volver()
    await vi.waitFor(() => expect(alCambiar).toHaveBeenCalledWith('sin_red'))
    d.volver()
    d.volver()
    expect(fetchFalso).toHaveBeenCalledOnce()
  })

  it('una respuesta que llega tras soltar se descarta', async () => {
    let soltarFetch: (r: unknown) => void = () => {}
    preparar([], vi.fn().mockImplementation(() => new Promise((r) => (soltarFetch = r))))
    const auth = await cargar()
    const alCambiar = vi.fn()
    const d = doc()
    const soltar = auth.vigilarSesion(alCambiar, d as never)
    d.volver()
    soltar()
    soltarFetch(respuesta(401))
    await new Promise((r) => setTimeout(r, 0))
    expect(alCambiar).not.toHaveBeenCalled()
  })
})

describe('limpiarLegado', () => {
  it('borra el token de la versión anterior; sin sessionStorage no lanza', async () => {
    const { sesion } = preparar()
    sesion.setItem('gc.idToken', 'viejo')
    const auth = await cargar()
    auth.limpiarLegado()
    expect(sesion.getItem('gc.idToken')).toBeNull()
    vi.stubGlobal('sessionStorage', undefined)
    expect(() => auth.limpiarLegado()).not.toThrow()
  })
})

describe('cuenta recordada', () => {
  const leer = async (valor: string) => {
    preparar([['gc.cuenta', valor]])
    return (await cargar()).leerCuentaRecordada()
  }

  it('lee una cuenta válida', async () => {
    expect(await leer(cuenta42)).toEqual(diego)
  })

  it('null con JSON roto, sub vacío, de espacios, > 255 o no string', async () => {
    const malas = ['{roto', 'null', '"x"', JSON.stringify({ sub: '' }), JSON.stringify({ sub: '   ' }),
      JSON.stringify({ sub: 'x'.repeat(256) }), JSON.stringify({ sub: 5 })]
    for (const v of malas) expect(await leer(v)).toBeNull()
  })

  it('null sin localStorage', async () => {
    const auth = await cargar()
    vi.stubGlobal('localStorage', undefined)
    expect(auth.leerCuentaRecordada()).toBeNull()
  })

  it('foto: solo https de lh3–lh6, sin usuario ni puerto; inválida -> null sin perder la cuenta', async () => {
    const malas = ['http://lh3.googleusercontent.com/a/x', 'https://evil.com/x', 'https://lh7.googleusercontent.com/a/x',
      'https://lh3.googleusercontent.com:8443/x', 'https://evil.com@lh3.googleusercontent.com/x', 'javascript:alert(1)', 42]
    for (const foto of malas) expect(await leer(JSON.stringify({ ...diego, foto }))).toEqual({ ...diego, foto: null })
    expect((await leer(JSON.stringify({ ...diego, foto: 'https://LH3.GoogleUserContent.com/a/x' })))?.foto)
      .toBe('https://lh3.googleusercontent.com/a/x')
  })

  it('nombre recortado a 60 sin partir un emoji; no string -> null', async () => {
    const familia = '👨‍👩‍👧'
    expect((await leer(JSON.stringify({ sub: '1', nombre: 'a'.repeat(59) + familia + 'zzz' })))?.nombre).toBe('a'.repeat(59) + familia)
    expect((await leer(JSON.stringify({ sub: '1', nombre: 7 })))?.nombre).toBeNull()
  })

  it('recordarCuenta guarda solo sub, nombre y foto', async () => {
    const { local } = preparar()
    const auth = await cargar()
    auth.recordarCuenta({ ...diego, token: 'secreto' } as never)
    expect(JSON.parse(local.getItem('gc.cuenta')!)).toEqual(diego)
  })
})

describe('callback de GIS', () => {
  const id = { initialize: vi.fn(), renderButton: vi.fn(), prompt: vi.fn(), disableAutoSelect: vi.fn() }
  const t = token({ sub: '42', name: 'Diego' })
  const otra = token({ sub: '99', name: 'Otra' })
  const otraSesion = { sub: '99', nombre: 'Otra', foto: null }

  /** Módulo y stubs frescos; `responder` simula la respuesta de GIS al último initialize. */
  const conGis = async (localInicial: [string, string][] = [], fetchFalso = vi.fn().mockResolvedValue(respuesta(200, diego))) => {
    for (const f of Object.values(id)) f.mockReset()
    const p = preparar(localInicial, fetchFalso)
    vi.stubGlobal('window', { google: { accounts: { id } } })
    const auth = await cargar()
    const callback = () => id.initialize.mock.lastCall![0].callback as (r: { credential: string; select_by?: string }) => void
    const responder = (credential: string, select_by?: string) => callback()({ credential, select_by })
    return { ...p, auth, callback, responder }
  }

  it('usa el CLIENT_ID de la prueba, no el de .env.local', async () => {
    expect((await cargar()).CLIENT_ID).toBe('cliente-test')
  })

  it('explícito (botón): crea la sesión, quita la bandera y entra con la sesión', async () => {
    const { auth, local, sesion, fetchFalso, responder } = await conGis([salio])
    const alEntrar = vi.fn()
    await auth.mostrarBotonGoogle({} as HTMLElement, alEntrar)
    responder(t, 'btn')
    await vi.waitFor(() => expect(alEntrar).toHaveBeenCalledWith(diego))
    expect(JSON.parse(fetchFalso.mock.calls[0][1].body)).toEqual({ idToken: t })
    expect(local.getItem('gc.salioManual')).toBeNull()
    expect(sesion.getItem('gc.idToken')).toBeNull()
  })

  it('automático con la misma cuenta recordada: entra', async () => {
    const { auth, responder } = await conGis([['gc.cuenta', cuenta42]])
    const alEntrar = vi.fn()
    await auth.mostrarBotonGoogle({} as HTMLElement, alEntrar)
    responder(t, 'auto')
    await vi.waitFor(() => expect(alEntrar).toHaveBeenCalledOnce())
  })

  it('automático (auto, itp, fedcm_auto, sin select_by) con otra cuenta o sin cuenta: ni POST ni entra', async () => {
    const { auth, local, fetchFalso, responder } = await conGis([['gc.cuenta', cuenta42]])
    const alEntrar = vi.fn()
    await auth.mostrarBotonGoogle({} as HTMLElement, alEntrar)
    for (const sb of ['auto', 'itp', 'fedcm_auto', 'raro', undefined]) responder(otra, sb)
    local.removeItem('gc.cuenta')
    responder(t, 'auto')
    await new Promise((r) => setTimeout(r, 0))
    expect(fetchFalso).not.toHaveBeenCalled()
    expect(alEntrar).not.toHaveBeenCalled()
  })

  it('salir con un login automático en vuelo: no entra, no escribe la cuenta y borra la cookie que llegó', async () => {
    let soltar: (r: unknown) => void = () => {}
    const fetchFalso = vi.fn().mockImplementationOnce(() => new Promise((r) => (soltar = r))).mockResolvedValue(respuesta(204))
    const { auth, local, responder } = await conGis([['gc.cuenta', cuenta42]], fetchFalso)
    const alEntrar = vi.fn()
    const alFallar = vi.fn()
    await auth.mostrarBotonGoogle({} as HTMLElement, alEntrar, { alFallar })
    responder(t, 'auto')
    await vi.waitFor(() => expect(fetchFalso).toHaveBeenCalledOnce())
    auth.cerrarSesionManual()
    soltar(respuesta(200, diego))
    await vi.waitFor(() => expect(fetchFalso).toHaveBeenCalledTimes(3))
    expect(fetchFalso.mock.calls.slice(1).every(([, init]) => init.method === 'DELETE')).toBe(true)
    expect(alEntrar).not.toHaveBeenCalled()
    expect(alFallar).not.toHaveBeenCalled()
    expect(local.getItem('gc.cuenta')).toBeNull()
    expect(auth.estadoSesion().estado).toBe('ausente')
  })

  it('el usuario que elige otra cuenta sí cambia', async () => {
    const { auth, local, responder } = await conGis([['gc.cuenta', cuenta42]], vi.fn().mockResolvedValue(respuesta(200, otraSesion)))
    const alEntrar = vi.fn()
    await auth.mostrarBotonGoogle({} as HTMLElement, alEntrar)
    responder(otra, 'user_1tap')
    await vi.waitFor(() => expect(alEntrar).toHaveBeenCalledWith(otraSesion))
    expect(JSON.parse(local.getItem('gc.cuenta')!).sub).toBe('99')
  })

  it('tras salir a mano lo automático no re-entra; la bandera apaga auto_select y One Tap', async () => {
    const { auth, fetchFalso, responder } = await conGis([salio, ['gc.cuenta', cuenta42]])
    await auth.mostrarBotonGoogle({} as HTMLElement, () => {})
    expect(id.initialize.mock.lastCall![0].auto_select).toBe(false)
    expect(id.prompt).not.toHaveBeenCalled()
    responder(t, 'auto')
    await new Promise((r) => setTimeout(r, 0))
    expect(fetchFalso).not.toHaveBeenCalled()
  })

  it('oneTap: false pinta solo el botón', async () => {
    const { auth } = await conGis()
    await auth.mostrarBotonGoogle({} as HTMLElement, () => {})
    expect(id.prompt).toHaveBeenCalledOnce()
    await auth.mostrarBotonGoogle({} as HTMLElement, () => {}, { oneTap: false })
    expect(id.prompt).toHaveBeenCalledOnce()
    expect(id.renderButton).toHaveBeenCalledTimes(2)
  })

  it('si la API no da sesión: no entra, avisa por alFallar y la bandera sigue', async () => {
    const { auth, local, responder } = await conGis([salio], vi.fn().mockRejectedValue(new TypeError('Failed to fetch')))
    const alEntrar = vi.fn()
    const alFallar = vi.fn()
    await auth.mostrarBotonGoogle({} as HTMLElement, alEntrar, { alFallar })
    responder(t, 'btn')
    await vi.waitFor(() => expect(alFallar).toHaveBeenCalledOnce())
    expect(alEntrar).not.toHaveBeenCalled()
    expect(local.getItem('gc.salioManual')).toBe('1')
  })
})
