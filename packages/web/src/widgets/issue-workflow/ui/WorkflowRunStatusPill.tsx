import { ActivityIcon, CheckCircle2Icon, CircleDashedIcon, ClockIcon, PauseCircleIcon, PlayCircleIcon, XCircleIcon } from 'lucide-react'
import { cn } from '@/shared/lib/utils'
import { statusTreatment } from '@/shared/status-presentation'
import type { WorkflowRunStatus } from '../../../entities/issue'

export interface WorkflowRunStatusPillProps {
  status: string | null | undefined
  className?: string
}

interface StatusLabel {
  label: string
  icon: typeof ActivityIcon
  testId: string
}

const LABELS_BY_STATUS: Record<WorkflowRunStatus, StatusLabel> = {
  created: { label: 'Created', icon: ClockIcon, testId: 'workflow-run-status-created' },
  pending: { label: 'Pending runner', icon: CircleDashedIcon, testId: 'workflow-run-status-pending' },
  ready: { label: 'Ready to run', icon: PlayCircleIcon, testId: 'workflow-run-status-ready' },
  running: { label: 'Running', icon: ActivityIcon, testId: 'workflow-run-status-running' },
  'awaiting-approval': { label: 'Awaiting approval', icon: PauseCircleIcon, testId: 'workflow-run-status-awaiting-approval' },
  paused: { label: 'Paused', icon: PauseCircleIcon, testId: 'workflow-run-status-paused' },
  stopped: { label: 'Stopped', icon: XCircleIcon, testId: 'workflow-run-status-stopped' },
  completed: { label: 'Completed', icon: CheckCircle2Icon, testId: 'workflow-run-status-completed' },
  failed: { label: 'Failed', icon: XCircleIcon, testId: 'workflow-run-status-failed' },
}

const UNKNOWN_LABEL: StatusLabel = {
  label: 'Unknown',
  icon: ClockIcon,
  testId: 'workflow-run-status-unknown',
}

function isKnownRunStatus(value: string): value is WorkflowRunStatus {
  return value in LABELS_BY_STATUS
}

export function WorkflowRunStatusPill({ status, className }: WorkflowRunStatusPillProps) {
  if (!status) return null

  const known = isKnownRunStatus(status)
  const meta = known ? LABELS_BY_STATUS[status] : UNKNOWN_LABEL
  const dataStatus = known ? status : 'unknown'
  const Icon = meta.icon
  const treatment = statusTreatment('workflow-run', dataStatus)

  return (
    <span
      data-testid={meta.testId}
      data-status={dataStatus}
      data-family={treatment.family}
      className={cn(
        'inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-[10px] font-semibold',
        treatment.container,
        className,
      )}
      title={meta.label}
    >
      <Icon className="h-3 w-3" aria-hidden="true" />
      <span className={cn('inline-block h-1.5 w-1.5 rounded-full', treatment.dot)} aria-hidden="true" />
      {meta.label}
    </span>
  )
}