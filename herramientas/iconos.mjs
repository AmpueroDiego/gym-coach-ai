// Genera app/public/icons/icon-{192,512}.png (mancuerna verde sobre fondo oscuro) sin dependencias.
// Uso: node herramientas/iconos.mjs
import { mkdirSync, writeFileSync } from 'node:fs'
import { deflateSync } from 'node:zlib'

const FONDO = [0x11, 0x14, 0x18]
const ACENTO = [0x3f, 0xb9, 0x50]
// Rectángulos en unidades de un lienzo de 64x64 (mismo dibujo que favicon.svg, con margen para maskable).
const RECTS = [
  [8, 22, 8, 20],
  [48, 22, 8, 20],
  [16, 18, 6, 28],
  [42, 18, 6, 28],
  [22, 29, 20, 6],
]

const crcTabla = Array.from({ length: 256 }, (_, n) => {
  let c = n
  for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1
  return c >>> 0
})
const crc32 = (buf) => {
  let c = 0xffffffff
  for (const b of buf) c = crcTabla[(c ^ b) & 0xff] ^ (c >>> 8)
  return (c ^ 0xffffffff) >>> 0
}
const chunk = (tipo, datos) => {
  const largo = Buffer.alloc(4)
  largo.writeUInt32BE(datos.length)
  const cuerpo = Buffer.concat([Buffer.from(tipo), datos])
  const crc = Buffer.alloc(4)
  crc.writeUInt32BE(crc32(cuerpo))
  return Buffer.concat([largo, cuerpo, crc])
}

function png(lado) {
  const escala = lado / 64
  // Reducir el dibujo al 80% centrado deja la zona segura de los íconos maskable.
  const dentro = (x, y) => {
    const u = ((x / escala - 32) / 0.8) + 32
    const v = ((y / escala - 32) / 0.8) + 32
    return RECTS.some(([rx, ry, rw, rh]) => u >= rx && u < rx + rw && v >= ry && v < ry + rh)
  }
  const filas = Buffer.alloc(lado * (lado * 3 + 1))
  for (let y = 0; y < lado; y++) {
    const o = y * (lado * 3 + 1)
    for (let x = 0; x < lado; x++) {
      const [r, g, b] = dentro(x + 0.5, y + 0.5) ? ACENTO : FONDO
      filas.set([r, g, b], o + 1 + x * 3)
    }
  }
  const ihdr = Buffer.alloc(13)
  ihdr.writeUInt32BE(lado, 0)
  ihdr.writeUInt32BE(lado, 4)
  ihdr.set([8, 2, 0, 0, 0], 8)
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', ihdr),
    chunk('IDAT', deflateSync(filas)),
    chunk('IEND', Buffer.alloc(0)),
  ])
}

const dir = new URL('../app/public/icons/', import.meta.url)
mkdirSync(dir, { recursive: true })
for (const lado of [192, 512]) writeFileSync(new URL(`icon-${lado}.png`, dir), png(lado))
console.log('Íconos generados en app/public/icons/')
