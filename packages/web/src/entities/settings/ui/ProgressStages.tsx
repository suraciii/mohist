import { CheckIcon, CircleDotIcon, Loader2Icon } from 'lucide-react'
import { cn } from '@/shared/lib/utils'
import { SYSTEM_UPDATE_STAGES } from '../model/types'
import { getActiveStageIndex, getStageIndex, isTerminalUpdateStatus, isActiveUpdateStatus } from '../model/updateOutcome'
import type { SystemUpdateStatus } from '../model/types'

interface ProgressStagesProps {
  job: SystemUpdateStatus | null
  className?: string
}

interface StageState {
  name: string
  state: 'done' | 'current' | 'pending' | 'superseded'
}

function deriveStageStates(job: SystemUpdateStatus | null): StageState[] {
  const superseded = job?.status === 'superseded'
  const status = job?.status
  const currentIndex = getActiveStageIndex(status, job?.stage)
  const explicitIndex = getStageIndex(job?.stage)

  return (SYSTEM_UPDATE_STAGES as readonly string[]).map((name, index) => {
    if (superseded) {
      return { name, state: 'superseded' as const }
    }
    if (isTerminalUpdateStatus(status) && currentIndex >= SYSTEM_UPDATE_STAGES.length - 1) {
      if (status === 'succeeded' || status === 'recovered') {
        return { name, state: index <= explicitIndex ? 'done' : 'pending' }
      }
      if (status === 'failed' || status === 'cancelled') {
        if (explicitIndex < 0) {
          return { name, state: 'pending' as const }
        }
        return { name, state: index < explicitIndex ? 'done' : index === explicitIndex ? 'current' : 'pending' }
      }
    }
    if (isActiveUpdateStatus(status)) {
      if (explicitIndex < 0) {
        return { name, state: index === 0 ? 'current' : 'pending' }
      }
      if (index < explicitIndex) return { name, state: 'done' as const }
      if (index === explicitIndex) return { name, state: 'current' as const }
      return { name, state: 'pending' as const }
    }
    return { name, state: 'pending' as const }
  })
}

function StageIcon({ state }: { state: StageState['state'] }) {
  if (state === 'done') {
    return <CheckIcon className="h-3.5 w-3.5 text-green-600" />
  }
  if (state === 'current') {
    return <Loader2Icon className="h-3.5 w-3.5 animate-spin text-amber-600" />
  }
  return <CircleDotIcon className="h-3.5 w-3.5 text-muted-foreground/50" />
}

export function ProgressStages({ job, className }: ProgressStagesProps) {
  const states = deriveStageStates(job)

  return (
    <ol
      data-testid="system-update-progress-stages"
      className={cn('flex flex-wrap items-center gap-x-3 gap-y-1 text-xs', className)}
    >
      {states.map((stage, index) => (
        <li
          key={stage.name}
          data-testid={`system-update-stage-${stage.name}`}
          data-state={stage.state}
          className={cn(
            'inline-flex items-center gap-1.5 rounded-md border px-2 py-1',
            stage.state === 'done' && 'border-green-200 bg-green-50 text-green-700',
            stage.state === 'current' && 'border-amber-200 bg-amber-50 text-amber-700',
            stage.state === 'pending' && 'border-border bg-muted/30 text-muted-foreground',
            stage.state === 'superseded' && 'border-border bg-muted/40 text-muted-foreground line-through',
          )}
        >
          <StageIcon state={stage.state} />
          <span>{stage.name}</span>
          {index < states.length - 1 && <span className="sr-only">next</span>}
        </li>
      ))}
    </ol>
  )
}
