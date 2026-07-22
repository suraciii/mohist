import { WorkflowStage } from '../../../entities/issue'
import { ApiError } from '../../../shared/api/client'
import { cn } from '@/shared/lib/utils'
import { Button } from '@/shared/ui/components/button'
import { useRebaseRecovery } from '../model/useRebaseRecovery'
import type { RebaseRecovery } from '../model/useRebaseRecovery'

interface BranchBarProps {
  issueNumber: number
  stage: string | null
  isAgentRunning: boolean
  baseBranch?: string | null
  allowRebase?: boolean
}

const REBASE_REASON_ID = 'branch-bar-rebase-reason'

function getRebaseUnavailableReason(rebase: RebaseRecovery, allowRebase: boolean): string | null {
  if (rebase.isConflictResolving) return 'Rebase is unavailable while conflict resolution is in progress.'
  if (rebase.isQueued || rebase.isPending || rebase.isRebasing) return 'Rebase is unavailable while another rebase is in progress.'
  if (rebase.workspace.isUpstreamUnknown) return 'Branch status could not be checked.'
  if (rebase.workspace.isChecking || !rebase.workspace.hasAheadBehind) return 'Branch status is still being checked.'
  if (!allowRebase) return 'Rebase is unavailable for this issue.'
  if (!rebase.canRequest) return 'Rebase is unavailable right now.'
  return null
}

function RebaseReason({ reason, className }: { reason: string | null; className: string }) {
  if (!reason) return null

  return (
    <p id={REBASE_REASON_ID} data-testid="branch-bar-rebase-reason" className={className}>
      {reason}
    </p>
  )
}

function RebaseAction({
  baseBranch,
  rebase,
  allowRebase,
  enabledClassName,
  reasonClassName,
}: {
  baseBranch: string
  rebase: RebaseRecovery
  allowRebase: boolean
  enabledClassName: string
  reasonClassName: string
}) {
  const canRequestRebase = rebase.canRequest && allowRebase
  const reason = getRebaseUnavailableReason(rebase, allowRebase)

  return (
    <div className="flex w-full flex-col items-start gap-1 sm:w-auto sm:items-end">
      <Button
        variant="outline"
        onClick={rebase.trigger}
        disabled={!canRequestRebase}
        aria-describedby={reason ? REBASE_REASON_ID : undefined}
        title={reason ?? undefined}
        className={cn(
          'h-auto w-full rounded-md px-3 py-1.5 text-xs font-medium transition-colors sm:w-auto',
          canRequestRebase ? enabledClassName : 'border-border bg-muted text-muted-foreground hover:bg-muted',
        )}
      >
        Rebase onto {baseBranch}
      </Button>
      <RebaseReason reason={reason} className={reasonClassName} />
    </div>
  )
}

export function BranchBar({ issueNumber, stage, baseBranch: fallbackBaseBranch, allowRebase = false }: BranchBarProps) {
  const rebase = useRebaseRecovery(issueNumber)
  const { workspace } = rebase

  if (!workspace.isChecking && (!workspace.data || !workspace.data.exists) && !allowRebase) return null

  const isUpstreamUnknown = workspace.isUpstreamUnknown
  const isBehind = workspace.isBehind
  const rebaseUnavailableReason = getRebaseUnavailableReason(rebase, allowRebase)
  const baseBranch = workspace.data?.baseBranch ?? fallbackBaseBranch ?? 'master'
  const isDone = stage === WorkflowStage.Done

  if (rebase.isRebasing) {
    return (
      <div className="mb-8" data-testid="branch-bar-frame">
        <div data-testid="branch-bar" className="rounded-lg border border-info-border bg-info-subtle px-4 py-3 space-y-2">
          <div className="flex items-center gap-3">
            {!rebase.isQueued && (
              <svg className="h-4 w-4 animate-spin text-info" viewBox="0 0 24 24" fill="none">
                <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
                <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
              </svg>
            )}
            <span className="text-sm font-medium text-info">
              {rebase.isQueued ? 'Rebase queued' : rebase.isConflictResolving ? 'Resolving conflicts...' : 'Rebasing...'}
            </span>
            <span className="text-xs text-info font-mono">{workspace.branch}</span>
          </div>
          {rebase.hasConflicts && (
            <div className="rounded-md bg-danger-subtle px-3 py-2 text-xs text-danger">
              <span>Conflicting files:</span>
              <ul className="mt-1 ml-3 list-disc">
                {rebase.hasConflicts.map((f) => (
                  <li key={f} className="font-mono">{f}</li>
                ))}
              </ul>
            </div>
          )}
          <RebaseReason reason={rebaseUnavailableReason} className="text-xs text-info" />
        </div>
      </div>
    )
  }

  if (isUpstreamUnknown) {
    return (
      <div className="mb-8" data-testid="branch-bar-frame">
        <div data-testid="branch-bar" className="rounded-lg border border-border bg-muted px-4 py-3">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <div className="flex items-center gap-3 min-w-0">
              <span className="text-sm font-mono font-medium text-foreground truncate">{workspace.branch}</span>
              <span className="text-xs text-muted-foreground shrink-0">onto</span>
              <span className="text-xs font-mono text-muted-foreground/80 shrink-0">{baseBranch}</span>
            </div>
            <div className="flex w-full flex-wrap items-center gap-3 sm:w-auto sm:shrink-0">
              <span className="text-xs font-medium text-muted-foreground">Upstream check unavailable</span>
              {allowRebase && (
                <RebaseAction
                  baseBranch={baseBranch}
                  rebase={rebase}
                  allowRebase={allowRebase}
                  enabledClassName="border-border text-foreground hover:bg-muted"
                  reasonClassName="text-xs text-muted-foreground"
                />
              )}
            </div>
          </div>
        </div>
      </div>
    )
  }

  if (isBehind || allowRebase) {
    return (
      <div className="mb-8" data-testid="branch-bar-frame">
        <div data-testid="branch-bar" className="rounded-lg border border-warning-border bg-warning-subtle px-4 py-3 space-y-2">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <div className="flex min-w-[14rem] flex-1 items-center gap-3">
              <span className="text-sm font-mono font-medium text-foreground truncate">{workspace.branch}</span>
              <span className="text-xs text-muted-foreground shrink-0">onto</span>
              <span className="text-xs font-mono text-muted-foreground/80 shrink-0">{baseBranch}</span>
            </div>
            <div className="flex w-full flex-wrap items-center gap-3 sm:w-auto sm:shrink-0">
              {workspace.isChecking || !workspace.hasAheadBehind ? (
                <span className="text-xs font-medium text-warning">Checking upstream...</span>
              ) : (
                <span className="text-xs font-medium text-warning">
                  {workspace.ahead > 0 && <span className="text-muted-foreground">↑{workspace.ahead} </span>}
                  {workspace.behind > 0 ? <span>↓{workspace.behind} behind</span> : <span>up to date</span>}
                </span>
              )}
              <RebaseAction
                baseBranch={baseBranch}
                rebase={rebase}
                allowRebase={allowRebase}
                enabledClassName="border-warning-border text-warning hover:bg-warning-subtle"
                reasonClassName="text-xs text-warning"
              />
            </div>
          </div>
          {isDone && (
            <p className="text-xs text-warning">
              This Done workflow workspace is retained for review, traceability, diff inspection, and debugging. Archiving will remove the retained workspace.
            </p>
          )}
          {rebase.error && (
            <div className="rounded-md bg-danger-subtle px-3 py-2 text-xs text-danger">
              {rebase.error instanceof ApiError
                ? rebase.error.message
                : 'Rebase failed'}
            </div>
          )}
          {rebase.hasConflicts && !rebase.isConflictFailed && (
            <div className="rounded-md bg-danger-subtle px-3 py-2 text-xs text-danger">
              <span>Conflicting files:</span>
              <ul className="mt-1 ml-3 list-disc">
                {rebase.hasConflicts.map((f) => (
                  <li key={f} className="font-mono">{f}</li>
                ))}
              </ul>
            </div>
          )}
          {rebase.isConflictFailed && (
            <div className="rounded-md bg-danger-subtle px-3 py-2 text-xs text-danger">
              <span>Conflict resolution failed{rebase.rebaseConflict?.error ? `: ${rebase.rebaseConflict.error}` : ''}</span>
            </div>
          )}
        </div>
      </div>
    )
  }

  return (
    <div className="mb-8" data-testid="branch-bar-frame">
      <div data-testid="branch-bar" className="rounded-lg border bg-background px-4 py-3">
        <div className="flex items-center justify-between gap-3">
          <div className="flex items-center gap-3 min-w-0">
            <span className="text-sm font-mono font-medium text-foreground truncate">{workspace.branch}</span>
            <span className="text-xs text-muted-foreground shrink-0">onto</span>
            <span className="text-xs font-mono text-muted-foreground/80 shrink-0">{baseBranch}</span>
          </div>
          <div className="flex items-center gap-3 shrink-0">
            {workspace.ahead > 0 && <span className="text-xs text-muted-foreground">↑{workspace.ahead} ahead</span>}
            <span className="text-xs font-medium text-success">up to date</span>
          </div>
        </div>
        {isDone && (
          <p className="mt-2 text-xs text-muted-foreground">
            This Done workflow workspace is retained for review, traceability, diff inspection, and debugging. Archiving will remove the retained workspace.
          </p>
        )}
      </div>
    </div>
  )
}
