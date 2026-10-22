// Récords por ejercicio y tipo de serie: qué cuenta como trabajo y cuándo una serie bate la marca.
import type { RecordEjercicio, Serie, TipoSerie } from './api'
import { textos } from './i18n'

export type TipoRecord = 'peso' | 'e1rm' | 'reps'

type SerieConEstado = Serie & { estado?: string }

/** El calentamiento no suma volumen, reps, récords ni series objetivo. */
export const cuentaComoTrabajo = (s: Pick<Serie, 'tipoSerie'>): boolean => s.tipoSerie !== 'calentamiento'

const round2 = (n: number) => Math.round(n * 100) / 100

/** Epley, misma regla que el servidor: más de 12 reps o sin peso no da una estimación fiable. */
export function e1rm(pesoKg: number, reps: number): number | null {
  if (reps > 12 || pesoKg === 0) return null
  return reps === 1 ? pesoKg : round2(pesoKg * (1 + reps / 30))
}

/** Base de cada ejercicio: el máximo entre el servidor y las series locales que cuentan (no rechazadas ni calentamiento). */
export function fusionarRecords(srv: RecordEjercicio[], locales: SerieConEstado[]): RecordEjercicio[] {
  const porId = new Map<string, RecordEjercicio>()
  for (const r of srv) porId.set(r.ejercicioId, r)
  for (const s of locales) {
    if (s.estado === 'rechazada' || !cuentaComoTrabajo(s)) continue
    const r = porId.get(s.ejercicioId)
    const kg = e1rm(s.pesoKg, s.reps)
    const e = kg === null ? null : { kg, pesoKg: s.pesoKg, reps: s.reps, fecha: s.fecha }
    if (!r) {
      porId.set(s.ejercicioId, {
        ejercicioId: s.ejercicioId,
        pesoMax: { pesoKg: s.pesoKg, reps: s.reps, fecha: s.fecha },
        e1rm: e,
        repsMax: { reps: s.reps, pesoKg: s.pesoKg, fecha: s.fecha },
      })
      continue
    }
    // Empates: gana la de más reps (peso) o más peso (reps); solo cambia lo que se muestra, no si hay récord.
    const p = r.pesoMax
    const m = r.repsMax
    porId.set(s.ejercicioId, {
      ejercicioId: s.ejercicioId,
      pesoMax: s.pesoKg > p.pesoKg || (s.pesoKg === p.pesoKg && s.reps > p.reps) ? { pesoKg: s.pesoKg, reps: s.reps, fecha: s.fecha } : p,
      e1rm: e && (!r.e1rm || e.kg > r.e1rm.kg) ? e : r.e1rm,
      repsMax: s.reps > m.reps || (s.reps === m.reps && s.pesoKg > m.pesoKg) ? { reps: s.reps, pesoKg: s.pesoKg, fecha: s.fecha } : m,
    })
  }
  return [...porId.values()]
}

/**
 * Qué marcas bate `nueva` (estrictamente mayor), en orden peso, e1rm, reps. `base`: lo de listarRecords;
 * `locales`: las series del almacén (puede incluir a `nueva`: se excluye por id). Sin base del ejercicio => []
 * (la primera serie solo fija la base). Carga corporal: reps, y peso solo con lastre; si no, peso y e1rm.
 */
export function recordsBatidos(
  nueva: SerieConEstado,
  base: RecordEjercicio[],
  locales: SerieConEstado[],
  corporal: boolean,
): TipoRecord[] {
  if (nueva.estado === 'rechazada' || !cuentaComoTrabajo(nueva)) return []
  const r = fusionarRecords(base, locales.filter((s) => s.id !== nueva.id)).find((x) => x.ejercicioId === nueva.ejercicioId)
  if (!r) return []
  const batidos: TipoRecord[] = []
  if (corporal) {
    if (nueva.pesoKg > 0 && nueva.pesoKg > r.pesoMax.pesoKg) batidos.push('peso')
    if (nueva.reps > r.repsMax.reps) batidos.push('reps')
    return batidos
  }
  if (nueva.pesoKg > r.pesoMax.pesoKg) batidos.push('peso')
  const kg = e1rm(nueva.pesoKg, nueva.reps)
  // Sin e1RM previo (todo era de más de 12 reps) no hay marca que batir: esta lo fija, igual que la primera serie.
  if (kg !== null && r.e1rm !== null && kg > r.e1rm.kg) batidos.push('e1rm')
  return batidos
}

const t = textos(
  {
    nombres: { peso: 'peso', e1rm: '1RM estimado', reps: 'reps' } as Record<TipoRecord, string>,
    y: ' y ',
    reps: (n: number) => `${n} reps`,
    extra: (e: string) => ` (1RM est. ${e})`,
    record: (que: string, serie: string, extra: string) => `Récord de ${que}: ${serie}${extra}`,
  },
  {
    nombres: { peso: 'weight', e1rm: 'estimated 1RM', reps: 'reps' } as Record<TipoRecord, string>,
    y: ' and ',
    reps: (n) => `${n} ${n === 1 ? 'rep' : 'reps'}`,
    extra: (e) => ` (est. 1RM ${e})`,
    record: (que, serie, extra) => `${que.charAt(0).toUpperCase()}${que.slice(1)} record: ${serie}${extra}`,
  },
)

/**
 * Aviso corto, p. ej. «Récord de peso y 1RM estimado: 100 kg × 5 (1RM est. 116,67 kg)». Los pesos llegan ya
 * formateados en la unidad del usuario; pesoTexto null (corporal sin lastre) => solo las reps. Sin tipos => ''.
 */
export function textoRecord(a: { tipos: TipoRecord[]; reps: number; pesoTexto: string | null; e1rmTexto?: string | null }): string {
  if (a.tipos.length === 0) return ''
  const nombres = a.tipos.map((k) => t().nombres[k])
  const que = nombres.length === 1 ? nombres[0] : `${nombres.slice(0, -1).join(', ')}${t().y}${nombres[nombres.length - 1]}`
  const serie = a.pesoTexto === null ? t().reps(a.reps) : `${a.pesoTexto} × ${a.reps}`
  const extra = a.tipos.includes('e1rm') && a.e1rmTexto ? t().extra(a.e1rmTexto) : ''
  return t().record(que, serie, extra)
}

/** Ciclo del botón de tipo: normal → calentamiento → fallo → drop → normal. */
export function siguienteTipo(t: TipoSerie | undefined): TipoSerie {
  switch (t) {
    case 'calentamiento':
      return 'fallo'
    case 'fallo':
      return 'drop'
    case 'drop':
      return 'normal'
    default:
      return 'calentamiento'
  }
}

/** Al sugerir la serie de la vez pasada solo se repite el calentamiento: fallo y drop dependen del día. */
export const tipoHeredado = (t: TipoSerie | undefined): TipoSerie | undefined => (t === 'calentamiento' ? t : undefined)
