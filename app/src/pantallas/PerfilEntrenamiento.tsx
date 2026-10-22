import type { ParaCoach } from '../api'
import { textos } from '../i18n'
import {
  paraCoachValido,
  textoCalculado,
  textoDivision,
  textoEquipo,
  textoFrecuencia,
  textoHorario,
  textoMarca,
  textoTendencia,
  usePerfilEntrenamiento,
} from '../perfilEntrenamiento'
import { etiquetaFecha } from '../presentacion'
import './perfilEntrenamiento.css'

/**
 * Sección "Lo que ve el coach" dentro de PerfilCoach (B4): campos de paraCoach, "Calculado el …" y el JSON literal.
 * - sub: cuenta dueña de la petición (leerPerfilEntrenamiento).
 * - conSesion: sin sesión no se pide nada; al recuperarla se vuelve a leer.
 * - hoy: día local YYYY-MM-DD (fechaLocal()); el offset lo saca el componente de getTimezoneOffset.
 */
interface Props {
  sub: string
  conSesion: boolean
  hoy: string
}

const t = textos(
  {
    origenPerfil: 'lo elegiste tú',
    origenHistorial: 'deducido de tus series',
    sinDatos: 'Sin datos suficientes',
    enMaquina: (pct: number) => `, ${pct} % en máquina`,
    pocoHistorial: 'Aún hay poco historial: el coach recomendará con lo que haya.',
    entrenos: 'Entrenos',
    dias: (n: number) => `${n} ${n === 1 ? 'día entrenado' : 'días entrenados'}`,
    ultimo: (f: string) => ` · último ${f}`,
    hace: (n: number) => ` (hace ${n} ${n === 1 ? 'día' : 'días'})`,
    frecuencia: 'Frecuencia',
    seriesPorSesion: (n: number) => `${n} series por sesión`,
    unosMin: (n: number) => ` · unos ${n} min`,
    division: 'División',
    horario: 'Horario',
    rutinasFrecuentes: 'Rutinas frecuentes',
    veces: (n: number) => `${n} ${n === 1 ? 'vez' : 'veces'}`,
    equipo: 'Equipo',
    principales: 'Ejercicios principales',
    sesiones: (n: number) => `${n} ${n === 1 ? 'sesión' : 'sesiones'}`,
    tendencias: 'Tendencias',
    truncado: 'Hay tantas series que faltan las más viejas.',
    calculado: (c: string) => `Calculado el ${c}`,
    verJson: 'Ver exactamente lo que recibe el coach',
    aria: 'Lo que ve el coach',
    titulo: 'Lo que ve el coach',
    reconecta: 'Reconecta para ver lo que sabe de ti.',
    cargando: 'Cargando…',
    noEntiende: 'La respuesta del servidor no se entiende.',
    reintentar: 'Reintentar',
  },
  {
    origenPerfil: 'you chose it',
    origenHistorial: 'inferred from your sets',
    sinDatos: 'Not enough data',
    enMaquina: (pct) => `, ${pct} % on machines`,
    pocoHistorial: 'Not much history yet: the coach will recommend with what there is.',
    entrenos: 'Workouts',
    dias: (n) => `${n} ${n === 1 ? 'day trained' : 'days trained'}`,
    ultimo: (f) => ` · last ${f}`,
    hace: (n) => ` (${n} ${n === 1 ? 'day' : 'days'} ago)`,
    frecuencia: 'Frequency',
    seriesPorSesion: (n) => `${n} sets per session`,
    unosMin: (n) => ` · about ${n} min`,
    division: 'Split',
    horario: 'Schedule',
    rutinasFrecuentes: 'Frequent routines',
    veces: (n) => `${n} ${n === 1 ? 'time' : 'times'}`,
    equipo: 'Equipment',
    principales: 'Main exercises',
    sesiones: (n) => `${n} ${n === 1 ? 'session' : 'sessions'}`,
    tendencias: 'Trends',
    truncado: 'There are so many sets that the oldest are missing.',
    calculado: (c) => `Calculated on ${c}`,
    verJson: 'See exactly what the coach receives',
    aria: 'What the coach sees',
    titulo: 'What the coach sees',
    reconecta: 'Reconnect to see what it knows about you.',
    cargando: 'Loading…',
    noEntiende: "The server's response isn't understood.",
    reintentar: 'Retry',
  },
)

/** Todo lo que viene del servidor (nombres de rutinas, ejercicios) se pinta como texto de React, nunca como HTML. */
export function VistaParaCoach({ p, json }: { p: ParaCoach; json: string }) {
  const h = p.habitos
  const origen = p.equipoOrigen === 'perfil' ? t().origenPerfil : t().origenHistorial
  const equipo =
    p.equipoPreferido === null
      ? t().sinDatos
      : `${textoEquipo(p.equipoPreferido)} (${origen}${p.pctSeriesMaquina === null ? '' : t().enMaquina(p.pctSeriesMaquina)})`
  const calculado = textoCalculado(p.calculadoEl)
  return (
    <>
      {!p.historialSuficiente && <p className="tenue">{t().pocoHistorial}</p>}
      <dl>
        <dt>{t().entrenos}</dt>
        <dd>
          {t().dias(p.diasEntrenados)}
          {p.ultimoEntreno && t().ultimo(etiquetaFecha(p.ultimoEntreno))}
          {p.diasDesdeUltimo !== null && t().hace(p.diasDesdeUltimo)}
        </dd>
        {h && (
          <>
            <dt>{t().frecuencia}</dt>
            <dd>
              {textoFrecuencia(h.diasPorSemana)} · {t().seriesPorSesion(Math.round(h.seriesPorSesion))}
              {h.duracionTipicaMin !== null && t().unosMin(h.duracionTipicaMin)}
            </dd>
            {h.division.length > 0 && (
              <>
                <dt>{t().division}</dt>
                <dd>
                  <ul>
                    {h.division.map((d, i) => (
                      <li key={i}>{textoDivision(d)}</li>
                    ))}
                  </ul>
                </dd>
              </>
            )}
            {h.horario && (
              <>
                <dt>{t().horario}</dt>
                <dd>{textoHorario(h.horario)}</dd>
              </>
            )}
            {h.rutinasFrecuentes.length > 0 && (
              <>
                <dt>{t().rutinasFrecuentes}</dt>
                <dd>
                  <ul>
                    {h.rutinasFrecuentes.map((r, i) => (
                      <li key={i}>
                        {r.nombre} · {t().veces(r.veces)}
                      </li>
                    ))}
                  </ul>
                </dd>
              </>
            )}
          </>
        )}
        <dt>{t().equipo}</dt>
        <dd>{equipo}</dd>
        {p.principales.length > 0 && (
          <>
            <dt>{t().principales}</dt>
            <dd>
              <ul>
                {p.principales.map((e) => (
                  <li key={e.ejercicioId}>
                    {e.nombre} · {t().sesiones(e.sesiones)}
                  </li>
                ))}
              </ul>
            </dd>
          </>
        )}
        {p.tendencias.length > 0 && (
          <>
            <dt>{t().tendencias}</dt>
            <dd>
              <ul>
                {p.tendencias.map((tn) => (
                  <li key={tn.ejercicioId}>
                    {tn.nombre}: {textoTendencia(tn)}
                    {tn.reciente && ` (${textoMarca(tn.reciente, 'kg')})`}
                  </li>
                ))}
              </ul>
            </dd>
          </>
        )}
      </dl>
      {p.truncado && <p className="tenue">{t().truncado}</p>}
      {calculado && <p className="tenue">{t().calculado(calculado)}</p>}
      <details>
        <summary>{t().verJson}</summary>
        <pre>{json}</pre>
      </details>
    </>
  )
}

export default function PerfilEntrenamiento({ sub, conSesion, hoy }: Props) {
  const { estado, datos, error, reintentar } = usePerfilEntrenamiento(sub, conSesion, hoy)
  const p = estado === 'listo' ? paraCoachValido(datos?.paraCoach) : null

  return (
    <section className="perfil-entrenamiento" aria-label={t().aria}>
      <h3>{t().titulo}</h3>
      {estado === 'sinSesion' && <p className="tenue">{t().reconecta}</p>}
      {estado === 'cargando' && <p className="tenue">{t().cargando}</p>}
      {(estado === 'error' || (estado === 'listo' && !p)) && (
        <>
          <p className="error" role="alert">
            {estado === 'error' ? error : t().noEntiende}
          </p>
          <button type="button" onClick={reintentar}>
            {t().reintentar}
          </button>
        </>
      )}
      {p && <VistaParaCoach p={p} json={JSON.stringify(datos?.paraCoach, null, 2)} />}
    </section>
  )
}
