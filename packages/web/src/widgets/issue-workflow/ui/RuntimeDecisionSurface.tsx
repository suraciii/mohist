import { useState } from 'react'
import { Link } from 'react-router-dom'
import { ActivityIcon } from 'lucide-react'
import { Button } from '@/shared/ui/components/button'
import { Textarea } from '@/shared/ui/components/textarea'
import { cn } from '@/shared/lib/utils'
import {
  type RuntimeAvailableAction,
  type RuntimeDecision,
  type RuntimeSummary,
} from '../model/derive-runtime-decision'
import { getStopConsequenceCopy, invokeAction } from '../runtime-action-handlers'
import type { RuntimeDecisionSurfaceMutations } from '../model/runtime-action-types'
export type { RuntimeDecisionSurfaceMutations } from '../model/runtime-action-types'
import { ArtifactOpener } from './ArtifactOpener'
import type {
  ArtifactContentHook,
  ArtifactOpenerArtifactsHook,
} from './ArtifactOpener'

export interface DecisionEvidence {
  issueNumber: number
  workflowRunId?: string | null
  artifactsHook?: ArtifactOpenerArtifactsHook
  contentHook?: ArtifactContentHook
  compactLimit?: number
}

export interface ActiveSessionCue {
  sessionName: string
  transcriptPath: string
}

export interface RunnerGatingReason {
  reason: string
  kind: 'runner-unavailable' | 'capacity-full'
}

export interface ExecutionSignal {
  activeSession?: ActiveSessionCue | null
  runnerGating?: RunnerGatingReason | null
}

export interface DriftRecoveryAction {
  baseBranch: string
  trigger: () => void
  isPending: boolean
  isQueued: boolean
  isRebasing: boolean
  isConflictResolving: boolean
  isConflictFailed: boolean
  canRequest: boolean
  hasConflicts: string[] | null
  error: Error | null
  branch: string
}

export interface RuntimeDecisionSurfaceProps {
  decision: RuntimeDecision
  mutations: RuntimeDecisionSurfaceMutations
  evidence?: DecisionEvidence
  executionSignal?: ExecutionSignal | null
  driftRecovery?: DriftRecoveryAction | null
}

interface SummaryPresentation {
  label: string
  tone: 'blue' | 'amber' | 'red' | 'orange' | 'green' | 'gray' | 'violet'
}

const SUMMARY_PRESENTATION: Record<RuntimeSummary, SummaryPresentation> = {
  'running': {
    label: 'Running',
    tone: 'blue',
  },
  'queued': {
    label: 'Queued',
    tone: 'violet',
  },
  'approval-required': {
    label: 'Approval required',
    tone: 'amber',
  },
  'blocked': {
    label: 'Blocked',
    tone: 'orange',
  },
  'failed': {
    label: 'Failed',
    tone: 'red',
  },
  'done': {
    label: 'Done',
    tone: 'green',
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

function SurfaceActionButton({
  action,
  onClick,
  pendingKind,
  primary,
}: {
  action: RuntimeAvailableAction
  onClick: () => void
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

  const busyLabel = ({
    approve: 'Approving...',
    'send-back': 'Sending back...',
    retry: 'Retrying...',
    resume: 'Resuming...',
    rerun: 'Rerunning...',
    stop: 'Stopping...',
    start: 'Starting...',
  } as const)[action.kind as 'approve' | 'send-back' | 'retry' | 'resume' | 'rerun' | 'stop' | 'start']

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
  evidence,
  executionSignal,
  driftRecovery,
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
  const showEvidence = (
    decision.summary === 'approval-required'
    || decision.summary === 'blocked'
    || decision.summary === 'failed'
  ) && !!evidence?.workflowRunId
  const showExecutionSignal = !!executionSignal
    && (!!executionSignal.activeSession || !!executionSignal.runnerGating)
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
    invokeAction('stop', { decision, mutations })
  }
  const openSendBack = () => {
    setSendBackOpen(true)
  }
  const submitSendBack = () => {
    invokeAction('send-back', {
      decision,
      mutations,
      sendBackBody: sendBackText,
      callbacks: {
        onSendBackSuccess: () => {
          setSendBackOpen(false)
          setSendBackText('')
        },
      },
    })
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
      <p
        data-testid="runtime-rationale"
        className="text-sm text-muted-foreground"
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

      {driftRecovery && decision.driftNote && (
        <div
          data-testid="runtime-drift-recovery"
          data-summary={decision.summary}
          className="mt-3 rounded-md border border-warning-border bg-warning-subtle px-3 py-2 text-xs text-warning"
        >
          <div className="flex flex-wrap items-center justify-between gap-3">
            <span className="font-medium text-warning">
              Base drift needs attention
            </span>
            <Button
              data-testid="runtime-drift-recovery-action"
              variant="outline"
              size="sm"
              onClick={driftRecovery.trigger}
              disabled={!driftRecovery.canRequest}
              title={driftRecovery.canRequest ? undefined : 'Rebase unavailable right now'}
              className="min-w-[7rem] border-warning-border text-warning hover:bg-warning-subtle"
            >
              {driftRecovery.isQueued
                ? 'Rebase queued'
                : driftRecovery.isPending
                  ? 'Rebasing...'
                  : driftRecovery.isConflictResolving
                    ? 'Resolving conflicts...'
                    : `Rebase onto ${driftRecovery.baseBranch}`}
            </Button>
          </div>
          {driftRecovery.branch && (
            <p className="mt-1 text-[11px] text-muted-foreground">
              Current branch <span className="font-mono">{driftRecovery.branch}</span>
              {' '}onto <span className="font-mono">{driftRecovery.baseBranch}</span>
            </p>
          )}
          {driftRecovery.hasConflicts && !driftRecovery.isConflictFailed && (
            <div className="mt-2 rounded-md border border-danger-border bg-danger-subtle px-2 py-1 text-[11px] text-danger">
              <span className="font-medium">Conflicting files: </span>
              <span className="font-mono">{driftRecovery.hasConflicts.join(', ')}</span>
            </div>
          )}
          {driftRecovery.isConflictFailed && (
            <div className="mt-2 rounded-md border border-danger-border bg-danger-subtle px-2 py-1 text-[11px] text-danger">
              Conflict resolution failed
            </div>
          )}
          {driftRecovery.error && !driftRecovery.isConflictFailed && (
            <div className="mt-2 rounded-md border border-danger-border bg-danger-subtle px-2 py-1 text-[11px] text-danger">
              {driftRecovery.error.message}
            </div>
          )}
        </div>
      )}

      {showExecutionSignal && executionSignal && (
        <div
          data-testid="runtime-execution-signal"
          className="mt-2 flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-muted-foreground"
        >
          {executionSignal.activeSession && (
            <span
              data-testid="runtime-execution-signal-session"
              data-session-name={executionSignal.activeSession.sessionName}
              className="inline-flex items-center gap-1"
            >
              <ActivityIcon className="size-3 text-info" aria-hidden="true" />
              <span className="text-muted-foreground/80">Session:</span>
              <Link
                to={executionSignal.activeSession.transcriptPath}
                data-testid="runtime-execution-signal-session-link"
                className="font-mono font-medium text-foreground/80 hover:text-foreground hover:underline"
                title={`Open ${executionSignal.activeSession.sessionName} transcript`}
              >
                {executionSignal.activeSession.sessionName}
              </Link>
            </span>
          )}
          {executionSignal.runnerGating && (
            <span
              data-testid="runtime-execution-signal-runner"
              data-gating-kind={executionSignal.runnerGating.kind}
              className="inline-flex items-center gap-1"
            >
              <span className="text-muted-foreground/80">Runner:</span>
              <span className="font-medium text-foreground/80">
                {executionSignal.runnerGating.reason}
              </span>
            </span>
          )}
        </div>
      )}

      {showEvidence && evidence && (
        <ArtifactOpener
          issueNumber={evidence.issueNumber}
          workflowRunId={evidence.workflowRunId}
          mode="compact"
          compactLimit={evidence.compactLimit ?? 3}
          artifactsHook={evidence.artifactsHook}
          contentHook={evidence.contentHook}
          evidenceSummary={decision.summary}
        />
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
              onClick={() => {
                if (action.kind === 'stop') {
                  runStop()
                  return
                }
                if (action.kind === 'send-back') {
                  openSendBack()
                  return
                }
                invokeAction(action.kind, { decision, mutations })
              }}
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
          {getStopConsequenceCopy(decision.stopRecoverable).body}
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
