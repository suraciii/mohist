import { useMemo } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { ActivityIcon, AlertCircleIcon, CheckCircle2Icon, ClockIcon, PauseCircleIcon, XCircleIcon } from 'lucide-react'
import { Button } from '@/shared/ui/components/button'
import {
  approveIssue,
  rejectIssue,
  retryIssue,
  resumeIssue,
  rerunIssue,
  startIssue,
  stopIssue,
  type Issue,
  type WorkflowTimeline,
} from '../../../entities/issue'
import type { AgentStatus } from '../../../entities/agent'
import { useProject } from '../../../entities/project'
import { cn } from '@/shared/lib/utils'
import {
  deriveRuntimeDecision,
  type RuntimeAvailableAction,
  type RuntimeDecision,
  type RuntimeSummary,
} from '../model/derive-runtime-decision'

export interface RuntimeDecisionSurfaceProps {
  issue: Issue | null | undefined
  timeline?: WorkflowTimeline | null
  agentStatus?: AgentStatus | null
  hasActiveAgent?: boolean
}

interface SummaryPresentation {
  label: string
  tone: 'blue' | 'amber' | 'red' | 'orange' | 'green' | 'gray' | 'violet'
  icon: typeof ActivityIcon
  testId: string
}

const SUMMARY_PRESENTATION: Record<RuntimeSummary, SummaryPresentation> = {
  'running': {
    label: 'Running',
    tone: 'blue',
    icon: ActivityIcon,
    testId: 'runtime-summary-running',
  },
  'queued': {
    label: 'Queued',
    tone: 'violet',
    icon: ClockIcon,
    testId: 'runtime-summary-queued',
  },
  'approval-required': {
    label: 'Approval required',
    tone: 'amber',
    icon: PauseCircleIcon,
    testId: 'runtime-summary-approval-required',
  },
  'blocked': {
    label: 'Blocked',
    tone: 'orange',
    icon: AlertCircleIcon,
    testId: 'runtime-summary-blocked',
  },
  'failed': {
    label: 'Failed',
    tone: 'red',
    icon: XCircleIcon,
    testId: 'runtime-summary-failed',
  },
  'done': {
    label: 'Done',
    tone: 'green',
    icon: CheckCircle2Icon,
    testId: 'runtime-summary-done',
  },
}

const toneClass: Record<SummaryPresentation['tone'], string> = {
  blue: 'border-blue-200 bg-blue-50',
  amber: 'border-amber-200 bg-amber-50',
  red: 'border-red-200 bg-red-50',
  orange: 'border-orange-200 bg-orange-50',
  green: 'border-green-200 bg-green-50',
  gray: 'border-gray-200 bg-gray-50',
  violet: 'border-violet-200 bg-violet-50',
}

const toneTitleClass: Record<SummaryPresentation['tone'], string> = {
  blue: 'text-blue-900',
  amber: 'text-amber-900',
  red: 'text-red-900',
  orange: 'text-orange-900',
  green: 'text-green-900',
  gray: 'text-gray-900',
  violet: 'text-violet-900',
}

const toneBodyClass: Record<SummaryPresentation['tone'], string> = {
  blue: 'text-blue-800',
  amber: 'text-amber-800',
  red: 'text-red-800',
  orange: 'text-orange-800',
  green: 'text-green-800',
  gray: 'text-gray-800',
  violet: 'text-violet-800',
}

const toneIconClass: Record<SummaryPresentation['tone'], string> = {
  blue: 'text-blue-600',
  amber: 'text-amber-600',
  red: 'text-red-600',
  orange: 'text-orange-600',
  green: 'text-green-600',
  gray: 'text-gray-600',
  violet: 'text-violet-600',
}

function CurrentTaskPill({
  task,
}: {
  task: RuntimeDecision['currentTask']
}) {
  if (!task) return null
  return (
    <span
      data-testid="runtime-current-task"
      data-task-kind={task.kind}
      className="inline-flex items-center gap-1.5 rounded-full bg-white/70 border border-current/20 px-2.5 py-1 text-xs font-medium"
    >
      <span className="text-[10px] uppercase tracking-wide opacity-70">
        {task.kind === 'check' ? 'Check' : 'Task'}
      </span>
      <span className="font-semibold">{task.title}</span>
      {task.status && (
        <span
          data-testid="runtime-current-task-status"
          className="text-[10px] uppercase tracking-wide opacity-80"
        >
          · {task.status}
        </span>
      )}
    </span>
  )
}

function SurfaceActionButton({
  action,
  onApprove,
  onSendBack,
  onRetry,
  onResume,
  onRerun,
  onStop,
  onStart,
  pendingKind,
}: {
  action: RuntimeAvailableAction
  onApprove: () => void
  onSendBack: () => void
  onRetry: () => void
  onResume: () => void
  onRerun: () => void
  onStop: () => void
  onStart: () => void
  pendingKind: RuntimeAvailableAction['kind'] | null
}) {
  const isPending = pendingKind === action.kind
  const variant = action.kind === 'approve'
    ? 'default'
    : action.kind === 'send-back' || action.kind === 'stop'
      ? 'destructive'
      : 'outline'

  let onClick: () => void = () => undefined
  let busyLabel = ''
  if (action.kind === 'approve') {
    onClick = onApprove
    busyLabel = 'Approving...'
  } else if (action.kind === 'send-back') {
    onClick = onSendBack
    busyLabel = 'Sending back...'
  } else if (action.kind === 'retry') {
    onClick = onRetry
    busyLabel = 'Retrying...'
  } else if (action.kind === 'resume') {
    onClick = onResume
    busyLabel = 'Resuming...'
  } else if (action.kind === 'rerun') {
    onClick = onRerun
    busyLabel = 'Rerunning...'
  } else if (action.kind === 'stop') {
    onClick = onStop
    busyLabel = 'Stopping...'
  } else if (action.kind === 'start') {
    onClick = onStart
    busyLabel = 'Starting...'
  }

  return (
    <Button
      data-testid={`runtime-action-${action.kind}`}
      variant={variant}
      size="sm"
      onClick={onClick}
      disabled={!action.enabled || isPending}
      title={action.reason ?? undefined}
      className="min-w-[7rem]"
    >
      {isPending ? busyLabel : action.label}
    </Button>
  )
}

export function RuntimeDecisionSurface({
  issue,
  timeline,
  agentStatus,
  hasActiveAgent,
}: RuntimeDecisionSurfaceProps) {
  const { projectId } = useProject()
  const queryClient = useQueryClient()

  const decision = useMemo(
    () =>
      deriveRuntimeDecision({
        issue: issue
          ? {
              status: issue.status,
              workflowStage: issue.workflowStage ?? null,
              workflowStatus: issue.workflowStatus ?? null,
              health: issue.health,
              approvalState: issue.approvalState ?? undefined,
              blockedReason: issue.blockedReason ?? undefined,
              recovery: issue.recovery ?? undefined,
              convergence: issue.convergence ?? undefined,
              drift: issue.drift ?? undefined,
              workflowStageProgress: issue.workflowStageProgress ?? undefined,
              prerequisites: issue.prerequisites ?? [],
              isDraft: issue.isDraft,
              canStart: issue.canStart,
              blocker: issue.blocker,
            }
          : null,
        timeline: timeline
          ? {
              currentStage: timeline.currentStage,
              status: timeline.status,
              stages: timeline.stages,
              pendingWork: timeline.pendingWork,
              availableActions: timeline.availableActions,
            }
          : null,
        agentStatus: agentStatus ?? null,
        issueNumber: issue?.number,
        hasActiveAgent,
      }),
    [issue, timeline, agentStatus, hasActiveAgent],
  )

  const invalidateAll = () => {
    queryClient.invalidateQueries({ queryKey: ['issues'] })
    if (issue?.number != null) {
      queryClient.invalidateQueries({ queryKey: ['issues', issue.number] })
    }
    queryClient.invalidateQueries({ queryKey: ['agent-status'] })
    queryClient.invalidateQueries({ queryKey: ['agent-activity'] })
  }

  const approveMutation = useMutation({
    mutationFn: () => approveIssue(issue!.number, projectId),
    onSuccess: invalidateAll,
  })

  const sendBackMutation = useMutation({
    mutationFn: () => rejectIssue(issue!.number, {}, projectId),
    onSuccess: invalidateAll,
  })

  const retryMutation = useMutation({
    mutationFn: () => retryIssue(issue!.number, projectId),
    onSuccess: invalidateAll,
  })

  const resumeMutation = useMutation({
    mutationFn: () => resumeIssue(issue!.number, projectId),
    onSuccess: invalidateAll,
  })

  const rerunMutation = useMutation({
    mutationFn: () => rerunIssue(issue!.number, projectId),
    onSuccess: invalidateAll,
  })

  const stopMutation = useMutation({
    mutationFn: () => stopIssue(issue!.number, projectId),
    onSuccess: invalidateAll,
  })

  const startMutation = useMutation({
    mutationFn: () => startIssue(issue!.number, projectId),
    onSuccess: invalidateAll,
  })

  if (!issue) return null

  const presentation = SUMMARY_PRESENTATION[decision.summary]
  const Icon = presentation.icon
  const pendingKind: RuntimeAvailableAction['kind'] | null =
    approveMutation.isPending ? 'approve'
    : sendBackMutation.isPending ? 'send-back'
    : retryMutation.isPending ? 'retry'
    : resumeMutation.isPending ? 'resume'
    : rerunMutation.isPending ? 'rerun'
    : stopMutation.isPending ? 'stop'
    : startMutation.isPending ? 'start'
    : null

  return (
    <section
      data-testid="runtime-decision-surface"
      data-summary={decision.summary}
      role="region"
      aria-label="Issue runtime decision"
      className={cn(
        'rounded-lg border p-4 shadow-sm',
        toneClass[presentation.tone],
      )}
    >
      <header className="flex flex-wrap items-start gap-3">
        <div className="flex items-center gap-2">
          <Icon
            className={cn('h-4 w-4', toneIconClass[presentation.tone])}
            aria-hidden="true"
            data-testid={presentation.testId}
          />
          <span
            data-testid="runtime-summary-label"
            className={cn('text-xs font-bold uppercase tracking-wide', toneTitleClass[presentation.tone])}
          >
            {presentation.label}
          </span>
        </div>
        <h2
          data-testid="runtime-headline"
          className={cn('flex-1 min-w-0 text-base font-semibold leading-tight', toneTitleClass[presentation.tone])}
        >
          {decision.headline}
        </h2>
        <CurrentTaskPill task={decision.currentTask} />
      </header>

      <p
        data-testid="runtime-rationale"
        className={cn('mt-2 text-sm', toneBodyClass[presentation.tone])}
      >
        {decision.rationale}
      </p>

      <div className="mt-3 flex flex-wrap items-center gap-3">
        <span
          data-testid="runtime-next-action"
          className={cn(
            'text-xs font-medium uppercase tracking-wide',
            toneTitleClass[presentation.tone],
          )}
        >
          Next action
        </span>
        <span
          data-testid="runtime-next-action-body"
          className={cn('text-sm', toneBodyClass[presentation.tone])}
        >
          {decision.nextAction}
        </span>
      </div>

      {decision.summary === 'queued' && decision.waitReason && (
        <p
          data-testid="runtime-wait-reason"
          className={cn('mt-2 text-xs', toneBodyClass[presentation.tone])}
        >
          Waiting on: {decision.waitReason}
        </p>
      )}

      {decision.driftNote && (
        <p
          data-testid="runtime-drift-note"
          className={cn('mt-2 text-xs italic', toneBodyClass[presentation.tone])}
        >
          Drift: {decision.driftNote}
        </p>
      )}

      {decision.actions.length > 0 && (
        <div
          data-testid="runtime-actions"
          className="mt-4 flex flex-wrap gap-2"
        >
          {decision.actions.map((action) => (
            <SurfaceActionButton
              key={action.kind}
              action={action}
              onApprove={() => approveMutation.mutate()}
              onSendBack={() => sendBackMutation.mutate()}
              onRetry={() => retryMutation.mutate()}
              onResume={() => resumeMutation.mutate()}
              onRerun={() => rerunMutation.mutate()}
              onStop={() => stopMutation.mutate()}
              onStart={() => startMutation.mutate()}
              pendingKind={pendingKind}
            />
          ))}
        </div>
      )}

      {(approveMutation.error
        || sendBackMutation.error
        || retryMutation.error
        || resumeMutation.error
        || rerunMutation.error
        || stopMutation.error
        || startMutation.error) && (
        <div
          data-testid="runtime-action-error"
          className="mt-3 rounded-md border border-red-200 bg-red-50 px-3 py-2 text-xs text-red-700"
        >
          {approveMutation.error?.message
            || sendBackMutation.error?.message
            || retryMutation.error?.message
            || resumeMutation.error?.message
            || rerunMutation.error?.message
            || stopMutation.error?.message
            || startMutation.error?.message}
        </div>
      )}
    </section>
  )
}
