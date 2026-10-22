import { afterEach, describe, expect, it, vi } from 'vitest'
import { DemasiadasPeticiones, Rechazada, type ResultadoImportacion, type Serie } from './api'
import type { AnalisisHevy } from './hevy'
import { fijarIdioma } from './i18n'
import { importarPorLotes, rangoTexto, seriesAImportar } from './importarLotes'

vi.mock('./hevy', () => ({
  FormatoHevyInvalido: class extends Error {},
  analizarHevy: vi.fn(),
  diasEnConflicto: vi.fn(),
  enLotes: vi.fn(),
  sinDias: (a: { series: Serie[] }, dias: string[]) => a.series.filter((s) => !dias.includes(s.fecha)),
}))

const serie = (i: number, fecha = '2026-10-01'): Serie => ({ id: `s${i}`, ejercicioId: 'banca', pesoKg: 50, reps: 8, fecha, ts: `${fecha}T10:00:00Z` })
const lotes = (...tams: number[]) => tams.map((n, k) => Array.from({ length: n }, (_, i) => serie(k * 100 + i)))
const ok: ResultadoImportacion = { guardadas: 1, existentes: 0, recordsActualizados: 1, recordsPendientes: 0 }

describe('importarPorLotes', () => {
  it('envía los lotes en orden y reporta el progreso', async () => {
    const vistos: string[] = []
    const progreso: number[] = []
    const fin = await importarPorLotes(lotes(25, 25, 10), async (l) => (vistos.push(l[0].id), ok), async () => {}, (h) => progreso.push(h))
    expect(vistos).toEqual(['s0', 's100', 's200'])
    expect(progreso).toEqual([25, 50, 60])
    expect(fin).toMatchObject({ estado: 'listo', recordsActualizados: 3 })
  })

  it('espera max(0, 2500 - transcurrido) entre lotes, no después del último', async () => {
    const esperas: number[] = []
    let t = 0
    await importarPorLotes(lotes(1, 1, 1), async () => ((t += 500), ok), async (ms) => void esperas.push(ms), () => {}, { ahora: () => t })
    expect(esperas).toEqual([2000, 2000])
  })

  it('429 en el lote 2: espera el Retry-After y reintenta el mismo lote', async () => {
    const llamadas: string[] = []
    const esperas: number[] = []
    let fallos = 0
    const enviar = async (l: Serie[]) => {
      llamadas.push(l[0].id)
      if (l[0].id === 's100' && fallos++ === 0) throw new DemasiadasPeticiones(7000)
      return ok
    }
    const fin = await importarPorLotes(lotes(2, 2, 2), enviar, async (ms) => void esperas.push(ms), () => {})
    expect(llamadas).toEqual(['s0', 's100', 's100', 's200'])
    expect(esperas).toContain(7000)
    expect(fin.estado).toBe('listo')
  })

  it('5 fallos seguidos pausan; continuar sigue desde ese lote', async () => {
    let caido = true
    const llamadas: string[] = []
    const enviar = async (l: Serie[]) => {
      llamadas.push(l[0].id)
      if (l[0].id === 's100' && caido) throw new DemasiadasPeticiones(1)
      return ok
    }
    const ls = lotes(2, 2, 2)
    const pausa = await importarPorLotes(ls, enviar, async () => {}, () => {})
    expect(pausa).toMatchObject({ estado: 'pausado', siguiente: 1 })
    expect(llamadas.filter((x) => x === 's100')).toHaveLength(5)
    caido = false
    llamadas.length = 0
    const fin = await importarPorLotes(ls, enviar, async () => {}, () => {}, { desde: 1, hechasInicial: 2 })
    expect(llamadas).toEqual(['s100', 's200'])
    expect(fin.estado).toBe('listo')
  })

  it('si todo se rechaza, cada serie cuenta como rechazada y el lote se da por procesado', async () => {
    const enviar = vi.fn().mockRejectedValue(new Rechazada('series: inválida'))
    const fin = await importarPorLotes(lotes(2), enviar, async () => {}, () => {})
    expect(enviar).toHaveBeenCalledTimes(4) // lote, lote sin rutina, 2 sueltas
    expect(fin).toMatchObject({ estado: 'listo', rechazadas: 2 })
  })

  it('cancelar corta antes del siguiente lote', async () => {
    let n = 0
    const fin = await importarPorLotes(lotes(1, 1, 1), async () => (n++, ok), async () => {}, () => {}, { cancelado: () => n >= 1 })
    expect(n).toBe(1)
    expect(fin).toMatchObject({ estado: 'cancelado', siguiente: 1 })
  })
})

describe('espera del servidor', () => {
  it('avisa alEspera(true/false) alrededor de la espera de un 429', async () => {
    const eventos: boolean[] = []
    let f = 0
    await importarPorLotes(lotes(1), async () => { if (f++ === 0) throw new DemasiadasPeticiones(10); return ok }, async () => {}, () => {}, { alEspera: (s) => eventos.push(s) })
    expect(eventos).toEqual([true, false])
  })
})

describe('400 y límites', () => {
  const conRutina = (n: number): Serie[] => Array.from({ length: n }, (_, i) => ({ ...serie(i), rutina: 'Push 💪' }))

  it('un 400 reintenta el lote sin rutina', async () => {
    const llamadas: Serie[][] = []
    const enviar = async (l: Serie[]) => {
      llamadas.push(l)
      if (l.some((s) => s.rutina)) throw new Rechazada('rutina: inválida')
      return ok
    }
    const fin = await importarPorLotes([conRutina(3)], enviar, async () => {}, () => {})
    expect(llamadas).toHaveLength(2)
    expect(llamadas[1].every((s) => s.rutina === undefined)).toBe(true)
    expect(fin).toMatchObject({ estado: 'listo', rechazadas: 0 })
  })

  it('si sigue el 400, manda de a una, omite las rechazadas y continúa con el siguiente lote', async () => {
    const malo = 's1'
    const enviar = async (l: Serie[]) => {
      if (l.some((s) => s.id === malo)) throw new Rechazada('mal')
      return { ...ok, guardadas: l.length }
    }
    const ls = [[serie(0), serie(1), serie(2)], [serie(10)]]
    const progreso: number[] = []
    const fin = await importarPorLotes(ls, enviar, async () => {}, (h) => progreso.push(h))
    expect(fin).toMatchObject({ estado: 'listo', rechazadas: 1, guardadas: 3 })
    expect(progreso).toEqual([3, 4])
  })

  it('un Retry-After de más de 2 minutos pausa sin esperar ni reintentar', async () => {
    const enviar = vi.fn().mockRejectedValue(new DemasiadasPeticiones(300_000))
    const esperar = vi.fn(async () => {})
    const fin = await importarPorLotes(lotes(2), enviar, esperar, () => {})
    expect(enviar).toHaveBeenCalledTimes(1)
    expect(esperar).not.toHaveBeenCalled()
    expect(fin).toMatchObject({ estado: 'pausado', siguiente: 0, motivo: expect.stringContaining('límite de importación de hoy') })
  })

  it('suma guardadas y existentes', async () => {
    const fin = await importarPorLotes(lotes(1, 1), async () => ({ ...ok, guardadas: 2, existentes: 3 }), async () => {}, () => {})
    expect(fin).toMatchObject({ guardadas: 4, existentes: 6 })
  })
})

describe('seriesAImportar', () => {
  const a = { series: [serie(1, '2026-10-01'), serie(2, '2026-10-02')] } as AnalisisHevy
  it('por defecto excluye los días en conflicto; "importar igual" los incluye', () => {
    expect(seriesAImportar(a, ['2026-10-02'], false).map((s) => s.id)).toEqual(['s1'])
    expect(seriesAImportar(a, ['2026-10-02'], true)).toHaveLength(2)
  })
})

describe('rangoTexto', () => {
  it('formatea meses y años', () => {
    expect(rangoTexto('2026-06-03', '2026-10-01')).toBe('jun–oct 2026')
    expect(rangoTexto('2026-10-03', '2026-10-20')).toBe('oct 2026')
    expect(rangoTexto('2025-12-03', '2026-02-01')).toBe('dic 2025–feb 2026')
  })
})

describe('en inglés', () => {
  afterEach(() => fijarIdioma('es'))

  it('el rango de meses sale en inglés', () => {
    fijarIdioma('en')
    expect(rangoTexto('2026-06-03', '2026-10-01')).toBe('Jun–Oct 2026')
    expect(rangoTexto('2025-12-03', '2026-02-01')).toBe('Dec 2025–Feb 2026')
  })

  it('el motivo de la pausa sale en inglés', async () => {
    fijarIdioma('en')
    const fin = await importarPorLotes(lotes(1), async () => { throw new DemasiadasPeticiones(1) }, async () => {}, () => {})
    expect(fin).toMatchObject({ estado: 'pausado', motivo: 'The server is not responding.' })
    const diario = await importarPorLotes(lotes(1), async () => { throw new DemasiadasPeticiones(10_000_000) }, async () => {}, () => {})
    expect(diario).toMatchObject({ estado: 'pausado', motivo: expect.stringContaining('import limit') })
  })
})
