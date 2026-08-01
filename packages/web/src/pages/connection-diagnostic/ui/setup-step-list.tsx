import { CheckIcon, CircleDotIcon, Loader2Icon } from 'lucide-react'
import { cn } from '@/shared/lib/utils'

export type SetupStepKey =
  | 'create_app_credentials'
  | 'waiting_for_slack_service'
  | 'fix_slack_setup'
  | 'claim_owner'
  | 'complete'

export interface SetupStepDefinition {
  key: SetupStepKey
  label: string
}

export const SETUP_STEPS: readonly SetupStepDefinition[] = [
  { key: 'create_app_credentials', label: 'Create app & add credentials' },
  { key: 'waiting_for_slack_service', label: 'Waiting for Slack service' },
  { key: 'fix_slack_setup', label: 'Fix Slack setup' },
  { key: 'claim_owner', label: 'Claim owner' },
  { key: 'complete', label: 'Complete' },
] as const

interface SetupStepListProps {
  setupProgress: SetupStepKey | string | null | undefined
  className?: string
}

export type SetupStepState = 'done' | 'current' | 'pending'

function getStepIndex(progress: string | null | undefined): number {
  if (!progress) return -1
  const index = SETUP_STEPS.findIndex((step) => step.key === progress)
  return index
}

function resolveStates(setupProgress: string | null | undefined): SetupStepState[] {
  const explicitIndex = getStepIndex(setupProgress)
  return SETUP_STEPS.map((_, index) => {
    if (explicitIndex < 0) {
      return index === 0 ? 'current' : 'pending'
    }
    if (index < explicitIndex) return 'done'
    if (index === explicitIndex) return 'current'
    return 'pending'
  })
}

function StepIcon({ state }: { state: SetupStepState }) {
  if (state === 'done') {
    return <CheckIcon className="h-3.5 w-3.5 text-green-600" />
  }
  if (state === 'current') {
    return <Loader2Icon className="h-3.5 w-3.5 animate-spin text-amber-600" />
  }
  return <CircleDotIcon className="h-3.5 w-3.5 text-muted-foreground/50" />
}

export function SetupStepList({ setupProgress, className }: SetupStepListProps) {
  const states = resolveStates(setupProgress)
  return (
    <ol
      data-testid="connection-setup-step-list"
      data-setup-progress={setupProgress ?? 'unknown'}
      className={cn('flex flex-wrap items-center gap-x-3 gap-y-1 text-xs', className)}
    >
      {SETUP_STEPS.map((step, index) => (
        <li
          key={step.key}
          data-testid={`connection-setup-step-${step.key}`}
          data-state={states[index]}
          className={cn(
            'inline-flex items-center gap-1.5 rounded-md border px-2 py-1',
            states[index] === 'done' && 'border-green-200 bg-green-50 text-green-700',
            states[index] === 'current' && 'border-amber-200 bg-amber-50 text-amber-700',
            states[index] === 'pending' && 'border-border bg-muted/30 text-muted-foreground',
          )}
        >
          <StepIcon state={states[index]} />
          <span>{step.label}</span>
        </li>
      ))}
    </ol>
  )
}
