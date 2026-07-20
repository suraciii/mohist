import {
  ActivityIcon,
  AlertCircleIcon,
  CheckCircle2Icon,
  ClockIcon,
  FileTextIcon,
  PauseCircleIcon,
  XCircleIcon,
} from 'lucide-react'
import type { ComponentType, SVGProps } from 'react'
import { cn } from '@/shared/lib/utils'
import type { IssueOnlyStatusContext } from '../model/issueDecisionContext'
import { IssueHealth, IssueStatus } from '../../../entities/issue'

type IconComponent = ComponentType<SVGProps<SVGSVGElement> & { className?: string }>

interface IssueStatusPresentation {
  fillClassName: string
  borderClassName: string
  iconClassName: string
  icon: IconComponent
}

const ISSUE_STATUS_PRESENTATION: Record<IssueStatus, IssueStatusPresentation> = {
  [IssueStatus.Backlog]: {
    fillClassName: 'bg-muted',
    borderClassName: 'border-border',
    iconClassName: 'text-muted-foreground',
    icon: FileTextIcon,
  },
  [IssueStatus.InProgress]: {
    fillClassName: 'bg-info-subtle',
    borderClassName: 'border-info-border',
    iconClassName: 'text-info',
    icon: ActivityIcon,
  },
  [IssueStatus.Done]: {
    fillClassName: 'bg-success-subtle',
    borderClassName: 'border-success-border',
    iconClassName: 'text-success',
    icon: CheckCircle2Icon,
  },
  [IssueStatus.Cancelled]: {
    fillClassName: 'bg-muted',
    borderClassName: 'border-border',
    iconClassName: 'text-muted-foreground',
    icon: XCircleIcon,
  },
}

function presentationFor(
  status: IssueStatus,
  health: IssueHealth,
  isDraft: boolean,
  isArchived: boolean,
): IssueStatusPresentation {
  if (isArchived) {
    return {
      fillClassName: 'bg-muted',
      borderClassName: 'border-border',
      iconClassName: 'text-muted-foreground',
      icon: ClockIcon,
    }
  }
  if (isDraft) {
    return {
      fillClassName: 'bg-muted',
      borderClassName: 'border-border',
      iconClassName: 'text-muted-foreground',
      icon: FileTextIcon,
    }
  }
  if (status === IssueStatus.InProgress && health === IssueHealth.Blocked) {
    return {
      fillClassName: 'bg-warning-subtle',
      borderClassName: 'border-warning-border',
      iconClassName: 'text-warning',
      icon: AlertCircleIcon,
    }
  }
  if (status === IssueStatus.InProgress && health === IssueHealth.Paused) {
    return {
      fillClassName: 'bg-warning-subtle',
      borderClassName: 'border-warning-border',
      iconClassName: 'text-warning',
      icon: PauseCircleIcon,
    }
  }
  return ISSUE_STATUS_PRESENTATION[status]
}

export interface IssueOnlyStatusHeadlineProps {
  status: IssueStatus
  health: IssueHealth
  isDraft: boolean
  isArchived: boolean
  context: IssueOnlyStatusContext
}

export function IssueOnlyStatusHeadline({
  status,
  health,
  isDraft,
  isArchived,
  context,
}: IssueOnlyStatusHeadlineProps) {
  const presentation = presentationFor(status, health, isDraft, isArchived)
  const Icon = presentation.icon
  return (
    <section
      data-testid="status-headline"
      data-summary="issue-only"
      data-sticky="true"
      data-tier-weight="status-header"
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
          data-testid="status-headline-icon-issue-only"
        />
        <span
          data-testid="status-headline-summary"
          className={cn(
            'inline-flex items-center rounded-full border px-2 py-0.5 text-[10px] font-bold uppercase tracking-wide',
            presentation.borderClassName,
          )}
        >
          {context.label}
        </span>
      </div>

      <h2
        data-testid="status-headline-headline"
        className="text-sm font-semibold leading-tight text-card-foreground"
      >
        {context.headline}
      </h2>

      <span
        data-testid="status-headline-rationale"
        className="text-xs text-muted-foreground"
      >
        {context.rationale}
      </span>

      <span
        data-testid="status-headline-next-action"
        className="text-xs text-muted-foreground"
      >
        {context.nextAction}
      </span>
    </section>
  )
}
