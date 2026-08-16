import { useState } from 'react'
import { Link } from 'react-router-dom'
import { AlertCircleIcon, CircleCheckIcon, BotIcon } from 'lucide-react'
import { Button } from '@/shared/ui/components/button'
import { Textarea } from '@/shared/ui/components/textarea'
import { cn } from '@/shared/lib/utils'
import type { IssueDecisionAction, IssueDecisionActionKind } from '../model/issueDecisionActions'
import type { IssueDecisionActionController } from '../model/useIssueDecisionActions'

export interface IssueDecisionSurfaceProps {
  actions: ReadonlyArray<IssueDecisionAction>
  summary:
    | 'running'
    | 'recoverable-interrupted'
    | 'queued'
    | 'approval-required'
    | 'blocked'
    | 'failed'
    | 'done'
    | 'cancelled'
    | 'done-no-action'
    | 'terminal-no-action'
  rationale: string
  nextAction: string
  controller: IssueDecisionActionController
  evidence?: React.ReactNode
  sendBackOpen?: boolean
  onSendBackOpen?: () => void
  sendBackForm?: React.ReactNode
  shortcutHints?: Partial<Record<IssueDecisionActionKind, string>>
  className?: string
}

interface SummaryPresentation {
  label: string
  tone: 'blue' | 'amber' | 'red' | 'orange' | 'green' | 'gray' | 'violet'
}

const SUMMARY_PRESENTATION: Record<IssueDecisionSurfaceProps['summary'], SummaryPresentation> = {
  running: { label: 'Running', tone: 'blue' },
  'recoverable-interrupted': { label: 'Recovering', tone: 'amber' },
  queued: { label: 'Queued', tone: 'violet' },
  'approval-required': { label: 'Approval required', tone: 'amber' },
  blocked: { label: 'Blocked', tone: 'orange' },
  failed: { label: 'Failed', tone: 'red' },
  done: { label: 'Done', tone: 'green' },
  cancelled: { label: 'Cancelled', tone: 'gray' },
  'done-no-action': { label: 'Done', tone: 'green' },
  'terminal-no-action': { label: 'No action available', tone: 'gray' },
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

const PENDING_BUSY_LABEL: Record<IssueDecisionActionKind, string> = {
  approve: 'Approving...',
  'send-back': 'Sending back...',
  retry: 'Retrying...',
  resume: 'Resuming...',
  rerun: 'Rerunning stage...',
  stop: 'Stopping...',
  start: 'Starting...',
  'mark-ready': 'Marking ready...',
  close: 'Closing...',
  'mark-as-done': 'Marking done...',
  'ask-agent': 'Opening agent composer...',
  'view-transcript': 'Opening transcript...',
}

const PENDING_BUSY_MESSAGE = 'Another request is in progress. Wait for it to finish before trying again.'

function describeIdFor(kind: IssueDecisionActionKind) {
  return `decision-action-${kind}-reason`
}

function isNavAction(action: IssueDecisionAction): boolean {
  return action.kind === 'ask-agent' || action.kind === 'view-transcript'
}

function isDestructive(action: IssueDecisionAction): boolean {
  return action.kind === 'stop' || action.kind === 'close'
}

function describeReason(action: IssueDecisionAction): string | null {
  if (action.enabled) return null
  if (action.reason) return action.reason
  if (action.kind === 'start') return 'Mark the issue ready before starting.'
  if (action.kind === 'retry') return 'Retry is not available right now.'
  if (action.kind === 'resume') return 'Resume is not available right now.'
  if (action.kind === 'rerun') return 'Rerun is not available right now.'
  if (action.kind === 'approve') return 'Approval is not available right now.'
  if (action.kind === 'send-back') return 'Send-back is not available right now.'
  if (action.kind === 'stop') return 'Stop becomes available between tasks.'
  return null
}

function variantFor(action: IssueDecisionAction, primary: boolean): 'default' | 'destructive' | 'outline' {
  if (action.kind === 'approve') return primary ? 'default' : 'outline'
  if (action.kind === 'send-back') return 'destructive'
  if (action.kind === 'stop') return primary ? 'destructive' : 'outline'
  if (action.kind === 'close') return 'outline'
  if (action.kind === 'mark-ready') return 'default'
  if (action.kind === 'mark-as-done') return 'default'
  if (action.kind === 'start') return primary ? 'default' : 'outline'
  if (action.kind === 'retry' || action.kind === 'resume' || action.kind === 'rerun')
    return primary ? 'default' : 'outline'
  if (action.kind === 'ask-agent') return 'outline'
  if (action.kind === 'view-transcript') return 'outline'
  return 'outline'
}

function ActionButton({
  action,
  primary,
  pendingKind,
  error,
  onClick,
  shortcutHint,
}: {
  action: IssueDecisionAction
  primary: boolean
  pendingKind: IssueDecisionActionKind | null
  error: Error | null
  onClick: () => void
  shortcutHint?: string
}) {
  const isPending = pendingKind === action.kind
  const isBusy = pendingKind !== null
  const reason = isBusy ? PENDING_BUSY_MESSAGE : describeReason(action)
  const isDisabled = !action.enabled || isBusy
  const descriptionId = isDisabled ? describeIdFor(action.kind) : undefined

  if (isNavAction(action)) {
    const label = isPending ? PENDING_BUSY_LABEL[action.kind] : action.kind === 'ask-agent' ? 'Ask Agent' : action.label
    if (action.to) {
      return (
        <div className="flex flex-col items-start gap-1" data-decision-nav={action.kind}>
          <Link
            to={isDisabled ? '#' : action.to}
            aria-disabled={isDisabled}
            aria-describedby={descriptionId}
            tabIndex={isDisabled ? -1 : 0}
            onClick={(event) => {
              if (isDisabled) {
                event.preventDefault()
                return
              }
              onClick()
            }}
            data-testid={`decision-action-${action.kind}`}
            data-primary={primary ? 'true' : 'false'}
            className={cn(
              'inline-flex items-center justify-center gap-1.5 whitespace-nowrap rounded-lg border px-2.5 text-sm font-medium transition-all outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50',
              primary
                ? 'h-8 border-transparent bg-primary text-primary-foreground hover:bg-primary/80'
                : 'h-8 border-border bg-background text-foreground hover:bg-muted hover:text-foreground',
              isDisabled && 'pointer-events-none opacity-50',
            )}
          >
            {action.kind === 'ask-agent' && !isPending ? <BotIcon className="size-4" aria-hidden="true" /> : null}
            {label}
          </Link>
          {isDisabled && reason && (
            <p
              id={descriptionId}
              data-testid={`decision-action-${action.kind}-reason`}
              className="text-xs text-muted-foreground"
            >
              {reason}
            </p>
          )}
        </div>
      )
    }
  }

  const label = isPending ? (PENDING_BUSY_LABEL[action.kind] ?? action.pendingLabel) : action.label

  return (
    <div className="flex flex-col items-start gap-1">
      <div className="flex items-center gap-2">
        <Button
          variant={variantFor(action, primary)}
          size="sm"
          data-testid={`decision-action-${action.kind}`}
          data-primary={primary ? 'true' : 'false'}
          data-destructive={isDestructive(action) ? 'true' : 'false'}
          disabled={isDisabled}
          aria-describedby={descriptionId}
          onClick={onClick}
          className={cn(
            'min-w-[7rem]',
            isDestructive(action) && isDisabled && 'border-border bg-muted text-muted-foreground hover:bg-muted',
          )}
        >
          {action.kind === 'mark-as-done' && !isPending ? (
            <CircleCheckIcon className="size-4" aria-hidden="true" />
          ) : null}
          {label}
        </Button>
        {shortcutHint && (
          <kbd
            data-testid={`decision-action-${action.kind}-shortcut`}
            className="rounded border border-border bg-muted px-1.5 py-0.5 text-xs font-mono text-muted-foreground"
          >
            {shortcutHint}
          </kbd>
        )}
      </div>
      {isDisabled && reason && (
        <p
          id={descriptionId}
          data-testid={`decision-action-${action.kind}-reason`}
          className="text-xs text-muted-foreground"
        >
          {reason}
        </p>
      )}
      {isPending && (
        <p data-testid={`decision-action-${action.kind}-pending`} className="sr-only" aria-live="polite">
          {`${PENDING_BUSY_LABEL[action.kind]}. ${PENDING_BUSY_MESSAGE}`}
        </p>
      )}
      {isPending && error && (
        <p data-testid={`decision-action-${action.kind}-error`} className="sr-only" role="alert" aria-live="assertive">
          {error.message}
        </p>
      )}
    </div>
  )
}

export function IssueDecisionSurface({
  actions,
  summary,
  rationale,
  nextAction,
  controller,
  evidence,
  sendBackOpen: controlledSendBackOpen,
  onSendBackOpen,
  sendBackForm,
  shortcutHints,
  className,
}: IssueDecisionSurfaceProps) {
  const presentation = SUMMARY_PRESENTATION[summary]
  const [uncontrolledSendBackOpen, setUncontrolledSendBackOpen] = useState(false)
  const [sendBackText, setSendBackText] = useState('')
  const pendingKind = controller.pendingKind
  const error = controller.error
  const sendBackOpen = controlledSendBackOpen ?? uncontrolledSendBackOpen

  const primaryKind = actions.find((a) => a.primary)?.kind ?? null
  const visibleActions = [...actions]
  const allDisabled = actions.length > 0 && actions.every((a) => !a.enabled)
  const showNoActionExplanation = actions.length === 0 || allDisabled

  const handleClick = (action: IssueDecisionAction) => {
    if (action.kind === 'stop' && action.enabled) {
      controller.openStopConfirm()
      return
    }
    if (action.kind === 'send-back' && action.enabled) {
      onSendBackOpen?.()
      if (!onSendBackOpen) setUncontrolledSendBackOpen(true)
      return
    }
    controller.runAction(action)
  }

  return (
    <section
      data-testid="issue-decision-surface"
      data-summary={summary}
      data-tone={presentation.tone}
      role="region"
      aria-label="Issue decision"
      className={cn(
        'rounded-lg border border-l-4 border-border bg-card p-4 shadow-sm',
        toneEdgeClass[presentation.tone],
        className,
      )}
    >
      <p data-testid="decision-rationale" className="text-sm text-muted-foreground">
        {rationale}
      </p>

      <div className="mt-3 flex flex-wrap items-center gap-3">
        <span
          data-testid="decision-next-action"
          className="text-xs font-medium uppercase tracking-wide text-muted-foreground"
        >
          Next action
        </span>
        <span data-testid="decision-next-action-body" className="text-sm text-card-foreground">
          {nextAction}
        </span>
      </div>

      {evidence}

      {showNoActionExplanation && actions.length === 0 && (
        <div
          data-testid="decision-no-action-explanation"
          className="mt-3 flex items-start gap-2 rounded-md border border-border bg-muted px-3 py-2 text-sm text-muted-foreground"
        >
          <AlertCircleIcon className="mt-0.5 size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
          <span>No action is currently available. The next transition will appear here when conditions change.</span>
        </div>
      )}

      {actions.length > 0 && (
        <div data-testid="decision-actions" className="mt-4 flex flex-wrap items-start gap-3">
          {visibleActions.map((action) => (
            <ActionButton
              key={action.kind}
              action={action}
              primary={action.kind === primaryKind}
              pendingKind={pendingKind}
              error={error}
              onClick={() => handleClick(action)}
              shortcutHint={shortcutHints?.[action.kind]}
            />
          ))}
        </div>
      )}

      {controller.stopConfirming && (
        <div
          data-testid="decision-stop-confirmation"
          className="mt-3 rounded-md border border-border bg-muted px-3 py-2 text-sm text-foreground"
        >
          <div className="font-medium text-foreground">{controller.stopConfirmTitle}</div>
          <p className="mt-1 text-xs text-muted-foreground">{controller.stopConfirmBody}</p>
          <div className="mt-2 flex justify-end gap-2">
            <Button
              type="button"
              size="sm"
              variant="ghost"
              data-testid="decision-stop-cancel"
              onClick={controller.closeStopConfirm}
              disabled={pendingKind === 'stop'}
            >
              Cancel
            </Button>
            <Button
              type="button"
              size="sm"
              variant="destructive"
              data-testid="decision-stop-confirm"
              onClick={() => {
                const stopAction = actions.find((a) => a.kind === 'stop')
                if (stopAction) controller.runAction(stopAction)
              }}
              disabled={pendingKind === 'stop'}
            >
              {pendingKind === 'stop' ? 'Stopping...' : 'Stop workflow'}
            </Button>
          </div>
        </div>
      )}

      {sendBackOpen && sendBackForm
        ? sendBackForm
        : sendBackOpen && (
            <div data-testid="decision-send-back-form" className="mt-3 rounded-md border border-border bg-muted p-3">
              <label htmlFor="decision-send-back-body" className="text-xs font-medium text-card-foreground">
                What should the agent change?
              </label>
              <Textarea
                id="decision-send-back-body"
                data-testid="decision-send-back-textarea"
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
                  data-testid="decision-send-back-cancel"
                  onClick={() => {
                    setUncontrolledSendBackOpen(false)
                    setSendBackText('')
                  }}
                  disabled={pendingKind === 'send-back'}
                >
                  Cancel
                </Button>
                <Button
                  type="button"
                  size="sm"
                  data-testid="decision-send-back-confirm"
                  disabled={!controller.sendBackBodyValid(sendBackText) || pendingKind === 'send-back'}
                  onClick={() => {
                    const sendBackAction = actions.find((a) => a.kind === 'send-back')
                    if (!sendBackAction) return
                    controller.runAction(sendBackAction, { sendBackBody: sendBackText })
                  }}
                >
                  {pendingKind === 'send-back' ? 'Sending back...' : 'Submit feedback'}
                </Button>
              </div>
            </div>
          )}

      {error && (
        <div
          data-testid="decision-action-error"
          role="alert"
          aria-live="polite"
          className="mt-3 rounded-md border border-danger-border bg-danger-subtle px-3 py-2 text-xs text-danger"
        >
          {error.message}
        </div>
      )}
    </section>
  )
}

export const __DECISION_SURFACE_TESTID = 'issue-decision-surface'
