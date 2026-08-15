import { ActivityIcon, CheckCircle2Icon, CircleDashedIcon, ClockIcon, HourglassIcon, PauseCircleIcon, PlayCircleIcon, XCircleIcon } from 'lucide-react'
import { cn } from '@/shared/lib/utils'
import type { WorkflowRunStatus } from '../../../entities/issue'

export interface WorkflowRunStatusPillProps {
  status: string | null | undefined
  className?: string
}

interface StatusPresentation {
  label: string
  bg: string
  text: string
  dot: string
  icon: typeof ActivityIcon
  testId: string
}

const PENDING_PRESENTATION: StatusPresentation = {
  label: 'Pending runner',
  bg: 'bg-violet-100',
  text: 'text-violet-800',
  dot: 'bg-violet-500',
  icon: CircleDashedIcon,
  testId: 'workflow-run-status-pending',
}

const READY_PRESENTATION: StatusPresentation = {
  label: 'Ready to run',
  bg: 'bg-cyan-100',
  text: 'text-cyan-800',
  dot: 'bg-cyan-500',
  icon: PlayCircleIcon,
  testId: 'workflow-run-status-ready',
}

const RUNNING_PRESENTATION: StatusPresentation = {
  label: 'Running',
  bg: 'bg-blue-100',
  text: 'text-blue-800',
  dot: 'bg-blue-500',
  icon: ActivityIcon,
  testId: 'workflow-run-status-running',
}

const RECOVERABLE_INTERRUPTED_PRESENTATION: StatusPresentation = {
  label: 'Recoverable interruption',
  bg: 'bg-amber-100',
  text: 'text-amber-800',
  dot: 'bg-amber-500',
  icon: HourglassIcon,
  testId: 'workflow-run-status-recoverable-interrupted',
}

const AWAITING_APPROVAL_PRESENTATION: StatusPresentation = {
  label: 'Awaiting approval',
  bg: 'bg-amber-100',
  text: 'text-amber-800',
  dot: 'bg-amber-500',
  icon: PauseCircleIcon,
  testId: 'workflow-run-status-awaiting-approval',
}

const PAUSED_PRESENTATION: StatusPresentation = {
  label: 'Paused',
  bg: 'bg-slate-100',
  text: 'text-slate-700',
  dot: 'bg-slate-500',
  icon: PauseCircleIcon,
  testId: 'workflow-run-status-paused',
}

const COMPLETED_PRESENTATION: StatusPresentation = {
  label: 'Completed',
  bg: 'bg-emerald-100',
  text: 'text-emerald-800',
  dot: 'bg-emerald-500',
  icon: CheckCircle2Icon,
  testId: 'workflow-run-status-completed',
}

const STOPPED_PRESENTATION: StatusPresentation = {
  label: 'Stopped',
  bg: 'bg-slate-100',
  text: 'text-slate-700',
  dot: 'bg-slate-500',
  icon: XCircleIcon,
  testId: 'workflow-run-status-stopped',
}

const FAILED_PRESENTATION: StatusPresentation = {
  label: 'Failed',
  bg: 'bg-red-100',
  text: 'text-red-800',
  dot: 'bg-red-500',
  icon: XCircleIcon,
  testId: 'workflow-run-status-failed',
}

const BLOCKED_PRESENTATION: StatusPresentation = {
  label: 'Blocked — agent result unconfirmed',
  bg: 'bg-amber-100',
  text: 'text-amber-800',
  dot: 'bg-amber-500',
  icon: HourglassIcon,
  testId: 'workflow-run-status-blocked',
}

const CREATED_PRESENTATION: StatusPresentation = {
  label: 'Created',
  bg: 'bg-gray-100',
  text: 'text-gray-700',
  dot: 'bg-gray-500',
  icon: ClockIcon,
  testId: 'workflow-run-status-created',
}

const UNKNOWN_PRESENTATION: StatusPresentation = {
  label: 'Unknown',
  bg: 'bg-gray-100',
  text: 'text-gray-700',
  dot: 'bg-gray-500',
  icon: ClockIcon,
  testId: 'workflow-run-status-unknown',
}

const PRESENTATION_BY_STATUS: Record<WorkflowRunStatus, StatusPresentation> = {
  created: CREATED_PRESENTATION,
  pending: PENDING_PRESENTATION,
  ready: READY_PRESENTATION,
  running: RUNNING_PRESENTATION,
  'recoverable-interrupted': RECOVERABLE_INTERRUPTED_PRESENTATION,
  'awaiting-approval': AWAITING_APPROVAL_PRESENTATION,
  paused: PAUSED_PRESENTATION,
  stopped: STOPPED_PRESENTATION,
  completed: COMPLETED_PRESENTATION,
  failed: FAILED_PRESENTATION,
  blocked: BLOCKED_PRESENTATION,
}

function isKnownRunStatus(value: string): value is WorkflowRunStatus {
  return value in PRESENTATION_BY_STATUS
}

export function WorkflowRunStatusPill({ status, className }: WorkflowRunStatusPillProps) {
  if (!status) return null

  const presentation = status && isKnownRunStatus(status)
    ? PRESENTATION_BY_STATUS[status]
    : UNKNOWN_PRESENTATION
  const Icon = presentation.icon
  const dataStatus = status && isKnownRunStatus(status) ? status : 'unknown'

  return (
    <span
      data-testid={presentation.testId}
      data-status={dataStatus}
      className={cn(
        'inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-[10px] font-semibold',
        presentation.bg,
        presentation.text,
        className,
      )}
      title={presentation.label}
    >
      <Icon className="h-3 w-3" aria-hidden="true" />
      <span className="inline-block h-1.5 w-1.5 rounded-full" style={{ backgroundColor: 'currentColor' }} />
      {presentation.label}
    </span>
  )
}
