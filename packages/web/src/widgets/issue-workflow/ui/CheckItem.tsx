import type { StageCheckState, CheckRepairState, CheckRepairStatus } from '../../../entities/issue'
import { formatDuration, formatOriginLabel, formatOriginTitle } from './format'
import { CheckmarkIcon, CrossIcon, EmptyCircleIcon, SpinnerIcon } from './StageStatusIcons'

function isScriptHealthCheck(check: StageCheckState): boolean {
  const output = check.output as { kind?: string } | undefined
  return check.checkName === 'health' || output?.kind === 'script'
}

export function CheckItem({ check, attemptLabel }: { check: StageCheckState; attemptLabel?: string }) {
  const isPending = check.status === 'pending'
  const isFailed = check.status === 'failed' || check.status === 'error'
  const isHealthCheck = isScriptHealthCheck(check)
  const healthOutput = check.output as { command?: string; duration?: number; summary?: string; logExcerpt?: string; enabled?: boolean; exitCode?: number; timedOut?: boolean } | undefined

  let icon: React.ReactNode
  if (check.status === 'completed' || check.status === 'passed') {
    icon = <CheckmarkIcon className="h-4 w-4 text-success flex-shrink-0" />
  } else if (isFailed) {
    icon = <CrossIcon className="h-4 w-4 text-danger flex-shrink-0" />
  } else if (check.status === 'running') {
    icon = <SpinnerIcon className="h-4 w-4 text-info animate-spin flex-shrink-0" />
  } else {
    icon = <EmptyCircleIcon className="h-4 w-4 text-muted-foreground/40 flex-shrink-0" />
  }

  const fallbackName = isHealthCheck ? 'Health check' : check.checkName
  const baseName = check.title?.trim() || fallbackName
  const displayName = attemptLabel ? `${baseName} (${attemptLabel})` : baseName
  const originLabel = formatOriginLabel(check.origin)
  const originTitle = formatOriginTitle(check.origin)

  return (
    <div
      className={`flex items-center gap-2 px-3 py-2 rounded-md border ${isHealthCheck && isFailed ? 'border-danger-border bg-danger-subtle' : 'border-border bg-card'} ${isPending ? 'opacity-50' : ''}`}
    >
      {icon}
      <span className="text-sm text-card-foreground flex-1 truncate">{displayName}</span>
      {isFailed && check.message && (
        <span className="text-xs text-danger flex-shrink-0 truncate max-w-48">{check.message}</span>
      )}
      {originLabel && (
        <span className="text-[11px] text-muted-foreground flex-shrink-0 font-mono" title={originTitle}>{originLabel}</span>
      )}
      {isHealthCheck && healthOutput && (
        <>
          {healthOutput.command && (
            <span className="text-xs text-muted-foreground flex-shrink-0 font-mono truncate max-w-32" title={healthOutput.command}>{healthOutput.command}</span>
          )}
          {healthOutput.duration != null && (
            <span className="text-xs text-muted-foreground flex-shrink-0">{formatDuration(healthOutput.duration)}</span>
          )}
          {isFailed && healthOutput.summary && (
            <span className="text-xs text-danger flex-shrink-0 truncate max-w-48" title={healthOutput.summary}>{healthOutput.summary}</span>
          )}
        </>
      )}
    </div>
  )
}

export function CheckRepairPanel({ checkRepair }: { checkRepair: CheckRepairState }) {
  const statusLabels: Record<CheckRepairStatus, string> = {
    'not-needed': 'Repair not needed',
    'available': 'Auto-fix available',
    'pending': 'Repair pending',
    'running': 'Repair running',
    'completed': 'Repair completed',
    'exhausted': 'Auto-fix exhausted',
  }

  const stopReasonLabels: Record<string, string> = {
    'review-passed': 'Review passed',
    'repair-pending': 'Waiting for repair to start',
    'repair-running': 'Repair in progress',
    'max-repair-attempts-reached': 'Max repair attempts reached',
    'manual-rerun-required': 'Manual review required',
  }

  return (
    <div className="rounded-lg border border-danger-border bg-danger-subtle p-4 space-y-3">
      <div className="flex items-center gap-2">
        <CrossIcon className="h-4 w-4 text-danger" />
        <span className="text-sm font-semibold text-danger">Check failed: {checkRepair.checkName}</span>
      </div>

      <div className="space-y-1.5 text-xs text-danger">
        <div className="flex items-center justify-between">
          <span className="font-medium">Auto-fix status:</span>
          <span className={checkRepair.status === 'exhausted' ? 'text-danger font-medium' : ''}>
            {statusLabels[checkRepair.status] ?? checkRepair.status}
          </span>
        </div>

        <div className="flex items-center justify-between">
          <span className="font-medium">Attempts:</span>
          <span>
            {checkRepair.attemptsUsed} used, {checkRepair.attemptsRemaining} remaining (max {checkRepair.attemptsMax})
          </span>
        </div>

        {checkRepair.lastRepairStatus && (
          <div className="flex items-center justify-between">
            <span className="font-medium">Last repair:</span>
            <span className={checkRepair.lastRepairStatus === 'completed' ? 'text-success' : ''}>
              {checkRepair.lastRepairStatus === 'completed' ? 'completed' : checkRepair.lastRepairStatus}
              {checkRepair.followUpReviewStatus === 'failed' && ' — follow-up check failed'}
            </span>
          </div>
        )}

        {checkRepair.followUpReviewStatus && (
          <div className="flex items-center justify-between">
            <span className="font-medium">Follow-up check:</span>
            <span className={checkRepair.followUpReviewStatus === 'failed' ? 'text-danger' : checkRepair.followUpReviewStatus === 'passed' ? 'text-success' : ''}>
              {checkRepair.followUpReviewStatus}
            </span>
          </div>
        )}

        {checkRepair.stopReason && (
          <div className="flex items-center justify-between">
            <span className="font-medium">Stop reason:</span>
            <span>{stopReasonLabels[checkRepair.stopReason] ?? checkRepair.stopReason}</span>
          </div>
        )}

        {checkRepair.unresolvedSummary && (
          <div className="mt-2 rounded border border-danger-border bg-card/70 p-2">
            <div className="font-medium text-danger mb-1">Unresolved findings:</div>
            <div className="text-danger whitespace-pre-wrap">{checkRepair.unresolvedSummary}</div>
          </div>
        )}
      </div>

      {checkRepair.status === 'exhausted' && (
        <div className="pt-2 border-t border-danger-border">
          <p className="text-xs text-danger">
            Auto-fix will not continue automatically. You can rerun this stage after making code changes, or take over manually.
          </p>
        </div>
      )}
    </div>
  )
}