import { idiomaApp } from '../i18n'
import datos from './datos.json'

// Músculos tal como los nombra free-exercise-db (yuhonas, Unlicense).
export type MusculoRef =
  | 'abdominals' | 'abductors' | 'adductors' | 'biceps' | 'calves' | 'chest'
  | 'forearms' | 'glutes' | 'hamstrings' | 'lats' | 'lower back' | 'middle back'
  | 'neck' | 'quadriceps' | 'shoulders' | 'traps' | 'triceps'

export interface FichaReferencia {
  refId: string
  equipo: string
  /** Máquina concreta en inglés. */
  equipoEn: string
  principales: MusculoRef[]
  secundarios: MusculoRef[]
  pasos: string[]
  /** Pasos en inglés; vacío = se muestran los de español. */
  pasosEn: string[]
  imagenes: [string, string]
  /** Foto de una máquina parecida, no la misma. */
  aproximada?: true
}

// Commit fijo del dataset: las fotos no cambian bajo nuestros pies.
export const REF_SHA = 'f00c92c7dcf1216a928a52c3706c7ce8e2f71ed5'

export const NOMBRE_MUSCULO: Record<MusculoRef, string> = {
  abdominals: 'Abdominales',
  abductors: 'Abductores',
  adductors: 'Aductores',
  biceps: 'Bíceps',
  calves: 'Pantorrillas',
  chest: 'Pecho',
  forearms: 'Antebrazos',
  glutes: 'Glúteos',
  hamstrings: 'Isquiotibiales',
  lats: 'Dorsales',
  'lower back': 'Zona lumbar',
  'middle back': 'Espalda media',
  neck: 'Cuello',
  quadriceps: 'Cuádriceps',
  shoulders: 'Hombros',
  traps: 'Trapecios',
  triceps: 'Tríceps',
}

export const NOMBRE_MUSCULO_EN: Record<MusculoRef, string> = {
  abdominals: 'Abs',
  abductors: 'Abductors',
  adductors: 'Adductors',
  biceps: 'Biceps',
  calves: 'Calves',
  chest: 'Chest',
  forearms: 'Forearms',
  glutes: 'Glutes',
  hamstrings: 'Hamstrings',
  lats: 'Lats',
  'lower back': 'Lower back',
  'middle back': 'Middle back',
  neck: 'Neck',
  quadriceps: 'Quads',
  shoulders: 'Shoulders',
  traps: 'Traps',
  triceps: 'Triceps',
}

export const nombreMusculo = (m: MusculoRef): string => (idiomaApp() === 'en' ? NOMBRE_MUSCULO_EN : NOMBRE_MUSCULO)[m]

const EJERCICIOS = datos.ejercicios as unknown as Record<string, FichaReferencia>

export function fichaDe(ejercicioId: string): FichaReferencia | null {
  if (!Object.hasOwn(EJERCICIOS, ejercicioId)) return null
  const ficha = EJERCICIOS[ejercicioId]
  return ficha.pasos.length > 0 ? ficha : null
}

export function urlImagen(ruta: string): string {
  return `https://raw.githubusercontent.com/yuhonas/free-exercise-db/${REF_SHA}/exercises/${ruta}`
}

/** En inglés usa pasosEn; si aún no está redactado cae al español. */
export const pasosDe = (f: FichaReferencia): string[] => (idiomaApp() === 'en' && f.pasosEn.length > 0 ? f.pasosEn : f.pasos)

export const equipoDe = (f: FichaReferencia): string => (idiomaApp() === 'en' && f.equipoEn ? f.equipoEn : f.equipo)
