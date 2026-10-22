import { afterEach, describe, expect, it } from 'vitest'
import { fijarIdioma } from './i18n'
import type { RecordEjercicio, Serie } from './api'
import { cuentaComoTrabajo, e1rm, fusionarRecords, recordsBatidos, siguienteTipo, textoRecord, tipoHeredado } from './records'

let n = 0
const s = (pesoKg: number, reps: number, extra: Partial<Serie> & { estado?: string } = {}): Serie & { estado?: string } => ({
  id: String(++n), ejercicioId: 'banca', pesoKg, reps, fecha: '2026-10-06', ts: '2026-10-06T10:00:00Z', ...extra,
})

const rec = (pesoKg: number, reps: number, ejercicioId = 'banca'): RecordEjercicio => ({
  ejercicioId,
  pesoMax: { pesoKg, reps, fecha: '2026-10-01' },
  e1rm: e1rm(pesoKg, reps) === null ? null : { kg: e1rm(pesoKg, reps)!, pesoKg, reps, fecha: '2026-10-01' },
  repsMax: { reps, pesoKg, fecha: '2026-10-01' },
})

describe('e1rm (misma tabla que el servidor)', () => {
  it('Epley con tope de 12 reps y sin peso', () => {
    expect(e1rm(100, 1)).toBe(100)
    expect(e1rm(100, 10)).toBe(133.33)
    expect(e1rm(100, 12)).toBe(140)
    expect(e1rm(100, 13)).toBeNull()
    expect(e1rm(0, 5)).toBeNull()
  })
})

describe('cuentaComoTrabajo', () => {
  it('solo el calentamiento queda fuera; sin campo = normal', () => {
    expect(cuentaComoTrabajo({})).toBe(true)
    expect(cuentaComoTrabajo({ tipoSerie: 'normal' })).toBe(true)
    expect(cuentaComoTrabajo({ tipoSerie: 'fallo' })).toBe(true)
    expect(cuentaComoTrabajo({ tipoSerie: 'drop' })).toBe(true)
    expect(cuentaComoTrabajo({ tipoSerie: 'calentamiento' })).toBe(false)
  })
})

describe('recordsBatidos', () => {
  it('primera serie de un ejercicio: solo fija la base, sin aviso', () => {
    const nueva = s(100, 5)
    expect(recordsBatidos(nueva, [], [nueva], false)).toEqual([])
    expect(recordsBatidos(nueva, [rec(200, 5, 'otro')], [], false)).toEqual([])
  })
  it('batir la base del servidor => peso y e1rm', () => {
    expect(recordsBatidos(s(102.5, 5), [rec(100, 5)], [], false)).toEqual(['peso', 'e1rm'])
  })
  it('igualar no es récord (estrictamente mayor); más reps con el mismo peso bate solo e1rm', () => {
    expect(recordsBatidos(s(100, 5), [rec(100, 5)], [], false)).toEqual([])
    expect(recordsBatidos(s(100, 6), [rec(100, 5)], [], false)).toEqual(['e1rm'])
  })
  it('servidor vacío: la segunda local más pesada bate a la primera; la nueva no se compara consigo misma', () => {
    const primera = s(80, 5)
    const nueva = s(85, 5)
    expect(recordsBatidos(nueva, [], [primera, nueva], false)).toEqual(['peso', 'e1rm'])
  })
  it('las locales suben la base: no se vuelve a avisar lo ya batido hoy', () => {
    expect(recordsBatidos(s(102.5, 5), [rec(100, 5)], [s(105, 5)], false)).toEqual([])
  })
  it('calentamiento y rechazadas nunca avisan ni suben la base', () => {
    expect(recordsBatidos(s(200, 5, { tipoSerie: 'calentamiento' }), [rec(100, 5)], [], false)).toEqual([])
    expect(recordsBatidos(s(200, 5, { estado: 'rechazada' }), [rec(100, 5)], [], false)).toEqual([])
    const locales = [s(200, 5, { tipoSerie: 'calentamiento' }), s(200, 5, { estado: 'rechazada' })]
    expect(recordsBatidos(s(102.5, 5), [rec(100, 5)], locales, false)).toEqual(['peso', 'e1rm'])
  })
  it('fallo y drop sí cuentan', () => {
    expect(recordsBatidos(s(102.5, 5, { tipoSerie: 'fallo' }), [rec(100, 5)], [], false)).toEqual(['peso', 'e1rm'])
  })
  it('sin e1RM previo (todo de más de 12 reps): la nueva lo fija sin aviso', () => {
    expect(recordsBatidos(s(40, 10), [rec(40, 15)], [], false)).toEqual([])
  })
  it('carga corporal: lastre 0 que bate reps => solo reps; con lastre mayor, peso', () => {
    expect(recordsBatidos(s(0, 12), [rec(0, 10)], [], true)).toEqual(['reps'])
    expect(recordsBatidos(s(5, 8), [rec(0, 10)], [], true)).toEqual(['peso'])
    expect(recordsBatidos(s(10, 11), [rec(5, 10)], [], true)).toEqual(['peso', 'reps'])
    expect(recordsBatidos(s(0, 10), [rec(0, 10)], [], true)).toEqual([])
  })
})

describe('fusionarRecords', () => {
  it('máximo entre servidor y locales que cuentan; crea entradas solo locales', () => {
    const r = fusionarRecords([rec(100, 5)], [s(90, 15), s(100, 8), s(30, 5, { ejercicioId: 'curl' })])
    const banca = r.find((x) => x.ejercicioId === 'banca')!
    expect(banca.pesoMax).toMatchObject({ pesoKg: 100, reps: 8 })
    expect(banca.e1rm).toMatchObject({ kg: 126.67, pesoKg: 100, reps: 8 })
    expect(banca.repsMax).toMatchObject({ reps: 15, pesoKg: 90 })
    expect(r.find((x) => x.ejercicioId === 'curl')).toMatchObject({ pesoMax: { pesoKg: 30 }, e1rm: { kg: 35 } })
  })
})

describe('textoRecord', () => {
  it('texto corto con pesos ya formateados', () => {
    expect(textoRecord({ tipos: [], reps: 5, pesoTexto: '100' })).toBe('')
    expect(textoRecord({ tipos: ['peso'], reps: 5, pesoTexto: '100 kg' })).toBe('Récord de peso: 100 kg × 5')
    expect(textoRecord({ tipos: ['peso', 'e1rm'], reps: 5, pesoTexto: '100 kg', e1rmTexto: '116.67 kg' })).toBe(
      'Récord de peso y 1RM estimado: 100 kg × 5 (1RM est. 116.67 kg)',
    )
    expect(textoRecord({ tipos: ['reps'], reps: 12, pesoTexto: null })).toBe('Récord de reps: 12 reps')
  })
})

describe('tipos de serie', () => {
  it('ciclo normal → calentamiento → fallo → drop → normal', () => {
    expect(siguienteTipo(undefined)).toBe('calentamiento')
    expect(siguienteTipo('normal')).toBe('calentamiento')
    expect(siguienteTipo('calentamiento')).toBe('fallo')
    expect(siguienteTipo('fallo')).toBe('drop')
    expect(siguienteTipo('drop')).toBe('normal')
  })
  it('solo se hereda el calentamiento', () => {
    expect(tipoHeredado('calentamiento')).toBe('calentamiento')
    expect(tipoHeredado('fallo')).toBeUndefined()
    expect(tipoHeredado('drop')).toBeUndefined()
    expect(tipoHeredado('normal')).toBeUndefined()
    expect(tipoHeredado(undefined)).toBeUndefined()
  })
})

describe('textoRecord en inglés', () => {
  afterEach(() => fijarIdioma('es'))
  it('traduce el aviso y la unión de tipos', () => {
    fijarIdioma('en')
    expect(textoRecord({ tipos: ['peso', 'e1rm'], reps: 5, pesoTexto: '100 kg', e1rmTexto: '116.67 kg' })).toBe(
      'Weight and estimated 1RM record: 100 kg × 5 (est. 1RM 116.67 kg)',
    )
    expect(textoRecord({ tipos: ['reps'], reps: 12, pesoTexto: null })).toBe('Reps record: 12 reps')
  })
})
