import { useState } from 'react'
import type { UseMutationResult } from '@tanstack/react-query'
import { ActivityIcon, AlertCircleIcon, CheckCircle2Icon, ClockIcon, PauseCircleIcon, XCircleIcon } from 'lucide-react'
import { Button } from '@/shared/ui/components/button'
import { Textarea } from '@/shared/ui/components/textarea'
import { cn } from '@/shared/lib/utils'
import {
  type RuntimeAvailableAction,
  type RuntimeDecision,
  type RuntimeSummary,
} from '../model/derive-runtime-decision'

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

export interface RuntimeDecisionSurfaceProps {
  decision: RuntimeDecision
  mutations: RuntimeDecisionSurfaceMutations
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

const toneEdgeClass: Record<SummaryPresentation['tone'], string> = {
  blue: 'border-l-info',
  amber: 'border-l-warning',
  red: 'border-l-danger',
  orange: 'border-l-warning',
  green: 'border-l-success',
  gray: 'border-l-muted-foreground',
  violet: 'border-l-info',
}

const toneLabelClass: Record<SummaryPresentation['tone'], string> = {
  blue: 'bg-info-subtle text-info border-info-border',
  amber: 'bg-warning-subtle text-warning border-warning-border',
  red: 'bg-danger-subtle text-danger border-danger-border',
  orange: 'bg-warning-subtle text-warning border-warning-border',
  green: 'bg-success-subtle text-success border-success-border',
  gray: 'bg-muted text-muted-foreground border-border',
  violet: 'bg-info-subtle text-info border-info-border',
}

const toneIconClass: Record<SummaryPresentation['tone'], string> = {
  blue: 'text-info',
  amber: 'text-warning',
  red: 'text-danger',
  orange: 'text-warning',
  green: 'text-success',
  gray: 'text-muted-foreground',
  violet: 'text-info',
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
      className="inline-flex items-center gap-1.5 rounded-full bg-card/70 border border-current/20 px-2.5 py-1 text-xs font-medium"
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
  primary,
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
  primary: boolean
}) {
  const isPending = pendingKind === action.kind
  const variant = primary && action.kind !== 'stop'
    ? 'default'
    : action.kind === 'send-back' || (primary && action.kind === 'stop')
      ? 'destructive'
      : 'outline'
  const disabledReason = action.kind === 'inspect'
    ? (action.reason ?? 'Transcript navigation is not available from this surface yet.')
    : action.reason

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
      data-primary={primary ? 'true' : 'false'}
      variant={variant}
      size="sm"
      onClick={onClick}
      disabled={!action.enabled || isPending || action.kind === 'inspect'}
      title={disabledReason ?? undefined}
      className="min-w-[7rem]"
    >
      {isPending ? busyLabel : action.label}
    </Button>
  )
}

export function RuntimeDecisionSurface({
  decision,
  mutations,
}: RuntimeDecisionSurfaceProps) {
  const [stopConfirming, setStopConfirming] = useState(false)
  const [sendBackOpen, setSendBackOpen] = useState(false)
  const [sendBackText, setSendBackText] = useState('')
  const {
    approveMutation,
    sendBackMutation,
    retryMutation,
    resumeMutation,
    rerunMutation,
    forceStopMutation,
    stopMutation,
    startMutation,
  } = mutations

  const presentation = SUMMARY_PRESENTATION[decision.summary]
  const Icon = presentation.icon
  const pendingKind: RuntimeAvailableAction['kind'] | null =
    approveMutation.isPending ? 'approve'
    : sendBackMutation.isPending ? 'send-back'
    : retryMutation.isPending ? 'retry'
    : resumeMutation.isPending ? 'resume'
    : rerunMutation.isPending ? 'rerun'
    : forceStopMutation.isPending || stopMutation.isPending ? 'stop'
    : startMutation.isPending ? 'start'
    : null
  const actionError = approveMutation.error
    || sendBackMutation.error
    || retryMutation.error
    || resumeMutation.error
    || rerunMutation.error
    || forceStopMutation.error
    || stopMutation.error
    || startMutation.error
  const visibleActions = [
    ...(decision.primary ? [decision.primary] : []),
    ...decision.actions.filter((action) => action.kind !== decision.primary?.kind),
  ]
  const hasVisibleStop = visibleActions.some((action) => action.kind === 'stop')

  const runStop = () => {
    if (!stopConfirming) {
      setStopConfirming(true)
      return
    }
    if (decision.stopRecoverable) {
      forceStopMutation.mutate()
    } else {
      stopMutation.mutate()
    }
  }
  const openSendBack = () => {
    setSendBackOpen(true)
  }
  const submitSendBack = () => {
    const body = sendBackText.trim()
    if (!body || !decision.approvalStage) return
    sendBackMutation.mutate(
      { stage: decision.approvalStage, body },
      {
        onSuccess: () => {
          setSendBackOpen(false)
          setSendBackText('')
        },
      },
    )
  }

  return (
    <section
      data-testid="runtime-decision-surface"
      data-summary={decision.summary}
      data-tone={presentation.tone}
      role="region"
      aria-label="Issue runtime decision"
      className={cn(
        'rounded-lg border border-l-4 border-border bg-card p-4 shadow-sm',
        toneEdgeClass[presentation.tone],
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
            className={cn(
              'inline-flex items-center rounded-full border px-2 py-0.5 text-[10px] font-bold uppercase tracking-wide',
              toneLabelClass[presentation.tone],
            )}
          >
            {presentation.label}
          </span>
        </div>
        <h2
          data-testid="runtime-headline"
          className="flex-1 min-w-0 text-base font-semibold leading-tight text-card-foreground"
        >
          {decision.headline}
        </h2>
        <CurrentTaskPill task={decision.currentTask} />
      </header>

      <p
        data-testid="runtime-rationale"
        className="mt-2 text-sm text-muted-foreground"
      >
        {decision.rationale}
      </p>

      <div className="mt-3 flex flex-wrap items-center gap-3">
        <span
          data-testid="runtime-next-action"
          className="text-xs font-medium uppercase tracking-wide text-muted-foreground"
        >
          Next action
        </span>
        <span
          data-testid="runtime-next-action-body"
          className="text-sm text-card-foreground"
        >
          {decision.nextAction}
        </span>
      </div>

      {decision.summary === 'queued' && decision.waitReason && (
        <p
          data-testid="runtime-wait-reason"
          className="mt-2 text-xs text-muted-foreground"
        >
          Waiting on: {decision.waitReason}
        </p>
      )}

      {decision.driftNote && (
        <p
          data-testid="runtime-drift-note"
          className="mt-2 text-xs italic text-muted-foreground"
        >
          Drift: {decision.driftNote}
        </p>
      )}

      {visibleActions.length > 0 && (
        <div
          data-testid="runtime-actions"
          className="mt-4 flex flex-wrap gap-2"
        >
          {visibleActions.map((action) => (
            <SurfaceActionButton
              key={action.kind}
              action={action}
              onApprove={() => approveMutation.mutate()}
              onSendBack={openSendBack}
              onRetry={() => retryMutation.mutate()}
              onResume={() => resumeMutation.mutate()}
              onRerun={() => rerunMutation.mutate()}
              onStop={runStop}
              onStart={() => startMutation.mutate()}
              pendingKind={pendingKind}
              primary={action.kind === decision.primary?.kind}
            />
          ))}
        </div>
      )}

      {sendBackOpen && (
        <div
          data-testid="runtime-send-back-form"
          className="mt-3 rounded-md border border-border bg-muted p-3"
        >
          <label
            htmlFor="runtime-send-back-body"
            className="text-xs font-medium text-card-foreground"
          >
            What should the agent change?
          </label>
          <Textarea
            id="runtime-send-back-body"
            data-testid="runtime-send-back-textarea"
            value={sendBackText}
            onChange={(event) => setSendBackText(event.target.value)}
            rows={3}
            className="mt-2 resize-none bg-card"
            placeholder="Describe the changes you want before the workflow continues..."
          />
          <div className="mt-2 flex justify-end gap-2">
            <Button
              type="button"
              variant="ghost"
              size="sm"
              disabled={sendBackMutation.isPending}
              onClick={() => {
                setSendBackOpen(false)
                setSendBackText('')
              }}
            >
              Cancel
            </Button>
            <Button
              type="button"
              size="sm"
              data-testid="runtime-submit-send-back"
              disabled={!sendBackText.trim() || !decision.approvalStage || sendBackMutation.isPending}
              onClick={submitSendBack}
            >
              {sendBackMutation.isPending ? 'Sending back...' : 'Submit feedback'}
            </Button>
          </div>
        </div>
      )}

      {stopConfirming && hasVisibleStop && (
        <div
          data-testid="runtime-stop-confirmation-copy"
          className="mt-3 rounded-md border border-danger-border bg-danger-subtle px-3 py-2 text-xs text-danger"
        >
          {decision.stopRecoverable
            ? 'Stop will preserve progress so this workflow can be resumed later.'
            : 'Stop is irreversible for this workflow run; progress cannot be resumed.'}
        </div>
      )}

      {actionError && (
        <div
          data-testid="runtime-action-error"
          className="mt-3 rounded-md border border-danger-border bg-danger-subtle px-3 py-2 text-xs text-danger"
        >
          {actionError.message}
        </div>
      )}
    </section>
  )
}
