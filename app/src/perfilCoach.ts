// Validación del perfil del coach en la PWA: espejo de la del backend (que es la que manda).
import type { EquipoPreferido, Experiencia, PerfilCoach } from './api'
import { textos } from './i18n'

export const MIN_MINUTOS = 10
export const MAX_MINUTOS = 180
export const MIN_ALTURA_CM = 100
export const MAX_ALTURA_CM = 250
export const MIN_PESO_KG = 30
export const MAX_PESO_KG = 300
export const MIN_DIAS = 1
export const MAX_DIAS = 7
export const MAX_TEXTO = 200
export const MAX_NOTAS = 10

const t = textos(
  {
    maquina: 'Máquinas',
    libre: 'Peso libre',
    mixto: 'Mixto',
    deduzca: 'Que lo deduzca',
    nunca: 'Nunca he entrenado',
    menos6m: 'Menos de 6 meses',
    mas6m: 'Más de 6 meses',
    sinDefinir: 'Sin definir',
    dias: `Entre ${MIN_DIAS} y ${MAX_DIAS} días.`,
    experiencia: 'Elige tu experiencia.',
    altura: `Entre ${MIN_ALTURA_CM} y ${MAX_ALTURA_CM} cm.`,
    peso: `Entre ${MIN_PESO_KG} y ${MAX_PESO_KG} kg, con un decimal como máximo.`,
    minutos: `Entre ${MIN_MINUTOS} y ${MAX_MINUTOS} minutos.`,
    objetivo: `El objetivo va en una línea de hasta ${MAX_TEXTO} caracteres.`,
    equipo: 'Elige máquinas, peso libre, mixto o que lo deduzca.',
    maxNotas: `Máximo ${MAX_NOTAS} notas.`,
    nota: `Cada nota va en una línea de hasta ${MAX_TEXTO} caracteres.`,
  },
  {
    maquina: 'Machines',
    libre: 'Free weights',
    mixto: 'Mixed',
    deduzca: 'Let it figure out',
    nunca: 'Never trained',
    menos6m: 'Less than 6 months',
    mas6m: 'More than 6 months',
    sinDefinir: 'Not set',
    dias: `Between ${MIN_DIAS} and ${MAX_DIAS} days.`,
    experiencia: 'Choose your experience.',
    altura: `Between ${MIN_ALTURA_CM} and ${MAX_ALTURA_CM} cm.`,
    peso: `Between ${MIN_PESO_KG} and ${MAX_PESO_KG} kg, with at most one decimal.`,
    minutos: `Between ${MIN_MINUTOS} and ${MAX_MINUTOS} minutes.`,
    objetivo: `The goal goes on one line of up to ${MAX_TEXTO} characters.`,
    equipo: 'Choose machines, free weights, mixed or let it figure out.',
    maxNotas: `At most ${MAX_NOTAS} notes.`,
    nota: `Each note goes on one line of up to ${MAX_TEXTO} characters.`,
  },
)

/** Opciones del selector; null = que el coach lo deduzca del historial. Texto como función: cambia con el idioma. */
export const EQUIPOS: { valor: EquipoPreferido | null; texto: () => string }[] = [
  { valor: 'maquina', texto: () => t().maquina },
  { valor: 'libre', texto: () => t().libre },
  { valor: 'mixto', texto: () => t().mixto },
  { valor: null, texto: () => t().deduzca },
]

export const EXPERIENCIAS: { valor: Experiencia | null; texto: () => string }[] = [
  { valor: 'nunca', texto: () => t().nunca },
  { valor: 'menos6m', texto: () => t().menos6m },
  { valor: 'mas6m', texto: () => t().mas6m },
  { valor: null, texto: () => t().sinDefinir },
]

export interface ErrorPerfil { campo: string; mensaje: string }

// Control (incluye salto de línea), uso privado, sin asignar, sustitutos sueltos, bidi y ancho cero.
const PROHIBIDO = /[\p{Cc}\p{Co}\p{Cn}\p{Cs}​-‏‪-‮⁦-⁩﻿]/u

/** Texto de una línea de 1..200 caracteres tras recortar, o null. */
export function textoPerfil(t: string): string | null {
  const r = t.trim()
  return r.length >= 1 && r.length <= MAX_TEXTO && !PROHIBIDO.test(r) ? r : null
}

/** "" = sin dato (null); solo dígitos; cualquier otra cosa es NaN. */
export function minutosDeTexto(t: string): number | null {
  const r = t.trim()
  if (r === '') return null
  return /^\d{1,4}$/.test(r) ? Number(r) : NaN
}

/** "" = sin dato (null); un solo dígito; cualquier otra cosa es NaN. */
export function diasDeTexto(t: string): number | null {
  const r = t.trim()
  if (r === '') return null
  return /^\d$/.test(r) ? Number(r) : NaN
}

/** "" = sin dato (null); 1 a 3 dígitos; cualquier otra cosa es NaN. */
export function alturaDeTexto(t: string): number | null {
  const r = t.trim()
  if (r === '') return null
  return /^\d{1,3}$/.test(r) ? Number(r) : NaN
}

/** "" = sin dato (null); coma o punto decimal, un decimal como máximo; cualquier otra cosa es NaN. */
export function pesoDeTexto(t: string): number | null {
  const r = t.trim()
  if (r === '') return null
  return /^\d{1,3}([.,]\d)?$/.test(r) ? Number(r.replace(',', '.')) : NaN
}

export function validarPerfil(p: PerfilCoach): ErrorPerfil[] {
  const e: ErrorPerfil[] = []
  const a = p.alturaCm
  if (a !== null && (!Number.isInteger(a) || a < MIN_ALTURA_CM || a > MAX_ALTURA_CM)) {
    e.push({ campo: 'alturaCm', mensaje: t().altura })
  }
  const w = p.pesoCorporalKg
  if (w !== null && (!(w >= MIN_PESO_KG && w <= MAX_PESO_KG) || Math.round(w * 10) / 10 !== w)) {
    e.push({ campo: 'pesoCorporalKg', mensaje: t().peso })
  }
  const m = p.minutosDisponibles
  if (m !== null && (!Number.isInteger(m) || m < MIN_MINUTOS || m > MAX_MINUTOS)) {
    e.push({ campo: 'minutosDisponibles', mensaje: t().minutos })
  }
  const d = p.diasPorSemana
  if (d != null && (!Number.isInteger(d) || d < MIN_DIAS || d > MAX_DIAS)) {
    e.push({ campo: 'diasPorSemana', mensaje: t().dias })
  }
  if (p.experiencia != null && !EXPERIENCIAS.some((o) => o.valor === p.experiencia)) {
    e.push({ campo: 'experiencia', mensaje: t().experiencia })
  }
  if (p.objetivo !== null && textoPerfil(p.objetivo) === null) {
    e.push({ campo: 'objetivo', mensaje: t().objetivo })
  }
  if (p.equipo != null && !EQUIPOS.some((o) => o.valor === p.equipo)) e.push({ campo: 'equipo', mensaje: t().equipo })
  if (p.notas.length > MAX_NOTAS) e.push({ campo: 'notas', mensaje: t().maxNotas })
  if (p.notas.some((n) => textoPerfil(n) === null)) {
    e.push({ campo: 'notas', mensaje: t().nota })
  }
  return e
}

/** Del formulario (todo texto) al perfil recortado, con sus errores. */
export function armarPerfil(
  minutos: string,
  objetivo: string,
  notas: string[],
  altura: string,
  peso: string,
  equipo: EquipoPreferido | null = null,
  // undefined = el usuario no lo tocó: la clave no se manda y el servidor conserva lo que el coach haya guardado.
  experiencia?: Experiencia | null,
  dias?: string,
): { perfil: PerfilCoach; errores: ErrorPerfil[] } {
  const perfil: PerfilCoach = {
    minutosDisponibles: minutosDeTexto(minutos),
    objetivo: objetivo.trim() === '' ? null : objetivo.trim(),
    notas: notas.map((n) => n.trim()),
    alturaCm: alturaDeTexto(altura),
    pesoCorporalKg: pesoDeTexto(peso),
    // Siempre explícito: null borra la elección y el coach vuelve a deducirlo.
    equipo,
  }
  if (experiencia !== undefined) perfil.experiencia = experiencia
  if (dias !== undefined) perfil.diasPorSemana = diasDeTexto(dias)
  return { perfil, errores: validarPerfil(perfil) }
}

export type EstadoPerfil = 'sinCargar' | 'cargando' | 'errorCarga' | 'listo' | 'guardando'

/** Guardar solo tras una lectura exitosa: el PUT es completo y un formulario vacío por fallo de red borraría lesiones y objetivo. */
export const puedeGuardar = (estado: EstadoPerfil, conSesion: boolean): boolean => conSesion && estado === 'listo'
