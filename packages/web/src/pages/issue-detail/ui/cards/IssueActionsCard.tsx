import { BotIcon } from 'lucide-react'
import { Button } from '@/shared/ui/components/button'
import { CardSection } from '@/shared/ui/components/card-section'
import { DraftPill } from '../pills'
import { formatRelativeTime } from '../../model/format'
import { useConfirmOutsideClick } from '../../model/useConfirmOutsideClick'
import type { ComputedActionsState } from '../../model/actionsState'
import type { IssueDetailMutations } from '../../model/useIssueDetailMutations'

export interface IssueActionsCardProps {
  state: ComputedActionsState
  mutations: Pick<
    IssueDetailMutations,
    | 'startMutation'
    | 'markReadyMutation'
    | 'closeMutation'
    | 'forceStopMutation'
    | 'stopMutation'
    | 'reopenMutation'
    | 'resumeMutation'
    | 'retryMutation'
    | 'rerunMutation'
  >
  confirmState: {
    forceStopConfirming: boolean
    setForceStopConfirming: (value: boolean) => void
    stopConfirming: boolean
    setStopConfirming: (value: boolean) => void
  }
  onAskAgent: () => void
}

export function IssueActionsCard({
  state,
  mutations,
  confirmState,
  onAskAgent,
}: IssueActionsCardProps) {
  const {
    startMutation,
    markReadyMutation,
    closeMutation,
    forceStopMutation,
    stopMutation,
    rerunMutation,
    resumeMutation,
    retryMutation,
  } = mutations

  const {
    forceStopConfirming,
    setForceStopConfirming,
    stopConfirming,
    setStopConfirming,
  } = confirmState

  const forceStopPanelRef = useConfirmOutsideClick({
    confirming: forceStopConfirming,
    setConfirming: setForceStopConfirming,
  })
  const stopPanelRef = useConfirmOutsideClick({
    confirming: stopConfirming,
    setConfirming: setStopConfirming,
  })

  const {
    showArchivedNote,
    startVariant,
    showForceStopPanel,
    forceStopContext,
    blockedActions,
    showStandaloneRerun,
    showClose,
    showError,
    errorMessages,
    showOtherAgents,
    otherAgentsCount,
  } = state

  return (
    <CardSection title="Actions">
      <div className="space-y-2">
        {showArchivedNote && (
          <div
            data-testid="archived-actions-note"
            className="rounded-md bg-slate-50 border border-slate-200 px-3 py-2 text-xs text-slate-700"
          >
            This issue is archived. Active workflow controls (start, stop, retry, rerun, resume, force stop) are not available because the workflow is no longer running. The execution history is preserved above.
          </div>
        )}

        {startVariant?.kind === 'draft' && (
          <div
            data-testid="start-readiness"
            data-blocker="draft"
            className="rounded-md bg-muted border border-border px-3 py-2 text-sm text-muted-foreground"
          >
            <div className="flex items-center gap-2 mb-1">
              <DraftPill />
              <span className="text-xs font-semibold uppercase tracking-wide">
                Still a draft
              </span>
            </div>
            <p className="text-xs">
              This issue has not been marked ready yet. Mark it ready to enable Start.
            </p>
            <Button
              data-testid="mark-ready-button"
              onClick={() => markReadyMutation.mutate()}
              disabled={markReadyMutation.isPending}
              className="w-full mt-2"
            >
              {markReadyMutation.isPending ? 'Marking ready...' : 'Mark ready'}
            </Button>
            {markReadyMutation.error && (
              <p className="mt-2 text-xs text-red-600">
                {markReadyMutation.error.message}
              </p>
            )}
          </div>
        )}

        {startVariant?.kind === 'waiting-for' && (
          <div
            data-testid="start-readiness"
            data-blocker="waiting-for"
            data-waiting-for={startVariant.issue.number}
            className="rounded-md bg-amber-50 border border-amber-200 px-3 py-2 text-sm text-amber-700"
          >
            <div className="font-medium">
              Waiting for #{startVariant.issue.number}
              {startVariant.issue.title ? ` ${startVariant.issue.title}` : ''}
            </div>
            <p className="text-xs mt-0.5">
              This issue cannot start until its prerequisite is delivered.
            </p>
            <Button
              data-testid="start-button"
              disabled
              className="w-full mt-2"
              title={`Waiting for prerequisite #${startVariant.issue.number}`}
            >
              Waiting for #{startVariant.issue.number}
            </Button>
          </div>
        )}

        {startVariant?.kind === 'ready' && (
          <div className="space-y-2">
            {startVariant.runnerUnavailable && (
              <div className="rounded-md border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-700">
                {startVariant.runnerMessage ?? 'No runner is connected. Start a runner before starting workflow work.'}
              </div>
            )}
            <Button
              data-testid="start-button"
              onClick={() => startMutation.mutate()}
              disabled={startVariant.runnerUnavailable || startVariant.isAgentRunningOnThis || startVariant.isCapacityFull || startMutation.isPending}
              className="w-full"
            >
              {startMutation.isPending
                ? 'Starting...'
                : startVariant.runnerUnavailable
                  ? 'Runner unavailable'
                  : startVariant.isAgentRunningOnThis
                    ? 'Agent running...'
                    : startVariant.isCapacityFull
                      ? 'Capacity full...'
                      : 'Start'}
            </Button>
          </div>
        )}

        {showClose && (
          <Button
            variant="outline"
            onClick={() => closeMutation.mutate()}
            disabled={closeMutation.isPending}
            className="w-full"
          >
            {closeMutation.isPending ? 'Closing...' : 'Close'}
          </Button>
        )}

        {showForceStopPanel && forceStopContext && (
          <div ref={forceStopPanelRef} className="rounded-lg border border-blue-200 bg-blue-50 p-3 space-y-2">
            <div className="flex items-center gap-2">
              <span className="inline-block h-2 w-2 rounded-full bg-blue-500 animate-pulse" />
              <span className="text-xs font-semibold text-blue-800">
                {forceStopContext.agentProgress
                  ? `${forceStopContext.agentProgress.stage.charAt(0).toUpperCase() + forceStopContext.agentProgress.stage.slice(1)} Stage`
                  : forceStopContext.recoveryCanWait
                    ? 'Waiting for running work'
                    : 'Running...'}
              </span>
            </div>
            {forceStopContext.recoveryAttemptState === 'running' && forceStopContext.currentWorkItem && (
              <div className="text-xs text-blue-700">
                Current: {forceStopContext.currentWorkItem.type} — {forceStopContext.currentWorkItem.title}
              </div>
            )}
            {forceStopContext.agentProgress?.roundType && (
              <div className="text-xs text-blue-700">
                Round: {forceStopContext.agentProgress.roundType} #{(forceStopContext.agentProgress.roundIndex ?? 0) + 1}
              </div>
            )}
            {forceStopContext.agentProgress?.taskProgress && (
              <div className="text-xs text-blue-700">
                Tasks: {forceStopContext.agentProgress.taskProgress.completed}/{forceStopContext.agentProgress.taskProgress.total}
              </div>
            )}
            {forceStopContext.agentProgress?.lastActivityAt && (
              <div className="text-xs text-blue-600">
                Last activity: {formatRelativeTime(forceStopContext.agentProgress.lastActivityAt)}
              </div>
            )}
            {forceStopContext.recoveryCanStop && (
              <Button
                onClick={() => {
                  if (forceStopConfirming) {
                    forceStopMutation.mutate()
                  } else {
                    setForceStopConfirming(true)
                  }
                }}
                disabled={forceStopMutation.isPending}
                variant={forceStopConfirming ? 'destructive' : 'outline'}
                className={`w-full ${
                  forceStopConfirming
                    ? ''
                    : 'border-red-300 text-red-600 hover:bg-red-50'
                }`}
              >
                {forceStopMutation.isPending
                  ? 'Stopping...'
                  : forceStopConfirming
                    ? 'Confirm Force Stop'
                    : 'Force Stop'}
              </Button>
            )}
            {forceStopMutation.error && (
              <div className="text-xs text-red-600">
                {forceStopMutation.error.message}
              </div>
            )}
          </div>
        )}

        {(blockedActions.showBlockedReason
          || blockedActions.isInterrupted
          || blockedActions.showRetry
          || blockedActions.showResume
          || blockedActions.showRerun
          || blockedActions.showStop
          || blockedActions.showInspectCurrent) && (
          <div className="space-y-2">
            {blockedActions.showBlockedReason && (
              <div className="rounded-md bg-red-50 border border-red-200 px-3 py-2 text-sm text-red-700">
                {blockedActions.blockedReason}
              </div>
            )}
            {blockedActions.isInterrupted && (
              <div className="rounded-md bg-orange-50 border border-orange-200 px-3 py-2 text-xs text-orange-700">
                Execution was interrupted. This is not a failed result — the work item can be resumed or rerun.
              </div>
            )}
            {blockedActions.showProjectedCheckRepair ? null : (
              <>
                {blockedActions.showRetry && (
                  <Button
                    variant="destructive"
                    onClick={() => retryMutation.mutate()}
                    disabled={retryMutation.isPending}
                    className="w-full"
                  >
                    {retryMutation.isPending ? 'Retrying...' : 'Retry'}
                  </Button>
                )}
                {blockedActions.showResume && (
                  <Button
                    onClick={() => resumeMutation.mutate()}
                    disabled={resumeMutation.isPending}
                    className="w-full bg-orange-500 hover:bg-orange-600 text-white"
                  >
                    {resumeMutation.isPending ? 'Resuming...' : 'Resume'}
                  </Button>
                )}
                {blockedActions.showRerun && (
                  <Button
                    variant="outline"
                    onClick={() => rerunMutation.mutate()}
                    disabled={rerunMutation.isPending}
                    className="w-full"
                  >
                    {rerunMutation.isPending ? 'Rerunning...' : 'Rerun Stage'}
                  </Button>
                )}
                {blockedActions.showStop && (
                  <div ref={stopPanelRef} className="rounded-md border border-red-200 bg-red-50 p-3 space-y-2">
                    <div className="text-xs text-red-700">
                      Stop is terminal: the workflow run will be permanently stopped and cannot be resumed. The issue itself is not closed.
                    </div>
                    <Button
                      onClick={() => {
                        if (stopConfirming) {
                          stopMutation.mutate()
                        } else {
                          setStopConfirming(true)
                        }
                      }}
                      disabled={stopMutation.isPending}
                      variant={stopConfirming ? 'destructive' : 'outline'}
                      className="w-full border-red-300 text-red-600 hover:bg-red-50"
                    >
                      {stopMutation.isPending
                        ? 'Stopping...'
                        : stopConfirming
                          ? 'Confirm Stop'
                          : 'Stop Workflow'}
                    </Button>
                    {stopMutation.error && (
                      <div className="text-xs text-red-600">
                        {stopMutation.error.message}
                      </div>
                    )}
                  </div>
                )}
                {blockedActions.showInspectCurrent && blockedActions.currentWorkItem && (
                  <div className="text-xs text-muted-foreground">
                    Current: {blockedActions.currentWorkItem.type} — {blockedActions.currentWorkItem.title}
                  </div>
                )}
              </>
            )}
          </div>
        )}

        {showStandaloneRerun && (
          <Button
            variant="outline"
            onClick={() => rerunMutation.mutate()}
            disabled={rerunMutation.isPending}
            className="w-full"
          >
            {rerunMutation.isPending ? 'Rerunning...' : 'Rerun Stage'}
          </Button>
        )}

        {showError && (
          <div className="rounded-md bg-red-50 px-3 py-2 text-xs text-red-600">
            {errorMessages.closeError?.message ||
              errorMessages.reopenError?.message ||
              errorMessages.startError?.message ||
              errorMessages.rerunError?.message ||
              errorMessages.retryError?.message ||
              ''}
          </div>
        )}

        {showOtherAgents && (
          <div className="text-xs text-muted-foreground text-center">
            {otherAgentsCount} agent{otherAgentsCount > 1 ? 's' : ''} running on other issues
          </div>
        )}

        <div className="border-t border-border/60 pt-2">
          <Button
            variant="outline"
            onClick={onAskAgent}
            className="w-full"
            data-testid="ask-agent-issue"
          >
            <BotIcon className="size-4 mr-2" />
            Ask Agent
          </Button>
        </div>
      </div>
    </CardSection>
  )
}

export function extractActionsErrorMessages(mutations: Pick<
  IssueDetailMutations,
  'startMutation' | 'markReadyMutation' | 'closeMutation' | 'forceStopMutation' | 'stopMutation' | 'reopenMutation' | 'resumeMutation' | 'retryMutation' | 'rerunMutation'
>) {
  return {
    closeError: mutations.closeMutation.error,
    reopenError: mutations.reopenMutation.error,
    startError: mutations.startMutation.error,
    rerunError: mutations.rerunMutation.error,
    retryError: mutations.retryMutation.error,
  }
}
