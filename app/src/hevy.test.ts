import { afterEach, describe, expect, it, vi } from 'vitest'
import type { Serie } from './api'
import { fijarIdioma } from './i18n'
import {
  analizarHevy, diasEnConflicto, enLotes, fechaHevy, FormatoHevyInvalido, HEVY_A_CATALOGO, idHevy, parsearCsv, sinDias,
} from './hevy'

const CAB = '"title","start_time","end_time","description","exercise_title","superset_id","exercise_notes","set_index","set_type","weight_lbs","reps","distance_km","duration_seconds","rpe"'
const CAB_KG = CAB.replace('weight_lbs', 'weight_kg')
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-5[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/

interface Fila { title?: string; inicio?: string; ej?: string; notas?: string; set?: number; tipo?: string; peso?: string; reps?: string }
const fila = (f: Fila = {}) =>
  [
    `"${f.title ?? 'Pierna'}"`, `"${f.inicio ?? '7 Oct 2026, 20:33'}"`, '"7 Oct 2026, 21:09"', '""',
    `"${f.ej ?? 'Leg Press (Machine)'}"`, '', `"${f.notas ?? ''}"`, String(f.set ?? 0), `"${f.tipo ?? 'normal'}"`,
    f.peso ?? '225', f.reps ?? '10', '', '', '',
  ].join(',')
const csv = (filas: Fila[], cab = CAB) => [cab, ...filas.map(fila)].join('\n')

describe('parsearCsv', () => {
  it('comillas, "" escapadas, comas y saltos de línea en campos, BOM y \\r\\n', () => {
    expect(parsearCsv('﻿"a","b,c"\r\n"di ""x""","e\nf"\r\n')).toEqual([['a', 'b,c'], ['di "x"', 'e\nf']])
    expect(parsearCsv('a,,b')).toEqual([['a', '', 'b']])
  })
})

describe('fechaHevy', () => {
  it('24 h y AM/PM', () => {
    expect(fechaHevy('7 Oct 2026, 20:33')).toEqual({ y: 2026, m: 10, d: 7, hh: 20, mm: 33 })
    expect(fechaHevy('7 Oct 2026, 8:33 PM')?.hh).toBe(20)
    expect(fechaHevy('7 Oct 2026, 12:05 AM')?.hh).toBe(0)
    expect(fechaHevy('2026-10-07')).toBeNull()
    expect(fechaHevy('31 Feb 2026, 10:00')).toBeNull()
  })
})

describe('idHevy', () => {
  it('crypto.subtle disponible y UUID con versión 5 y variante RFC', async () => {
    expect(typeof crypto?.subtle?.digest).toBe('function')
    const id = await idHevy('sub', ['hevy', 'x'])
    expect(id).toMatch(UUID)
    expect(await idHevy('sub', ['hevy', 'x'])).toBe(id)
    expect(await idHevy('otro', ['hevy', 'x'])).not.toBe(id)
  })
})

describe('analizarHevy', () => {
  it('lb a kg, fecha, ts local y ids', async () => {
    const a = await analizarHevy(csv([{}, { set: 1 }]), 'u1')
    expect(a.unidad).toBe('lb')
    expect(a.series).toHaveLength(2)
    const [s] = a.series
    expect(s.pesoKg).toBe(102.06)
    expect(s.ejercicioId).toBe('prensa')
    expect(s.fecha).toBe('2026-10-07')
    expect(new Date(s.ts).getHours()).toBe(20)
    expect(s.rutina).toBe('Pierna')
    expect(s.tipoSerie).toBeUndefined()
    expect(a.series.every((x) => UUID.test(x.id))).toBe(true)
    expect(a).toMatchObject({ entrenos: 1, ejercicios: 1, desde: '2026-10-07', hasta: '2026-10-07', ejerciciosImportados: ['prensa'] })
  })

  it('AM/PM en el CSV', async () => {
    const a = await analizarHevy(csv([{ inicio: '7 Oct 2026, 8:33 PM' }]), 'u1')
    expect(new Date(a.series[0].ts).getHours()).toBe(20)
  })

  it('weight_kg con decimales', async () => {
    const a = await analizarHevy(csv([{ peso: '60.5' }], CAB_KG), 'u1')
    expect(a.unidad).toBe('kg')
    expect(a.series[0].pesoKg).toBe(60.5)
  })

  it('sin cabecera, sin columnas clave o sin columna de peso: lanza', async () => {
    await expect(analizarHevy(csv([{}], CAB.replace('"weight_lbs",', '"peso",')), 'u1')).rejects.toBeInstanceOf(FormatoHevyInvalido)
    await expect(analizarHevy('', 'u1')).rejects.toBeInstanceOf(FormatoHevyInvalido)
    await expect(analizarHevy(csv([{}], CAB.replace('"reps"', '"r"')), 'u1')).rejects.toBeInstanceOf(FormatoHevyInvalido)
  })

  it('notas con comas, "" y salto de línea no rompen la fila; se cuentan una vez por ejercicio', async () => {
    const a = await analizarHevy(csv([{ notas: 'lento, ""pausa""\nsin dolor' }, { notas: 'lento, ""pausa""\nsin dolor', set: 1 }]), 'u1')
    expect(a.series).toHaveLength(2)
    expect(a.series[0].reps).toBe(10)
    expect(a.notasDescartadas).toBe(1)
  })

  it('tipos de serie', async () => {
    const a = await analizarHevy(csv([
      { tipo: 'warmup', set: 0 }, { tipo: 'dropset', set: 1 }, { tipo: 'failure', set: 2 }, { tipo: 'raro', set: 3 },
    ]), 'u1')
    expect(a.series.map((s) => s.tipoSerie)).toEqual(['calentamiento', 'drop', 'fallo', undefined])
  })

  it('omitidas por reps, peso y fecha; peso vacío es 0', async () => {
    const a = await analizarHevy(csv([
      { reps: '', set: 0 }, { reps: '0', set: 1 }, { reps: '150', set: 2 },
      { peso: '1200', set: 3 }, { peso: '', set: 4 },
      { inicio: '2026-10-07' },
    ]), 'u1')
    expect(a.omitidas).toEqual({ reps: 3, peso: 1, fecha: 1 })
    expect(a.series).toHaveLength(1)
    expect(a.series[0].pesoKg).toBe(0)
  })

  it('ejercicio repetido en un entreno con set_index 0: ids distintos', async () => {
    const a = await analizarHevy(csv([
      { set: 0 }, { ej: 'Leg Extension (Machine)', set: 0 }, { set: 0 },
    ]), 'u1')
    expect(new Set(a.series.map((s) => s.id)).size).toBe(3)
  })

  it('deterministas por texto y por cuenta', async () => {
    const t = csv([{}, { set: 1 }])
    const ids = (a: { series: Serie[] }) => a.series.map((s) => s.id)
    const u1 = ids(await analizarHevy(t, 'u1'))
    expect(ids(await analizarHevy(t, 'u1'))).toEqual(u1)
    expect(ids(await analizarHevy(t, 'u2')).some((id) => u1.includes(id))).toBe(false)
  })

  it('ts estrictamente creciente dentro del entreno', async () => {
    const a = await analizarHevy(csv([{ set: 0 }, { set: 1 }, { ej: 'Leg Extension (Machine)', set: 0 }]), 'u1')
    const ts = a.series.map((s) => Date.parse(s.ts))
    expect(ts[0] < ts[1] && ts[1] < ts[2]).toBe(true)
  })

  describe('fechas que el backend rechazaría', () => {
    afterEach(() => vi.useRealTimers())

    it('antes de 2000 o después de mañana (UTC): omitidas por fecha', async () => {
      vi.useFakeTimers({ toFake: ['Date'], now: new Date('2026-10-09T12:00:00Z') })
      const a = await analizarHevy(csv([
        { inicio: '31 Dec 1999, 10:00' }, { inicio: '11 Oct 2026, 10:00' }, { inicio: '8 Oct 2026, 10:00' },
      ]), 'u1')
      expect(a.omitidas.fecha).toBe(2)
      expect(a.series.map((s) => s.fecha)).toEqual(['2026-10-08'])
    })

    it('ts más de 1 h en el futuro aunque la fecha sea válida: omitida por fecha', async () => {
      vi.useFakeTimers({ toFake: ['Date'], now: new Date('2026-10-09T12:00:00Z') })
      // Mañana a las 23:00 locales: fecha <= mañana UTC, pero el ts está en el futuro en cualquier zona de -12 h a +12 h.
      const a = await analizarHevy(csv([{ inicio: '10 Oct 2026, 23:00' }]), 'u1')
      expect(a.omitidas.fecha).toBe(1)
      expect(a.series).toHaveLength(0)
    })
  })

  it('sin emparejar: conteo y sin series', async () => {
    const a = await analizarHevy(csv([{ ej: 'Zercher Squat' }, { ej: 'Zercher Squat', set: 1 }, {}]), 'u1')
    expect(a.sinEmparejar).toEqual([{ nombre: 'Zercher Squat', series: 2 }])
    expect(a.series).toHaveLength(1)
  })

  it('título: recorta a 40 y descarta bidi', async () => {
    const largo = await analizarHevy(csv([{ title: 'x'.repeat(60) }]), 'u1')
    expect(largo.series[0].rutina).toBe('x'.repeat(40))
    const bidi = await analizarHevy(csv([{ title: 'Pierna‮axe' }]), 'u1')
    expect(bidi.series[0].rutina).toBeUndefined()
    expect(bidi.series[0]).not.toHaveProperty('rutina')
  })
})

describe('conflictos y lotes', () => {
  it('diasEnConflicto y sinDias', async () => {
    const a = await analizarHevy(csv([{}, { inicio: '8 Oct 2026, 19:00' }]), 'u1')
    const propia = a.series[0]
    const ajena: Serie = { id: 'otra', ejercicioId: 'prensa', pesoKg: 50, reps: 8, fecha: '2026-10-08', ts: '2026-10-08T12:00:00.000Z' }
    const fueraDeRango: Serie = { ...ajena, id: 'x', fecha: '2026-10-01' }
    // La serie ya importada (mismo id) no es conflicto; la ajena del mismo día sí.
    expect(diasEnConflicto(a, [propia, ajena, fueraDeRango])).toEqual(['2026-10-08'])
    expect(sinDias(a, ['2026-10-08']).map((s) => s.fecha)).toEqual(['2026-10-07'])
  })

  it('enLotes de 806 -> 81 lotes, el último de 6', () => {
    const l = enLotes(Array.from({ length: 806 }, (_, i) => i))
    expect(l).toHaveLength(81)
    expect(l[80]).toHaveLength(6)
  })
})

it('el mapeo cubre los 23 ejercicios', () => {
  expect(Object.keys(HEVY_A_CATALOGO)).toHaveLength(23)
})

describe('mensajes en inglés', () => {
  afterEach(() => fijarIdioma('es'))

  it('el error de formato sale en inglés', async () => {
    fijarIdioma('en')
    await expect(analizarHevy('', 'u1')).rejects.toThrow('The Hevy header or columns are missing.')
  })
})
