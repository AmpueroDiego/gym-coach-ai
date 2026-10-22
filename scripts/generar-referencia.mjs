// Genera app/src/referencia/datos.json cruzando scripts/mapa-referencia.json con
// free-exercise-db (SHA fijo). Uso: node scripts/generar-referencia.mjs [--en]
// --en imprime las instrucciones en inglés por slug (para redactar los pasos) y no escribe.
import { readFileSync, writeFileSync, existsSync } from 'node:fs'

const SHA = 'f00c92c7dcf1216a928a52c3706c7ce8e2f71ed5'
const URL_DATASET = `https://raw.githubusercontent.com/yuhonas/free-exercise-db/${SHA}/dist/exercises.json`
const MAPA = new URL('./mapa-referencia.json', import.meta.url)
const SALIDA = new URL('../app/src/referencia/datos.json', import.meta.url)
const RE_IMG = /^[A-Za-z0-9_-]+\/[01]\.jpg$/

const EQUIPO = {
  barbell: 'Barra', dumbbell: 'Mancuernas', cable: 'Polea', machine: 'Máquina',
  'body only': 'Peso corporal', kettlebells: 'Pesas rusas', bands: 'Bandas',
  'e-z curl bar': 'Barra Z', 'medicine ball': 'Balón medicinal',
  'exercise ball': 'Fitball', 'foam roll': 'Rodillo de espuma', other: 'Otro',
}

// throw en vez de process.exit: en Windows exit tras fetch deja un assert de libuv.
const fallo = (msg) => { throw new Error(msg) }

const mapa = JSON.parse(readFileSync(MAPA, 'utf8'))
const res = await fetch(URL_DATASET)
if (!res.ok) fallo(`no pude bajar el dataset (${res.status})`)
const porId = new Map((await res.json()).map((e) => [e.id, e]))

if (process.argv.includes('--en')) imprimirIngles()
else generar()

function imprimirIngles() {
  for (const [slug, { refId }] of Object.entries(mapa)) {
    const e = porId.get(refId)
    console.log(`\n## ${slug} (${refId}) [${e?.equipment ?? '?'}]`)
    ;(e?.instructions ?? []).forEach((p, i) => console.log(`${i + 1}. ${p}`))
  }
}

function generar() {
  const previo = existsSync(SALIDA) ? JSON.parse(readFileSync(SALIDA, 'utf8')).ejercicios ?? {} : {}
  const ejercicios = {}
  for (const [slug, { refId, aproximada }] of Object.entries(mapa)) {
    const e = porId.get(refId)
    if (!e) fallo(`${slug}: refId ${refId} no existe en el dataset`)
    if (!Array.isArray(e.images) || e.images.length !== 2) fallo(`${slug}: se esperaban 2 imágenes`)
    for (const img of e.images) if (!RE_IMG.test(img)) fallo(`${slug}: imagen inválida ${img}`)
    // Si el refId cambió, lo editado a mano ya no aplica: se descarta y se avisa.
    const ant = previo[slug]?.refId === refId ? previo[slug] : undefined
    if (previo[slug] && !ant) console.warn(`AVISO: ${slug} cambió de refId; se descartan pasos, equipo (es/en) y músculos previos`)
    ejercicios[slug] = {
      refId,
      // Conserva el equipo editado a mano; si no hay, usa el diccionario.
      equipo: ant?.equipo || EQUIPO[e.equipment ?? 'other'] || 'Otro',
      // Conserva el equipo en inglés redactado a mano.
      equipoEn: ant?.equipoEn ?? '',
      principales: ant?.principales ?? e.primaryMuscles,
      secundarios: ant?.secundarios ?? e.secondaryMuscles,
      // Los pasos se redactan a mano (es y en): nunca se pisan.
      pasos: ant?.pasos?.length ? ant.pasos : [],
      pasosEn: ant?.pasosEn?.length ? ant.pasosEn : [],
      imagenes: e.images,
      ...(aproximada ? { aproximada: true } : {}),
    }
  }
  const datos = { fuente: { repo: 'yuhonas/free-exercise-db', sha: SHA, licencia: 'Unlicense' }, ejercicios }
  writeFileSync(SALIDA, JSON.stringify(datos, null, 2) + '\n')
  console.log(`OK: ${Object.keys(ejercicios).length} ejercicios`)
}
