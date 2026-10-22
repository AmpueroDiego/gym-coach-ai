import { DESCANSOS } from '../contador'
import { textos, type Idioma, type NombresEj } from '../i18n'
import { etiquetaDescanso } from '../presentacion'

interface Props {
  idioma: Idioma
  alCambiarIdioma: (i: Idioma) => void
  nombres: NombresEj
  alCambiarNombres: (n: NombresEj) => void
  descansoS: number
  alCambiarDescanso: (s: number) => void
}

const t = textos(
  {
    idiomaApp: 'Idioma de la app',
    idiomaAyuda: 'Pantallas, fechas y respuestas del coach.',
    nombres: 'Nombres de ejercicios',
    nombresAyuda: 'Cómo se muestran los nombres.',
    enApp: 'En el idioma de la app',
    enAmbos: 'En ambos idiomas',
    descanso: 'Descanso fuera de una rutina',
    descansoAyuda: 'Al guardar una serie de un ejercicio que no está en la rutina de hoy.',
  },
  {
    idiomaApp: 'App language',
    idiomaAyuda: 'Screens, dates and coach replies.',
    nombres: 'Exercise names',
    nombresAyuda: 'How names are shown.',
    enApp: 'In the app language',
    enAmbos: 'In both languages',
    descanso: 'Rest outside a routine',
    descansoAyuda: "When you log a set of an exercise that isn't in today's routine.",
  },
)

// Cada idioma se nombra en el suyo: se reconoce aunque la app esté en el otro.
const IDIOMAS: { valor: Idioma; texto: string }[] = [
  { valor: 'es', texto: 'Español' },
  { valor: 'en', texto: 'English' },
]

// Chips grandes en rejilla: se eligen con el pulgar y caben en pantallas de 360 px.
export default function Configuracion(p: Props) {
  const x = t()
  const NOMBRES: { valor: NombresEj; texto: string }[] = [
    { valor: 'app', texto: x.enApp },
    { valor: 'ambos', texto: x.enAmbos },
  ]
  return (
    <div className="configuracion">
      <section className="bloque">
        <h2>{x.idiomaApp}</h2>
        <fieldset className="chips-opcion idiomas">
          <legend className="tenue">{x.idiomaAyuda}</legend>
          {IDIOMAS.map((i) => (
            <label key={i.valor} className="chip-opcion">
              <input type="radio" name="idioma-app" checked={p.idioma === i.valor} onChange={() => p.alCambiarIdioma(i.valor)} />
              {i.texto}
            </label>
          ))}
        </fieldset>
      </section>

      <section className="bloque">
        <h2>{x.nombres}</h2>
        <fieldset className="chips-opcion idiomas">
          <legend className="tenue">{x.nombresAyuda}</legend>
          {NOMBRES.map((n) => (
            <label key={n.valor} className="chip-opcion">
              <input type="radio" name="nombres" checked={p.nombres === n.valor} onChange={() => p.alCambiarNombres(n.valor)} />
              {n.texto}
            </label>
          ))}
        </fieldset>
      </section>

      <section className="bloque">
        <h2>{x.descanso}</h2>
        <fieldset className="chips-opcion descansos">
          <legend className="tenue">{x.descansoAyuda}</legend>
          {DESCANSOS.map((s) => (
            <label key={s} className={s === 0 ? 'chip-opcion entero' : 'chip-opcion'}>
              <input type="radio" name="descanso" checked={p.descansoS === s} onChange={() => p.alCambiarDescanso(s)} />
              {etiquetaDescanso(s)}
            </label>
          ))}
        </fieldset>
      </section>
    </div>
  )
}
