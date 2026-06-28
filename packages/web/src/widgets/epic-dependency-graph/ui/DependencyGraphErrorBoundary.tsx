import { Component, type ErrorInfo, type ReactNode } from 'react'

export interface DependencyGraphErrorBoundaryProps {
  children: ReactNode
  onError?: () => void
}

interface DependencyGraphErrorBoundaryState {
  hasError: boolean
}

export class DependencyGraphErrorBoundary extends Component<
  DependencyGraphErrorBoundaryProps,
  DependencyGraphErrorBoundaryState
> {
  state: DependencyGraphErrorBoundaryState = { hasError: false }

  static getDerivedStateFromError(_error: Error): DependencyGraphErrorBoundaryState {
    return { hasError: true }
  }

  componentDidCatch(error: Error, info: ErrorInfo): void {
    if (typeof console !== 'undefined' && console.error) {
      console.error('DependencyGraphWidget render error', error, info.componentStack)
    }
    this.props.onError?.()
  }

  render(): ReactNode {
    if (this.state.hasError) {
      return null
    }
    return this.props.children
  }
}