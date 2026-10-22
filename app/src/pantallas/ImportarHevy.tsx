import { useRef } from 'react'
import type { AnalisisHevy } from '../hevy'
import { rangoTexto, seriesAImportar } from '../importarLotes'
import { textos } from '../i18n'
import type { Importacion } from '../useImportacionHevy'

const t = textos(
  {
    serieNueva: (n: number) => `${n} ${n === 1 ? 'serie nueva' : 'series nuevas'}`,
    serieRechazada: (n: number) => `${n} ${n === 1 ? 'serie rechazada' : 'series rechazadas'}`,
    recordSe: (n: number) => `${n} ${n === 1 ? 'récord se actualizará' : 'récords se actualizarán'}`,
    serie: (n: number) => `${n} ${n === 1 ? 'serie' : 'series'}`,
    entreno: (n: number) => `${n} ${n === 1 ? 'entreno' : 'entrenos'}`,
    ejercicio: (n: number) => `${n} ${n === 1 ? 'ejercicio' : 'ejercicios'}`,
    nota: (n: number) => `${n} ${n === 1 ? 'nota no se importa' : 'notas no se importan'}`,
    diaYa: (n: number) => `${n} ${n === 1 ? 'día ya tiene' : 'días ya tienen'}`,
    cancelando: 'Cancelando…',
    pausado: (h: number, tot: number, motivo: string) => `Pausado en ${h} de ${tot} series. ${motivo}`,
    esperando: 'Esperando al servidor…',
    progreso: (h: number, tot: number) => `${h} de ${tot} series`,
    listo: (nuevas: string, ya: number, rechazadas: string, records: string) =>
      `${nuevas}, ${ya} ya estaban. ${rechazadas}${records}`,
    rechazadasPor: (s: string) => `${s} por el servidor. `,
    recordsSolos: (s: string) => `${s} solos.`,
    recordsListos: 'Récords actualizados.',
    importarHevy: 'Importar de Hevy',
    progresoAria: 'Progreso de la importación',
    cancelar: 'Cancelar',
    continuar: 'Continuar',
    cerrar: 'Listo',
    pesosEn: (u: string) => `pesos en ${u}`,
    sinEmparejar: 'Sin emparejar (se omiten): ',
    omitidas: 'Omitidas: ',
    sinReps: (s: string) => `${s} sin repeticiones válidas`,
    pesoInvalido: (s: string) => `${s} con peso inválido`,
    fechaInvalida: (s: string) => `${s} con fecha inválida`,
    revise: (s: string) => `Revisé los últimos 30 días: ${s} series en gym-coach y se omiten`,
    noDuplica: 'Reimportar no duplica ni pisa lo que ya está',
    igual: 'Importar igual',
    importar: (s: string) => `Importar ${s}`,
  },
  {
    serieNueva: (n) => `${n} new ${n === 1 ? 'set' : 'sets'}`,
    serieRechazada: (n) => `${n} ${n === 1 ? 'set' : 'sets'} rejected`,
    recordSe: (n) => `${n} ${n === 1 ? 'record' : 'records'} will update`,
    serie: (n) => `${n} ${n === 1 ? 'set' : 'sets'}`,
    entreno: (n) => `${n} ${n === 1 ? 'workout' : 'workouts'}`,
    ejercicio: (n) => `${n} ${n === 1 ? 'exercise' : 'exercises'}`,
    nota: (n) => `${n} ${n === 1 ? 'note is' : 'notes are'} not imported`,
    diaYa: (n) => `${n} ${n === 1 ? 'day already has' : 'days already have'}`,
    cancelando: 'Canceling…',
    pausado: (h, tot, motivo) => `Paused at ${h} of ${tot} sets. ${motivo}`,
    esperando: 'Waiting for the server…',
    progreso: (h, tot) => `${h} of ${tot} sets`,
    listo: (nuevas, ya, rechazadas, records) => `${nuevas}, ${ya} already there. ${rechazadas}${records}`,
    rechazadasPor: (s) => `${s} by the server. `,
    recordsSolos: (s) => `${s} automatically.`,
    recordsListos: 'Records updated.',
    importarHevy: 'Import from Hevy',
    progresoAria: 'Import progress',
    cancelar: 'Cancel',
    continuar: 'Continue',
    cerrar: 'Done',
    pesosEn: (u) => `weights in ${u}`,
    sinEmparejar: 'Unmatched (skipped): ',
    omitidas: 'Skipped: ',
    sinReps: (s) => `${s} without valid reps`,
    pesoInvalido: (s) => `${s} with invalid weight`,
    fechaInvalida: (s) => `${s} with invalid date`,
    revise: (s) => `I checked the last 30 days: ${s} sets in gym-coach; they're skipped`,
    noDuplica: "Re-importing doesn't duplicate or overwrite what's already there",
    igual: 'Import anyway',
    importar: (s) => `Import ${s}`,
  },
)

/** Solo pinta: el estado y el bucle viven en useImportacionHevy (en Principal), así sobreviven al cambio de pestaña. */
export default function ImportarHevy({ imp }: { imp: Importacion }) {
  const entrada = useRef<HTMLInputElement>(null)
  const { fase } = imp
  const enCurso = fase.f === 'importando' || fase.f === 'pausado'
  const texto =
    fase.f === 'importando' || fase.f === 'pausado'
      ? imp.cancelando
        ? t().cancelando
        : fase.f === 'pausado'
          ? t().pausado(fase.hechas, fase.total, fase.motivo)
          : imp.esperandoServidor
            ? t().esperando
            : t().progreso(fase.hechas, fase.total)
      : fase.f === 'listo'
        ? t().listo(
            t().serieNueva(fase.guardadas),
            fase.existentes,
            fase.rechazadas > 0 ? t().rechazadasPor(t().serieRechazada(fase.rechazadas)) : '',
            fase.pendientes > 0 ? t().recordsSolos(t().recordSe(fase.pendientes)) : t().recordsListos,
          )
        : ''
  return (
    <div className="importar-hevy">
      <input ref={entrada} type="file" accept=".csv,text/csv" hidden onChange={(e) => void imp.elegirArchivo(e)} />
      {fase.f === 'inicio' && (
        <>
          <button className="secundario ancho" onClick={() => entrada.current?.click()}>
            {t().importarHevy}
          </button>
          {fase.error && (
            <p className="error" role="alert">
              {fase.error}
            </p>
          )}
        </>
      )}
      {fase.f === 'previa' && (
        <Previa
          a={fase.a}
          dias={fase.dias}
          importarIgual={imp.importarIgual}
          alCambiar={imp.setImportarIgual}
          alEmpezar={imp.empezar}
          alCancelar={imp.descartarPrevia}
        />
      )}
      {/* Siempre montada: un lector de pantalla anuncia los cambios, no el montaje. */}
      <div role="status" className={enCurso || fase.f === 'listo' ? 'tarjeta-importar' : undefined}>
        {enCurso && <progress className="barra-importar" max={fase.total} value={fase.hechas} aria-label={t().progresoAria} />}
        {texto && <p>{texto}</p>}
        {fase.f === 'importando' && (
          <button className="secundario ancho" disabled={imp.cancelando} onClick={imp.cancelar}>
            {t().cancelar}
          </button>
        )}
        {fase.f === 'pausado' && (
          <>
            <button className="primario ancho" onClick={imp.continuar}>
              {t().continuar}
            </button>
            <button className="secundario ancho" onClick={imp.cancelar}>
              {t().cancelar}
            </button>
          </>
        )}
        {fase.f === 'listo' && (
          <button className="secundario ancho" onClick={imp.cerrarListo}>
            {t().cerrar}
          </button>
        )}
      </div>
    </div>
  )
}

function Previa(p: {
  a: AnalisisHevy
  dias: string[]
  importarIgual: boolean
  alCambiar: (v: boolean) => void
  alEmpezar: () => void
  alCancelar: () => void
}) {
  const { a, dias } = p
  const n = seriesAImportar(a, dias, p.importarIgual).length
  const omitidas = [
    a.omitidas.reps > 0 && t().sinReps(t().serie(a.omitidas.reps)),
    a.omitidas.peso > 0 && t().pesoInvalido(t().serie(a.omitidas.peso)),
    a.omitidas.fecha > 0 && t().fechaInvalida(t().serie(a.omitidas.fecha)),
  ].filter(Boolean)
  return (
    <div className="tarjeta-importar">
      <p className="resumen-importar">
        {t().entreno(a.entrenos)} · {t().serie(a.series.length)} · {t().ejercicio(a.ejercicios)} ·{' '}
        {rangoTexto(a.desde, a.hasta)} · {t().pesosEn(a.unidad)}
      </p>
      <ul className="avisos-importar">
        {a.sinEmparejar.length > 0 && <li>{t().sinEmparejar}{a.sinEmparejar.map((s) => `${s.nombre} (${s.series})`).join(', ')}</li>}
        {omitidas.map((o) => (
          <li key={String(o)}>
            {t().omitidas}
            {o}
          </li>
        ))}
        {a.notasDescartadas > 0 && <li>{t().nota(a.notasDescartadas)}</li>}
        {dias.length > 0 && (
          <li>
            {t().revise(t().diaYa(dias.length))}
          </li>
        )}
        <li>{t().noDuplica}</li>
      </ul>
      {dias.length > 0 && (
        <label className="interruptor-importar">
          <input type="checkbox" checked={p.importarIgual} onChange={(e) => p.alCambiar(e.target.checked)} />
          {t().igual}
        </label>
      )}
      <button className="primario ancho" disabled={n === 0} onClick={p.alEmpezar}>
        {t().importar(t().serie(n))}
      </button>
      <button className="secundario ancho" onClick={p.alCancelar}>
        {t().cancelar}
      </button>
    </div>
  )
}
