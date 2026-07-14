import type { UseMutationResult } from '@tanstack/react-query'

interface RuntimeActionMutation<TVariables = void> {
  mutate: UseMutationResult<unknown, Error, TVariables, unknown>['mutate']
  isPending: boolean
  error: Error | null
}

export interface RuntimeDecisionSurfaceMutations {
  approveMutation: RuntimeActionMutation
  sendBackMutation: RuntimeActionMutation<{ stage: string; body: string }>
  retryMutation: RuntimeActionMutation
  resumeMutation: RuntimeActionMutation
  rerunMutation: RuntimeActionMutation
  forceStopMutation: RuntimeActionMutation
  stopMutation: RuntimeActionMutation
  startMutation: RuntimeActionMutation
}
