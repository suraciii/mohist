import { lazy } from 'react'

export const DependencyGraphWidget = lazy(async () => {
  const mod = await import('./ui/DependencyGraphWidget')
  return { default: mod.DependencyGraphWidget }
})

export { DependencyGraphErrorBoundary } from './ui/DependencyGraphErrorBoundary'
export type { DependencyGraphErrorBoundaryProps } from './ui/DependencyGraphErrorBoundary'

export type { DependencyGraphWidgetProps } from './ui/DependencyGraphWidget'
export type { Renderability } from './ui/DependencyGraphCanvas'
