// Radar del resumen con Chart.js. Este módulo se carga con import() solo al abrir el resumen:
// así Chart.js no entra en el bundle inicial de la PWA. Imports con nombre para que el tree-shaking deje solo esto.
import { Chart, Filler, LineElement, PointElement, RadarController, RadialLinearScale, Tooltip } from 'chart.js'
import { textos } from './i18n'
import { TOPE_VISUAL_PCT } from './resumenFinal'

Chart.register(RadarController, RadialLinearScale, PointElement, LineElement, Filler, Tooltip)

const t = textos(
  {
    promedio: 'Promedio del último mes',
    hoy: 'Hoy',
    promedioMes: 'Promedio del mes: 100%',
    nuevo: 'Hoy: nuevo',
    delPromedio: (n: number) => `Hoy: ${n}% del promedio`,
  },
  {
    promedio: 'Average for the last month',
    hoy: 'Today',
    promedioMes: 'Monthly average: 100%',
    nuevo: 'Today: new',
    delPromedio: (n) => `Today: ${n}% of average`,
  },
)

export interface DatosRadar {
  etiquetas: string[]
  /** % del promedio sin recortar; null = 'nuevo'. */
  pct: (number | null)[]
}

const AZUL = '#58a6ff'
const GRIS = '#8b949e'

/** Dibuja y devuelve el gráfico; quien lo crea llama destroy() al cerrar. */
export function dibujarRadar(canvas: HTMLCanvasElement, d: DatosRadar): Chart {
  return new Chart(canvas, {
    type: 'radar',
    data: {
      labels: d.etiquetas,
      datasets: [
        {
          // Promedio = 100% en cada eje con historial; 'nuevo' sin punto gris.
          label: t().promedio,
          data: d.pct.map((p): number | null => (p === null ? null : 100)),
          borderColor: GRIS,
          backgroundColor: 'transparent',
          pointBackgroundColor: GRIS,
          pointRadius: 2,
          borderWidth: 1.5,
          spanGaps: true,
          // Mayor order = se dibuja antes: el gris queda debajo del azul.
          order: 2,
        },
        {
          label: t().hoy,
          // Tope solo visual; el valor real va en el tooltip.
          data: d.pct.map((p) => (p === null ? 100 : Math.min(p, TOPE_VISUAL_PCT))),
          borderColor: AZUL,
          backgroundColor: 'rgba(88, 166, 255, 0.25)',
          pointBackgroundColor: AZUL,
          pointRadius: 3,
          borderWidth: 2,
          fill: true,
          order: 1,
        },
      ],
    },
    options: {
      animation: false,
      responsive: true,
      maintainAspectRatio: false,
      scales: {
        r: {
          min: 0,
          max: TOPE_VISUAL_PCT,
          ticks: { display: false, stepSize: 50 },
          grid: { color: '#2d333b' },
          angleLines: { color: '#2d333b' },
          pointLabels: { color: '#e6edf3', font: { size: 11 } },
        },
      },
      plugins: {
        tooltip: {
          callbacks: {
            label: (c) => {
              const real = d.pct[c.dataIndex]
              if (c.datasetIndex === 0) return t().promedioMes
              return real === null ? t().nuevo : t().delPromedio(Math.round(real))
            },
          },
        },
      },
    },
  })
}
