import { CheckCircle2Icon, AlertTriangleIcon, XCircleIcon, InfoIcon } from 'lucide-react'
import { cn } from '@/shared/lib/utils'
import { getOutcomeCapabilityMessage, getOutcomeLabel, isSupersededStatus } from '../model/updateOutcome'
import type { SystemUpdateStatus } from '../model/types'

interface SystemUpdateOutcomeViewProps {
  job: SystemUpdateStatus
  className?: string
}

export function SystemUpdateOutcomeView({ job, className }: SystemUpdateOutcomeViewProps) {
  if (isSupersededStatus(job.status)) {
    return (
      <div
        data-testid="system-update-superseded"
        className={cn(
          'rounded-md border border-border bg-muted/40 px-3 py-2 text-xs text-muted-foreground',
          className,
        )}
      >
        <div className="flex items-center gap-1.5 font-medium text-foreground/80">
          <InfoIcon className="h-3.5 w-3.5" />
          Previous update is no longer relevant
        </div>
        <p className="mt-1">
          The last persisted update job belongs to an earlier runtime and is superseded by the current server state.
        </p>
        {job.reason && <p className="mt-1 italic">{job.reason}</p>}
      </div>
    )
  }

  const label = getOutcomeLabel(job.outcome ?? null)
  if (!label) {
    return null
  }

  const detail = getOutcomeCapabilityMessage(job)
  const toneClass = job.outcome === 'succeeded'
    ? 'border-green-200 bg-green-50 text-green-700'
    : job.outcome === 'recovered'
      ? 'border-amber-200 bg-amber-50 text-amber-700'
      : job.outcome === 'cancelled'
        ? 'border-blue-200 bg-blue-50 text-blue-700'
        : 'border-red-200 bg-red-50 text-red-700'
  const Icon = job.outcome === 'succeeded'
    ? CheckCircle2Icon
    : job.outcome === 'recovered'
      ? AlertTriangleIcon
      : job.outcome === 'cancelled'
        ? InfoIcon
        : XCircleIcon

  return (
    <div
      data-testid="system-update-outcome"
      data-outcome={job.outcome}
      className={cn('rounded-md border px-3 py-2 text-xs', toneClass, className)}
    >
      <div className="flex items-center gap-1.5 font-medium">
        <Icon className="h-3.5 w-3.5" />
        {label}
      </div>
      {detail && <p className="mt-1">{detail}</p>}
    </div>
  )
}
