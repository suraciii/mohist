import { Suspense, useCallback, useMemo, useState } from 'react'
import { useProjectPath } from '../../../entities/project'
import type { LinkedIssue } from '../../../entities/epic'
import { DependencyGraphCanvas, type Renderability } from './DependencyGraphCanvas'

export interface DependencyGraphWidgetProps {
  linkedIssues: LinkedIssue[]
  onRenderabilityChange?: (state: { renderable: boolean; reason: Renderability | null }) => void
}

export function DependencyGraphWidget({ linkedIssues, onRenderabilityChange }: DependencyGraphWidgetProps) {
  const toProjectPath = useProjectPath()
  const navigatePathFor = useCallback(
    (issueNumber: number) => toProjectPath(`/issues/${issueNumber}`),
    [toProjectPath],
  )

  const handleRenderabilityChange = useCallback(
    (state: { renderable: boolean; reason: Renderability | null }) => {
      onRenderabilityChange?.(state)
    },
    [onRenderabilityChange],
  )

  return (
    <Suspense fallback={<DependencyGraphSkeleton />}>
      <DependencyGraphCanvas
        linkedIssues={linkedIssues}
        navigatePathFor={navigatePathFor}
        onRenderabilityChange={handleRenderabilityChange}
      />
    </Suspense>
  )
}

export function DependencyGraphSkeleton() {
  return (
    <div
      data-testid="epic-dep-graph-skeleton"
      className="h-[560px] w-full min-w-[640px] rounded-lg border bg-muted/30 flex items-center justify-center text-sm text-muted-foreground"
    >
      Loading dependency graph…
    </div>
  )
}

export function useDependencyGraphRenderability(): {
  setState: (state: { renderable: boolean; reason: Renderability | null }) => void
  state: { renderable: boolean; reason: Renderability | null }
} {
  const [state, setState] = useState<{ renderable: boolean; reason: Renderability | null }>({
    renderable: false,
    reason: 'empty',
  })
  return useMemo(() => ({ setState, state }), [state])
}
