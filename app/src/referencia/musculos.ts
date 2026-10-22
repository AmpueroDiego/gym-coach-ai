import type { IExerciseData, Muscle } from 'react-body-highlighter'
import type { MusculoRef } from './referencia'

// Músculos de free-exercise-db -> nombres de react-body-highlighter.
// Trampa de la lib: 'abductors' (vista anterior) y 'adductor' (posterior) dibujan el muslo
// INTERNO (polígonos pegados a la línea media, x~37-64), o sea son aductores aunque se llamen al revés.
// Los abductores de cadera (glúteo medio) se pintan como 'gluteal'.
export const A_RBH: Record<MusculoRef, Muscle[]> = {
  abdominals: ['abs'],
  abductors: ['gluteal'],
  adductors: ['abductors', 'adductor'],
  biceps: ['biceps'],
  calves: ['calves', 'left-soleus', 'right-soleus'],
  chest: ['chest'],
  forearms: ['forearm'],
  glutes: ['gluteal'],
  hamstrings: ['hamstring'],
  lats: ['upper-back'],
  'lower back': ['lower-back'],
  'middle back': ['upper-back'],
  neck: ['neck'],
  quadriceps: ['quadriceps'],
  shoulders: ['front-deltoids', 'back-deltoids'],
  traps: ['trapezius'],
  triceps: ['triceps'],
}

// Solo estos se dibujan en la vista posterior; el resto, en la anterior.
const POSTERIOR: ReadonlySet<Muscle> = new Set<Muscle>([
  'trapezius', 'upper-back', 'lower-back', 'gluteal', 'hamstring', 'back-deltoids', 'adductor',
  'left-soleus', 'right-soleus',
])

function unicos(lista: MusculoRef[]): Muscle[] {
  return [...new Set(lista.flatMap((m) => A_RBH[m] ?? []))]
}

// La lib suma frecuencias por músculo: principal = 2 (rojo fuerte), secundario = 1 (rojo suave).
// Un músculo en ambas listas queda solo como principal.
export function datosSilueta(principales: MusculoRef[], secundarios: MusculoRef[]): IExerciseData[] {
  const p = unicos(principales)
  const s = unicos(secundarios).filter((m) => !p.includes(m))
  const datos: IExerciseData[] = []
  if (p.length > 0) datos.push({ name: 'principales', muscles: p, frequency: 2 })
  if (s.length > 0) datos.push({ name: 'secundarios', muscles: s, frequency: 1 })
  return datos
}

// Vista donde cae la mayoría de los músculos principales; empate o vacío, anterior.
export function vistaPrincipal(principales: MusculoRef[]): 'anterior' | 'posterior' {
  const m = unicos(principales)
  const atras = m.filter((x) => POSTERIOR.has(x)).length
  return atras > m.length - atras ? 'posterior' : 'anterior'
}
