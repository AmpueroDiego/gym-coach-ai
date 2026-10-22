import { Component, type ReactNode } from 'react'
import { textos } from './i18n'

const t = textos(
  { fallo: 'Algo falló al mostrar la app.', recargar: 'Recargar' },
  { fallo: 'Something went wrong showing the app.', recargar: 'Reload' },
)

// Sin esto, un dato raro en localStorage que reviente un render deja la pantalla en blanco.
export default class ErrorBoundary extends Component<{ children: ReactNode }, { fallo: boolean }> {
  state = { fallo: false }

  static getDerivedStateFromError() {
    return { fallo: true }
  }

  componentDidCatch(e: unknown) {
    console.error(e)
  }

  render() {
    if (!this.state.fallo) return this.props.children
    return (
      <div role="alert" style={{ padding: '2rem 1rem', textAlign: 'center' }}>
        <p>{t().fallo}</p>
        <button style={{ padding: '0.75rem 1.5rem', fontSize: '1rem' }} onClick={() => location.reload()}>
          {t().recargar}
        </button>
      </div>
    )
  }
}
