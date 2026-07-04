import { useState } from 'react'
import { Button } from '@/shared/ui/components/button'
import { Textarea } from '@/shared/ui/components/textarea'
import { cn } from '@/shared/lib/utils'
import { getStopConsequenceCopy, invokeAction } from '../../../widgets/issue-workflow'
import type {
  RuntimeAvailableAction,
  RuntimeDecision,
} from '../../../widgets/issue-workflow/model/derive-runtime-decision'
import type { RuntimeDecisionSurfaceMutations } from '../../../widgets/issue-workflow/ui/RuntimeDecisionSurface'
import { ConfirmationDrawer } from './ConfirmationDrawer'

type ConfirmKind = 'stop' | 'send-back'

export interface MobileActionBarProps {
  decision: RuntimeDecision
  mutations: RuntimeDecisionSurfaceMutations
}

const PENDING_LABEL_BY_KIND: Record<string, string> = {
  approve: 'Approving...',
  'send-back': 'Sending back...',
  retry: 'Retrying...',
  resume: 'Resuming...',
  rerun: 'Rerunning...',
  stop: 'Stopping...',
  start: 'Starting...',
}

function isDestructiveKind(kind: RuntimeAvailableAction['kind']): kind is 'stop' | 'send-back' {
  return kind === 'stop' || kind === 'send-back'
}

function isPending(decision: RuntimeDecision, mutations: RuntimeDecisionSurfaceMutations): boolean {
  const kind = decision.primary?.kind
  if (!kind) return false
  if (kind === 'approve') return mutations.approveMutation.isPending
  if (kind === 'send-back') return mutations.sendBackMutation.isPending
  if (kind === 'retry') return mutations.retryMutation.isPending
  if (kind === 'resume') return mutations.resumeMutation.isPending
  if (kind === 'rerun') return mutations.rerunMutation.isPending
  if (kind === 'stop') {
    return mutations.stopMutation.isPending || mutations.forceStopMutation.isPending
  }
  if (kind === 'start') return mutations.startMutation.isPending
  return false
}

function getActionError(mutations: RuntimeDecisionSurfaceMutations): Error | null {
  return mutations.approveMutation.error
    || mutations.sendBackMutation.error
    || mutations.retryMutation.error
    || mutations.resumeMutation.error
    || mutations.rerunMutation.error
    || mutations.forceStopMutation.error
    || mutations.stopMutation.error
    || mutations.startMutation.error
}

export function MobileActionBar({ decision, mutations }: MobileActionBarProps) {
  const [confirmKind, setConfirmKind] = useState<ConfirmKind | null>(null)
  const [sendBackText, setSendBackText] = useState('')

  const primary = decision.primary
  if (!primary) return null

  const handlePrimaryClick = () => {
    if (!primary.enabled || pending) return
    if (isDestructiveKind(primary.kind)) {
      setConfirmKind(primary.kind)
      setSendBackText('')
      return
    }
    invokeAction(primary.kind, { decision, mutations })
  }

  const closeDrawer = () => {
    if (primary.kind === 'send-back' && mutations.sendBackMutation.isPending) return
    if (primary.kind === 'stop' && (mutations.stopMutation.isPending || mutations.forceStopMutation.isPending)) {
      return
    }
    setConfirmKind(null)
    setSendBackText('')
  }

  const confirmStop = () => {
    invokeAction('stop', { decision, mutations })
    setConfirmKind(null)
  }

  const submitSendBack = () => {
    invokeAction('send-back', {
      decision,
      mutations,
      sendBackBody: sendBackText,
      callbacks: {
        onSendBackSuccess: () => {
          setConfirmKind(null)
          setSendBackText('')
        },
      },
    })
  }

  const pending = isPending(decision, mutations)
  const actionError = getActionError(mutations)
  const disabledReason = primary.reason
  const disabledDescriptionId = disabledReason ? `mobile-action-${primary.kind}-reason` : undefined
  const label = pending ? PENDING_LABEL_BY_KIND[primary.kind] ?? primary.label : primary.label
  const buttonVariant = primary.kind === 'stop' || primary.kind === 'send-back' ? 'destructive' : 'default'

  const drawerTitleId = 'mobile-confirmation-drawer-title'
  const drawerDescriptionId = 'mobile-confirmation-drawer-description'

  return (
    <>
      <div
        data-testid="mobile-action-bar"
        data-action-kind={primary.kind}
        data-summary={decision.summary}
        className={cn(
          'fixed inset-x-0 z-30 isolate px-3 pb-[calc(0.5rem+env(safe-area-inset-bottom))]',
          'bottom-[calc(3.5rem+env(safe-area-inset-bottom))] md:bottom-0',
        )}
      >
        <div className="mx-auto w-full max-w-md rounded-xl border border-border bg-popover/95 backdrop-blur p-2 shadow-lg ring-1 ring-foreground/5 space-y-2">
          <Button
            type="button"
            variant={buttonVariant}
            data-testid={`mobile-action-${primary.kind}`}
            data-primary="true"
            disabled={!primary.enabled || pending}
            title={disabledReason ?? undefined}
            aria-describedby={disabledDescriptionId}
            onClick={handlePrimaryClick}
            className="w-full min-h-[44px] text-sm font-semibold"
          >
            {label}
          </Button>
          {disabledReason && (
            <p id={disabledDescriptionId} className="sr-only">
              {disabledReason}
            </p>
          )}
          {actionError && !confirmKind && (
            <div
              role="alert"
              aria-live="polite"
              data-testid="mobile-action-error"
              className="rounded-md border border-danger-border bg-danger-subtle px-3 py-2 text-xs text-danger"
            >
              {actionError.message}
            </div>
          )}
        </div>
      </div>

      <ConfirmationDrawer
        open={confirmKind !== null}
        onClose={closeDrawer}
        titleId={drawerTitleId}
        descriptionId={drawerDescriptionId}
      >
        {confirmKind === 'stop' && (
          <div className="p-4 space-y-3" data-testid="mobile-stop-confirmation">
            <div className="space-y-1">
              <h3
                id={drawerTitleId}
                data-testid="mobile-confirmation-title"
                className="text-base font-semibold text-popover-foreground"
              >
                {getStopConsequenceCopy(decision.stopRecoverable).title}
              </h3>
              <p
                id={drawerDescriptionId}
                data-testid="mobile-confirmation-body"
                className="text-sm text-muted-foreground"
              >
                {getStopConsequenceCopy(decision.stopRecoverable).body}
              </p>
            </div>
            <div className="flex justify-end gap-2">
              <Button
                type="button"
                variant="ghost"
                size="sm"
                onClick={closeDrawer}
                disabled={mutations.stopMutation.isPending || mutations.forceStopMutation.isPending}
                data-testid="mobile-confirmation-cancel"
              >
                Cancel
              </Button>
              <Button
                type="button"
                variant="destructive"
                size="sm"
                onClick={confirmStop}
                disabled={mutations.stopMutation.isPending || mutations.forceStopMutation.isPending}
                data-testid="mobile-confirmation-confirm"
              >
                {mutations.stopMutation.isPending || mutations.forceStopMutation.isPending
                  ? 'Stopping...'
                  : 'Stop workflow'}
              </Button>
            </div>
            {actionError && (
              <div
                role="alert"
                aria-live="polite"
                data-testid="mobile-action-error"
                className="rounded-md border border-danger-border bg-danger-subtle px-3 py-2 text-xs text-danger"
              >
                {actionError.message}
              </div>
            )}
          </div>
        )}

        {confirmKind === 'send-back' && (
          <div className="p-4 space-y-3" data-testid="mobile-send-back-form">
            <div className="space-y-1">
              <h3
                id={drawerTitleId}
                data-testid="mobile-confirmation-title"
                className="text-base font-semibold text-popover-foreground"
              >
                Send back for changes
              </h3>
              <p
                id={drawerDescriptionId}
                data-testid="mobile-confirmation-body"
                className="text-sm text-muted-foreground"
              >
                Tell the agent what to change before the workflow continues.
              </p>
            </div>
            <div>
              <label
                htmlFor="mobile-send-back-body"
                className="text-xs font-medium text-popover-foreground"
              >
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
                onClick={closeDrawer}
                disabled={mutations.sendBackMutation.isPending}
                data-testid="mobile-confirmation-cancel"
              >
                Cancel
              </Button>
              <Button
                type="button"
                size="sm"
                onClick={submitSendBack}
                disabled={
                  !sendBackText.trim()
                  || !decision.approvalStage
                  || mutations.sendBackMutation.isPending
                }
                data-testid="mobile-confirmation-confirm"
              >
                {mutations.sendBackMutation.isPending ? 'Sending back...' : 'Submit feedback'}
              </Button>
            </div>
            {actionError && (
              <div
                role="alert"
                aria-live="polite"
                data-testid="mobile-action-error"
                className="rounded-md border border-danger-border bg-danger-subtle px-3 py-2 text-xs text-danger"
              >
                {actionError.message}
              </div>
            )}
          </div>
        )}
      </ConfirmationDrawer>
    </>
  )
}
