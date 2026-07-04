import {
  ActivityIcon,
  AlertCircleIcon,
  CheckCircle2Icon,
  ClockIcon,
  PauseCircleIcon,
  XCircleIcon,
} from 'lucide-react'
import type { ComponentType, SVGProps } from 'react'
import { cn } from '@/shared/lib/utils'
import type { RuntimeDecision, RuntimeSummary } from '../../../widgets/issue-workflow/model/derive-runtime-decision'

export interface StatusHeadlineStageProgress {
  stage?: string | null
  total?: number | null
  completed?: number | null
}

export interface StatusHeadlineProps {
  decision: RuntimeDecision
  stageProgress: StatusHeadlineStageProgress | null | undefined
}

type IconComponent = ComponentType<SVGProps<SVGSVGElement> & { className?: string }>

interface SummaryPresentation {
  label: string
  fillClassName: string
  borderClassName: string
  iconClassName: string
  icon: IconComponent
}

const SUMMARY_PRESENTATION: Record<RuntimeSummary, SummaryPresentation> = {
  'running': {
    label: 'Running',
    fillClassName: 'bg-info-subtle',
    borderClassName: 'border-info-border',
    iconClassName: 'text-info',
    icon: ActivityIcon,
  },
  'queued': {
    label: 'Queued',
    fillClassName: 'bg-info-subtle',
    borderClassName: 'border-info-border',
    iconClassName: 'text-info',
    icon: ClockIcon,
  },
  'approval-required': {
    label: 'Approval required',
    fillClassName: 'bg-warning-subtle',
    borderClassName: 'border-warning-border',
    iconClassName: 'text-warning',
    icon: PauseCircleIcon,
  },
  'blocked': {
    label: 'Blocked',
    fillClassName: 'bg-warning-subtle',
    borderClassName: 'border-warning-border',
    iconClassName: 'text-warning',
    icon: AlertCircleIcon,
  },
  'failed': {
    label: 'Failed',
    fillClassName: 'bg-danger-subtle',
    borderClassName: 'border-danger-border',
    iconClassName: 'text-danger',
    icon: XCircleIcon,
  },
  'done': {
    label: 'Done',
    fillClassName: 'bg-success-subtle',
    borderClassName: 'border-success-border',
    iconClassName: 'text-success',
    icon: CheckCircle2Icon,
  },
}

function StageProgress({
  stageProgress,
}: {
  stageProgress: StatusHeadlineStageProgress
}) {
  const stage = stageProgress.stage ?? null
  const total = stageProgress.total ?? 0
  const completed = stageProgress.completed ?? 0
  if (!stage || total <= 0) return null
  return (
    <span
      data-testid="status-headline-stage-progress"
      data-stage={stage}
      className="inline-flex items-center gap-1 text-xs font-medium tabular-nums"
    >
      <span className="font-semibold">{stage}</span>
      <span aria-hidden="true">·</span>
      <span>{completed}/{total}</span>
    </span>
  )
}

export function StatusHeadline({ decision, stageProgress }: StatusHeadlineProps) {
  const presentation = SUMMARY_PRESENTATION[decision.summary]
  const Icon = presentation.icon
  const currentTaskTitle = decision.currentTask?.title ?? null
  const hasStageProgress = !!(stageProgress?.stage && (stageProgress.total ?? 0) > 0)

  return (
    <section
      data-testid="status-headline"
      data-summary={decision.summary}
      data-sticky="true"
      aria-label="Issue status headline"
      className={cn(
        'sticky top-0 z-20 flex flex-wrap items-center gap-x-4 gap-y-2 rounded-lg border border-b px-4 py-3 shadow-sm',
        presentation.fillClassName,
        presentation.borderClassName,
      )}
    >
      <div className="flex items-center gap-2">
        <Icon
          className={cn('h-4 w-4', presentation.iconClassName)}
          aria-hidden="true"
          data-testid={`status-headline-icon-${decision.summary}`}
        />
        <span
          data-testid="status-headline-summary"
          className={cn(
            'inline-flex items-center rounded-full border px-2 py-0.5 text-[10px] font-bold uppercase tracking-wide',
            presentation.borderClassName,
          )}
        >
          {presentation.label}
        </span>
      </div>

      {decision.headline && (
        <h2
          data-testid="status-headline-headline"
          className="text-sm font-semibold leading-tight text-card-foreground"
        >
          {decision.headline}
        </h2>
      )}

      {hasStageProgress && stageProgress && <StageProgress stageProgress={stageProgress} />}

      {currentTaskTitle && (
        <span
          data-testid="status-headline-current-task"
          data-task-kind={decision.currentTask?.kind ?? 'task'}
          className="inline-flex items-center gap-1.5 rounded-full border border-current/20 bg-card/70 px-2.5 py-1 text-xs font-medium text-card-foreground"
        >
          <span className="text-[10px] uppercase tracking-wide opacity-70">
            {decision.currentTask?.kind === 'check' ? 'Check' : 'Task'}
          </span>
          <span className="font-semibold">{currentTaskTitle}</span>
          {decision.currentTask?.status && (
            <span className="text-[10px] uppercase tracking-wide opacity-80">
              · {decision.currentTask.status}
            </span>
          )}
        </span>
      )}
    </section>
  )
}