import { useState } from 'react'
import type { UseMutationResult } from '@tanstack/react-query'
import { Button } from '@/shared/ui/components/button'
import { Textarea } from '@/shared/ui/components/textarea'
import { cn } from '@/shared/lib/utils'
import {
  type RuntimeAvailableAction,
  type RuntimeDecision,
  type RuntimeSummary,
} from '../model/derive-runtime-decision'
import { getStopConsequenceCopy, invokeAction } from '../runtime-action-handlers'
import { ArtifactOpener } from './ArtifactOpener'
import type {
  ArtifactContentHook,
  ArtifactOpenerArtifactsHook,
} from './ArtifactOpener'

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

export interface DecisionEvidence {
  issueNumber: number
  workflowRunId?: string | null
  artifactsHook?: ArtifactOpenerArtifactsHook
  contentHook?: ArtifactContentHook
  compactLimit?: number
}

export interface RuntimeDecisionSurfaceProps {
  decision: RuntimeDecision
  mutations: RuntimeDecisionSurfaceMutations
  evidence?: DecisionEvidence
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
