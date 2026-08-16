import { useState } from 'react'
import { Link } from 'react-router-dom'
import { AlertCircleIcon, BotIcon } from 'lucide-react'
import { Button } from '@/shared/ui/components/button'
import { Textarea } from '@/shared/ui/components/textarea'
import { cn } from '@/shared/lib/utils'
import type { IssueDecisionAction, IssueDecisionActionKind } from '../model/issueDecisionActions'
import type { IssueDecisionActionController } from '../model/useIssueDecisionActions'
import { ConfirmationDrawer } from './ConfirmationDrawer'

const PENDING_BUSY_LABEL: Partial<Record<IssueDecisionActionKind, string>> = {
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
  'ask-agent': 'Opening...',
  'view-transcript': 'Opening...',
}

const PENDING_BUSY_MESSAGE = 'Another request is in progress. Wait for it to finish before trying again.'

function describeIdFor(kind: IssueDecisionActionKind) {
  return `mobile-sheet-action-${kind}-reason`
}

function isNavAction(action: IssueDecisionAction): boolean {
  return action.kind === 'ask-agent' || action.kind === 'view-transcript'
}

function describeReason(action: IssueDecisionAction): string | null {
  if (action.enabled) return null
  if (action.reason) return action.reason
  if (action.kind === 'start') return 'Mark the issue ready before starting.'
  if (action.kind === 'approve') return 'Approval is not available right now.'
  if (action.kind === 'send-back') return 'Send-back is not available right now.'
  if (action.kind === 'stop') return 'Stop becomes available between tasks.'
  if (action.kind === 'retry') return 'Retry is not available right now.'
  if (action.kind === 'resume') return 'Resume is not available right now.'
  if (action.kind === 'rerun') return 'Rerun is not available right now.'
  return null
}

export interface MobileActionBarProps {
  actions: ReadonlyArray<IssueDecisionAction>
  primary: IssueDecisionAction | null
  rationale: string
  nextAction: string
  controller: IssueDecisionActionController
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
}

export function MobileActionBar({
  actions,
  primary,
  rationale,
  nextAction,
  controller,
  summary,
}: MobileActionBarProps) {
  const [sheetOpen, setSheetOpen] = useState(false)
  const [sendBackOpen, setSendBackOpen] = useState(false)
  const [sendBackText, setSendBackText] = useState('')

  const pendingKind = controller.pendingKind
  const error = controller.error

  const handlePrimaryClick = () => {
    setSheetOpen(true)
  }

  const handleSheetClose = () => {
    if (pendingKind === 'stop' || pendingKind === 'send-back') return
    setSheetOpen(false)
    setSendBackOpen(false)
    setSendBackText('')
  }

  const openStopConfirm = () => {
    controller.openStopConfirm()
  }

  const closeStopConfirm = () => {
    controller.closeStopConfirm()
  }

  const submitSendBack = () => {
    const sendBackAction = actions.find((a) => a.kind === 'send-back')
    if (!sendBackAction) return
    controller.runAction(sendBackAction, { sendBackBody: sendBackText })
  }

  const launcherLabel = primary ? primary.label : (actions[0]?.label ?? 'Open actions')
  const launcherPendingLabel =
    primary && pendingKind === primary.kind ? (PENDING_BUSY_LABEL[primary.kind] ?? primary.pendingLabel) : null
  const launcherLabelText = launcherPendingLabel ?? launcherLabel

  return (
    <>
      <div
        data-testid="mobile-action-bar"
        data-summary={summary}
        className={cn(
          'fixed inset-x-0 z-30 isolate px-3 pb-[calc(0.5rem+env(safe-area-inset-bottom))]',
          'bottom-[calc(3.5rem+env(safe-area-inset-bottom))] md:bottom-0',
        )}
      >
        <div className="mx-auto w-full max-w-md rounded-xl border border-border bg-popover/95 backdrop-blur p-2 shadow-lg ring-1 ring-foreground/5 space-y-2">
          <Button
            type="button"
            variant="default"
            data-testid="mobile-action-sheet-launcher"
            data-action-kind={primary?.kind ?? 'none'}
            onClick={handlePrimaryClick}
            className="w-full min-h-[44px] text-sm font-semibold"
          >
            {launcherLabelText}
          </Button>
        </div>
      </div>

      <ConfirmationDrawer
        open={sheetOpen}
        onClose={handleSheetClose}
        testId="mobile-action-sheet"
        titleId="mobile-action-sheet-title"
        descriptionId="mobile-action-sheet-description"
      >
        <div className="p-4 space-y-4" data-testid="mobile-action-sheet-body">
          <div className="space-y-1">
            <h3
              id="mobile-action-sheet-title"
              data-testid="mobile-action-sheet-title-text"
              className="text-base font-semibold text-popover-foreground"
            >
              Issue actions
            </h3>
            <p
              id="mobile-action-sheet-description"
              data-testid="mobile-action-sheet-rationale"
              className="text-sm text-muted-foreground"
            >
              {rationale}
            </p>
            <p
              data-testid="mobile-action-sheet-next-action"
              className="text-xs font-medium uppercase tracking-wide text-muted-foreground"
            >
              Next action: <span className="text-popover-foreground normal-case font-normal">{nextAction}</span>
            </p>
          </div>

          <div className="space-y-2" data-testid="mobile-action-sheet-actions">
            {actions.map((action) => {
              const isPending = pendingKind === action.kind
              const isBusy = pendingKind !== null
              const reason = isBusy ? PENDING_BUSY_MESSAGE : describeReason(action)
              const isDisabled = !action.enabled || isBusy
              const descriptionId = isDisabled ? describeIdFor(action.kind) : undefined
              const label = isPending ? (PENDING_BUSY_LABEL[action.kind] ?? action.pendingLabel) : action.label

              if (isNavAction(action) && action.to) {
                return (
                  <div key={action.kind} className="flex flex-col items-stretch gap-1">
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
                        handleSheetClose()
                      }}
                      data-testid={`mobile-sheet-action-${action.kind}`}
                      className={cn(
                        'inline-flex items-center justify-center gap-1.5 whitespace-nowrap rounded-lg border px-3 h-10 text-sm font-medium transition-all outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50',
                        action.primary
                          ? 'border-transparent bg-primary text-primary-foreground hover:bg-primary/80'
                          : 'border-border bg-background text-foreground hover:bg-muted hover:text-foreground',
                        isDisabled && 'pointer-events-none opacity-50',
                      )}
                    >
                      {action.kind === 'ask-agent' && !isPending ? (
                        <BotIcon className="size-4" aria-hidden="true" />
                      ) : null}
                      {label}
                    </Link>
                    {isDisabled && reason && (
                      <p
                        id={descriptionId}
                        data-testid={`mobile-sheet-action-${action.kind}-reason`}
                        className="text-xs text-muted-foreground"
                      >
                        {reason}
                      </p>
                    )}
                  </div>
                )
              }

              const handleActionClick = () => {
                if (action.kind === 'stop' && action.enabled) {
                  openStopConfirm()
                  return
                }
                if (action.kind === 'send-back' && action.enabled) {
                  setSendBackOpen(true)
                  return
                }
                controller.runAction(action)
              }

              return (
                <div key={action.kind} className="flex flex-col items-stretch gap-1">
                  <Button
                    type="button"
                    variant={
                      action.primary
                        ? 'default'
                        : action.kind === 'send-back' || action.kind === 'stop'
                          ? 'destructive'
                          : 'outline'
                    }
                    size="default"
                    data-testid={`mobile-sheet-action-${action.kind}`}
                    data-primary={action.primary ? 'true' : 'false'}
                    data-destructive={action.kind === 'stop' || action.kind === 'send-back' ? 'true' : 'false'}
                    disabled={isDisabled}
                    aria-describedby={descriptionId}
                    onClick={handleActionClick}
                    className={cn(
                      'min-h-[40px] justify-center text-sm font-semibold',
                      (action.kind === 'stop' || action.kind === 'send-back') &&
                        isDisabled &&
                        'border-border bg-muted text-muted-foreground hover:bg-muted',
                    )}
                  >
                    {label}
                  </Button>
                  {isDisabled && reason && (
                    <p
                      id={descriptionId}
                      data-testid={`mobile-sheet-action-${action.kind}-reason`}
                      className="text-xs text-muted-foreground"
                    >
                      {reason}
                    </p>
                  )}
                  {isPending && (
                    <p
                      data-testid={`mobile-sheet-action-${action.kind}-pending`}
                      className="sr-only"
                      aria-live="polite"
                    >
                      {`${PENDING_BUSY_LABEL[action.kind] ?? action.pendingLabel}. ${PENDING_BUSY_MESSAGE}`}
                    </p>
                  )}
                </div>
              )
            })}

            {actions.length === 0 && (
              <div
                data-testid="mobile-sheet-no-action"
                className="flex items-start gap-2 rounded-md border border-border bg-muted px-3 py-2 text-sm text-muted-foreground"
              >
                <AlertCircleIcon className="mt-0.5 size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
                <span>
                  No action is currently available. The next transition will appear here when conditions change.
                </span>
              </div>
            )}
          </div>

          {error && !pendingKind && (
            <div
              data-testid="mobile-action-error"
              role="alert"
              aria-live="polite"
              className="rounded-md border border-danger-border bg-danger-subtle px-3 py-2 text-xs text-danger"
            >
              {error.message}
            </div>
          )}

          <div className="flex justify-end">
            <Button
              type="button"
              variant="ghost"
              size="sm"
              data-testid="mobile-action-sheet-close"
              onClick={handleSheetClose}
              disabled={pendingKind === 'stop' || pendingKind === 'send-back'}
            >
              Close
            </Button>
          </div>
        </div>

        {controller.stopConfirming && (
          <div data-testid="mobile-stop-confirmation" className="px-4 pb-4 space-y-3">
            <div className="space-y-1">
              <h3
                data-testid="mobile-stop-confirmation-title"
                className="text-base font-semibold text-popover-foreground"
              >
                {controller.stopConfirmTitle}
              </h3>
              <p data-testid="mobile-stop-confirmation-body" className="text-sm text-muted-foreground">
                {controller.stopConfirmBody}
              </p>
            </div>
            <div className="flex justify-end gap-2">
              <Button
                type="button"
                variant="ghost"
                size="sm"
                data-testid="mobile-stop-confirmation-cancel"
                onClick={closeStopConfirm}
                disabled={pendingKind === 'stop'}
              >
                Cancel
              </Button>
              <Button
                type="button"
                variant="destructive"
                size="sm"
                data-testid="mobile-stop-confirmation-confirm"
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

        {sendBackOpen && (
          <div data-testid="mobile-send-back-form" className="px-4 pb-4 space-y-3">
            <div className="space-y-1">
              <h3 className="text-base font-semibold text-popover-foreground">Send back for changes</h3>
              <p className="text-sm text-muted-foreground">
                Tell the agent what to change before the workflow continues.
              </p>
            </div>
            <div>
              <label htmlFor="mobile-send-back-body" className="text-xs font-medium text-popover-foreground">
                Feedback
              </label>
              <Textarea
                id="mobile-send-back-body"
                data-testid="mobile-send-back-textarea"
                value={sendBackText}
                onChange={(event) => setSendBackText(event.target.value)}
                rows={3}
                className="mt-2 resize-none bg-background"
                placeholder="Describe the changes you want before the workflow continues..."
              />
            </div>
            <div className="flex justify-end gap-2">
              <Button
                type="button"
                variant="ghost"
                size="sm"
                data-testid="mobile-send-back-cancel"
                onClick={() => {
                  setSendBackOpen(false)
                  setSendBackText('')
                }}
                disabled={pendingKind === 'send-back'}
              >
                Cancel
              </Button>
              <Button
                type="button"
                size="sm"
                data-testid="mobile-send-back-confirm"
                disabled={!controller.sendBackBodyValid(sendBackText) || pendingKind === 'send-back'}
                onClick={submitSendBack}
              >
                {pendingKind === 'send-back' ? 'Sending back...' : 'Submit feedback'}
              </Button>
            </div>
          </div>
        )}
      </ConfirmationDrawer>
    </>
  )
}
