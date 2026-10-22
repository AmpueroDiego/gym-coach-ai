import { afterEach, describe, expect, it } from 'vitest'
import { fijarIdioma } from './i18n'
import type { ParaCoach, PerfilEntrenamiento } from './api'
import {
  paraCoachValido,
  perfilEntrenamientoValido,
  textoCalculado,
  textoDivision,
  textoFrecuencia,
  textoHorario,
  textoMarca,
  textoTendencia,
} from './perfilEntrenamiento'

const marca = { pesoKg: 80, reps: 8, fecha: '2026-10-06' }
const ejercicio = (i: number) => ({
  ejercicioId: `ej-${i}`,
  nombre: `Ejercicio ${i}`,
  grupo: 'pecho',
  equipo: 'libre',
  cargaCorporal: false,
  sesiones: 3,
  series: 9,
  primeraFecha: '2026-09-01',
  ultimaFecha: '2026-10-06',
  ultimaSerie: marca,
  mejorSerie: marca,
  e1rmMaxKg: 100.5,
})
const tendencia = { ejercicioId: 'ej-1', nombre: 'Ejercicio 1', estado: 'progreso', reciente: marca, anterior: null, cambioPct: 5.2 }
const perfil = (): Record<string, unknown> => ({
  version: 1,
  calculadoEl: '2026-10-07T12:00:00Z',
  hoy: '2026-10-07',
  seriesAnalizadas: 100,
  truncado: false,
  primerEntreno: '2026-09-01',
  ultimoEntreno: '2026-10-06',
  diasEntrenados: 12,
  habitos: {
    desde: '2026-08-12',
    hasta: '2026-10-06',
    dias: 12,
    diasPorSemana: 3.5,
    division: [{ grupos: ['pecho', 'triceps'], veces: 5 }],
    rutinasFrecuentes: [{ nombre: 'Push <b>A</b>', veces: 4 }],
    horario: { franja: 'tarde', horaTipica: '18:00', sesiones: 9 },
    duracionTipicaMin: 55,
    duracionSesionesMedidas: 9,
    seriesPorSesion: 18,
  },
  equipo: { inferido: 'libre', pctMaquina: 20, seriesContadas: 100 },
  principales: ['ej-1'],
  tendencias: [tendencia],
  ejercicios: [ejercicio(1), ejercicio(2)],
})
const paraCoach = (): Record<string, unknown> => ({
  calculadoEl: '2026-10-07T12:00:00Z',
  seriesAnalizadas: 100,
  truncado: false,
  primerEntreno: '2026-09-01',
  ultimoEntreno: '2026-10-06',
  diasDesdeUltimo: 1,
  diasEntrenados: 12,
  historialSuficiente: true,
  habitos: { diasPorSemana: 3.5, division: [], rutinasFrecuentes: [], horario: null, duracionTipicaMin: null, seriesPorSesion: 18 },
  equipoPreferido: 'libre',
  equipoOrigen: 'historial',
  pctSeriesMaquina: 20,
  principales: [{ ejercicioId: 'ej-1', nombre: 'Ejercicio 1', sesiones: 3 }],
  tendencias: [tendencia],
})

describe('perfilEntrenamientoValido', () => {
  it('acepta un perfil completo y uno sin hábitos', () => {
    expect(perfilEntrenamientoValido(perfil())?.ejercicios).toHaveLength(2)
    expect(perfilEntrenamientoValido({ ...perfil(), habitos: null, equipo: { inferido: null, pctMaquina: null, seriesContadas: 0 } })?.habitos).toBeNull()
  })
  it('rechaza formas rotas y tipos malos', () => {
    for (const x of [null, undefined, 3, 'x', [], {}, { ...perfil(), version: '1' }, { ...perfil(), hoy: 'ayer' }, { ...perfil(), ejercicios: 'no' }]) {
      expect(perfilEntrenamientoValido(x)).toBeNull()
    }
    expect(perfilEntrenamientoValido({ ...perfil(), ejercicios: [{ ...ejercicio(1), equipo: 'raro' }] })).toBeNull()
    expect(perfilEntrenamientoValido({ ...perfil(), ejercicios: [{ ...ejercicio(1), sesiones: -1 }] })).toBeNull()
    expect(perfilEntrenamientoValido({ ...perfil(), ejercicios: [{ ...ejercicio(1), ultimaSerie: { pesoKg: NaN, reps: 1, fecha: '2026-10-06' } }] })).toBeNull()
    expect(perfilEntrenamientoValido({ ...perfil(), tendencias: [{ ...tendencia, estado: 'otro' }] })).toBeNull()
  })
  it('recorta las listas y descarta claves de más, incluida __proto__', () => {
    const muchos = { ...perfil(), principales: Array.from({ length: 9 }, (_, i) => `e${i}`), ejercicios: Array.from({ length: 600 }, (_, i) => ejercicio(i)) }
    const r = perfilEntrenamientoValido(muchos) as PerfilEntrenamiento
    expect(r.principales).toHaveLength(6)
    expect(r.ejercicios).toHaveLength(500)
    const sucio = JSON.parse(JSON.stringify(perfil()).replace('{"version"', '{"__proto__":{"admin":true},"extra":1,"version"')) as unknown
    const limpio = perfilEntrenamientoValido(sucio) as PerfilEntrenamiento
    expect(limpio).not.toBeNull()
    expect(Object.keys(limpio)).not.toContain('extra')
    expect(Object.keys(limpio)).not.toContain('__proto__')
    expect(({} as Record<string, unknown>).admin).toBeUndefined()
    expect((limpio as unknown as Record<string, unknown>).admin).toBeUndefined()
  })
})

describe('paraCoachValido', () => {
  it('acepta el objeto del coach, con y sin hábitos', () => {
    expect(paraCoachValido(paraCoach())?.principales).toHaveLength(1)
    expect(paraCoachValido({ ...paraCoach(), habitos: null, equipoPreferido: null, equipoOrigen: null })?.habitos).toBeNull()
  })
  it('rechaza formas rotas', () => {
    for (const x of [null, [], 'x', { ...paraCoach(), historialSuficiente: 'si' }, { ...paraCoach(), equipoOrigen: 'otro' }, { ...paraCoach(), equipoPreferido: 'pesas' }, { ...paraCoach(), principales: [{ ejercicioId: 1 }] }]) {
      expect(paraCoachValido(x)).toBeNull()
    }
  })
  it('recorta tendencias y no deja pasar __proto__', () => {
    const r = paraCoachValido({ ...paraCoach(), tendencias: Array(30).fill(tendencia) }) as ParaCoach
    expect(r.tendencias).toHaveLength(10)
    const sucio = JSON.parse('{"__proto__":{"admin":true},' + JSON.stringify(paraCoach()).slice(1)) as unknown
    const limpio = paraCoachValido(sucio) as ParaCoach
    expect((limpio as unknown as Record<string, unknown>).admin).toBeUndefined()
  })
  it('conserva los textos del usuario tal cual (se pintan como texto)', () => {
    const r = paraCoachValido({ ...paraCoach(), principales: [{ ejercicioId: 'x', nombre: '<img src=x onerror=alert(1)>', sesiones: 1 }] })
    expect(r?.principales[0].nombre).toBe('<img src=x onerror=alert(1)>')
  })
})

describe('formateadores', () => {
  it('frecuencia', () => {
    expect(textoFrecuencia(3.5)).toBe('3.5 días por semana')
    expect(textoFrecuencia(1)).toBe('1 día por semana')
  })
  it('división con etiquetas y plural', () => {
    expect(textoDivision({ grupos: ['pecho', 'triceps'], veces: 5 })).toBe('Pecho + Tríceps · 5 veces')
    expect(textoDivision({ grupos: ['espalda'], veces: 1 })).toBe('Espalda · 1 vez')
  })
  it('horario', () => {
    expect(textoHorario({ franja: 'tarde', horaTipica: '18:00', sesiones: 9 })).toBe('Tarde, hacia las 18:00 (9 sesiones)')
    expect(textoHorario({ franja: 'manana', horaTipica: '07:30', sesiones: 1 })).toBe('Mañana, hacia las 07:30 (1 sesión)')
  })
  it('tendencia', () => {
    expect(textoTendencia({ estado: 'progreso', cambioPct: 5.2 })).toBe('Progreso +5.2 %')
    expect(textoTendencia({ estado: 'baja', cambioPct: -3 })).toBe('A la baja -3 %')
    expect(textoTendencia({ estado: 'sin_comparar', cambioPct: null })).toBe('Sin comparar')
  })
  it('marca en kg, en lb y en carga corporal', () => {
    expect(textoMarca(marca, 'kg')).toBe('80 kg × 8')
    expect(textoMarca({ ...marca, pesoKg: 45 }, 'lb')).toBe('99.2 lb × 8')
    expect(textoMarca({ ...marca, pesoKg: 0 }, 'kg', true)).toBe('8 reps')
    expect(textoMarca({ ...marca, pesoKg: 10 }, 'kg', true)).toBe('10 kg × 8')
  })
  it('calculado: vacío si la fecha no se entiende', () => {
    expect(textoCalculado('basura')).toBe('')
    expect(textoCalculado('2026-10-07T12:00:00Z')).toMatch(/\d{2}:\d{2}$/)
  })
})

describe('textos del perfil en inglés', () => {
  afterEach(() => fijarIdioma('es'))
  it('frecuencia, horario y tendencia', () => {
    fijarIdioma('en')
    expect(textoFrecuencia(3)).toBe('3 days per week')
    expect(textoFrecuencia(1)).toBe('1 day per week')
    expect(textoHorario({ franja: 'tarde', horaTipica: '18:00', sesiones: 9 })).toBe('Afternoon, around 18:00 (9 sessions)')
    expect(textoTendencia({ estado: 'progreso', cambioPct: 5.2 })).toBe('Progressing +5.2 %')
  })
})
