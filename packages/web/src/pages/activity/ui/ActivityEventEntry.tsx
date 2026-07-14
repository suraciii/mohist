import type { ReactNode } from 'react'
import { Link } from 'react-router-dom'
import { useProjectPath } from '../../../entities/project'
import type { ActivityEvent, ActivityEventTargets } from '../../../widgets/coder-session'

function formatTimeAgo(isoString: string, now: number): string {
  const timestamp = Date.parse(isoString)
  if (!Number.isFinite(timestamp)) return 'unknown'
  const diff = Math.max(0, now - timestamp)
  const minutes = Math.floor(diff / 60000)
  if (minutes < 1) return 'just now'
  if (minutes < 60) return `${minutes}m ago`
  const hours = Math.floor(minutes / 60)
  if (hours < 24) return `${hours}h ago`
  const days = Math.floor(hours / 24)
  return `${days}d ago`
}

const attentionPresentation = {
  failure: {
    surface: 'border-danger-border bg-danger-subtle',
    marker: 'bg-danger',
    tone: 'danger' as const,
  },
  approval: {
    surface: 'border-warning-border bg-warning-subtle',
    marker: 'bg-warning',
    tone: 'warning' as const,
  },
  blocked: {
    surface: 'border-warning-border bg-warning-subtle',
    marker: 'bg-warning',
    tone: 'warning' as const,
  },
  routine: {
    surface: 'border-border bg-background',
    marker: 'bg-muted-foreground/60',
    tone: 'neutral' as const,
  },
}

const typePresentation: Record<ActivityEvent['type'], { label: string; chip: string }> = {
  'issue-state': {
    label: 'Issue',
    chip: 'bg-info-subtle text-info border-info-border',
  },
  'workflow-stage': {
    label: 'Workflow',
    chip: 'bg-info-subtle text-info border-info-border',
  },
  'agent-session': {
    label: 'Session',
    chip: 'bg-muted text-muted-foreground border-border',
  },
  runner: {
    label: 'Runner',
    chip: 'bg-muted text-muted-foreground border-border',
  },
  failure: {
    label: 'Failure',
    chip: 'bg-danger-subtle text-danger border-danger-border',
  },
}

function SecondaryTargets({ targets }: { targets: ActivityEventTargets }) {
  const toProjectPath = useProjectPath()
  const chips: ReactNode[] = []

  if (targets.issue?.path && targets.issue.path !== targets.primary?.path) {
    chips.push(
      <Link
        key="issue"
        to={toProjectPath(targets.issue.path)}
        data-testid="activity-event-issue-link"
        className="inline-flex items-center rounded border border-border bg-muted px-1.5 py-0.5 text-[10px] font-medium text-muted-foreground hover:text-foreground"
      >
        {targets.issue.label}
      </Link>,
    )
  }

  if (targets.workflow?.path) {
    chips.push(
      <Link
        key="workflow"
        to={toProjectPath(targets.workflow.path)}
        data-testid="activity-event-workflow-link"
        className="inline-flex items-center rounded border border-border bg-muted px-1.5 py-0.5 text-[10px] font-medium text-muted-foreground hover:text-foreground"
      >
        {targets.workflow.label}
      </Link>,
    )
  }
  if (targets.session) {
    const path = targets.session.path
      ?? (targets.session.isGeneric
        ? `/agent-sessions/${encodeURIComponent(targets.session.sessionId)}?from=activity`
        : targets.issue
          ? `/issues/${targets.issue.number}/session/${encodeURIComponent(targets.session.sessionId)}?from=activity`
          : null)
    if (path) {
      chips.push(
        <Link
          key="session"
          to={toProjectPath(path)}
          data-testid="activity-event-session-link"
          className="inline-flex items-center rounded border border-border bg-muted px-1.5 py-0.5 text-[10px] font-medium text-muted-foreground hover:text-foreground"
        >
          {targets.session.label}
        </Link>,
      )
    }
  }
  if (targets.agent) {
    chips.push(
      <Link
        key="agent"
        to={toProjectPath(targets.agent.path ?? `/agents/${encodeURIComponent(targets.agent.agentId)}?from=activity`)}
        data-testid="activity-event-agent-link"
        className="inline-flex items-center rounded border border-border bg-muted px-1.5 py-0.5 text-[10px] font-medium text-muted-foreground hover:text-foreground"
      >
        {targets.agent.label}
      </Link>,
    )
  }
  if (targets.runner) {
    chips.push(
      <Link
        key="runner"
        to={toProjectPath(targets.runner.path ?? `/runners/${encodeURIComponent(targets.runner.runnerId)}?from=activity`)}
        data-testid="activity-event-runner-link"
        className="inline-flex items-center rounded border border-border bg-muted px-1.5 py-0.5 text-[10px] font-medium text-muted-foreground hover:text-foreground"
      >
        {targets.runner.label}
      </Link>,
    )
  }

  if (chips.length === 0) return null

  return <div className="flex flex-wrap items-center gap-1">{chips}</div>
}

interface ActivityEventEntryProps {
  event: ActivityEvent
  now: number
}

export function ActivityEventEntry({ event, now }: ActivityEventEntryProps) {
  const toProjectPath = useProjectPath()
  const attentionStyle = attentionPresentation[event.attention]
  const typeStyle = typePresentation[event.type]
  const primary = event.targets.primary

  return (
    <div
      data-testid="activity-event-entry"
      data-event-type={event.type}
      data-attention={event.attention}
      data-event-time={event.time}
      data-tone={attentionStyle.tone}
      className={`rounded-lg border p-3 shadow-sm transition-colors ${attentionStyle.surface}`}
    >
      <div className="flex items-start gap-3">
        <div className="flex shrink-0 flex-col items-center pt-0.5">
          <span className={`inline-block h-2.5 w-2.5 rounded-full ${attentionStyle.marker}`} />
        </div>
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <span
              className={`inline-flex items-center rounded-full border px-1.5 py-0.5 text-[10px] font-semibold ${typeStyle.chip}`}
            >
              {typeStyle.label}
            </span>
            {primary ? (
              <Link
                to={toProjectPath(primary.path)}
                data-testid="activity-event-primary-link"
                className="text-sm font-medium text-foreground hover:underline truncate"
              >
                {event.title}
              </Link>
            ) : (
              <span className="text-sm font-medium text-foreground truncate">{event.title}</span>
            )}
          </div>
          <p className="mt-0.5 text-xs text-muted-foreground">{event.description}</p>
          <div className="mt-1.5 flex flex-wrap items-center gap-2">
            <SecondaryTargets targets={event.targets} />
            <time
              dateTime={event.time}
              title={event.time}
              className="ml-auto text-[10px] tabular-nums text-muted-foreground"
            >
              {formatTimeAgo(event.time, now)}
            </time>
          </div>
        </div>
      </div>
    </div>
  )
}
