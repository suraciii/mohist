import type { StageCheckState } from '../../../entities/issue'
import { formatDuration, formatOriginLabel, formatOriginTitle } from './format'
import { CheckmarkIcon, CrossIcon, EmptyCircleIcon, HourglassIcon, SpinnerIcon } from './StageStatusIcons'
import { isScriptHealthCheck } from '../model/runtime-query-helpers'

export function CheckItem({ check, attemptLabel }: { check: StageCheckState; attemptLabel?: string }) {
  const isPending = check.status === 'pending'
  const isRecoverableInterrupted = check.status === 'recoverable-interrupted'
  const isFailed = check.status === 'failed' || check.status === 'error'
  const isHealthCheck = isScriptHealthCheck(check)
  const healthOutput = check.output as
    | {
        command?: string
        duration?: number
        summary?: string
        logExcerpt?: string
        enabled?: boolean
        exitCode?: number
        timedOut?: boolean
      }
    | undefined

  let icon: React.ReactNode
  if (check.status === 'completed' || check.status === 'passed') {
    icon = <CheckmarkIcon className="h-4 w-4 text-success flex-shrink-0" />
  } else if (isFailed) {
    icon = <CrossIcon className="h-4 w-4 text-danger flex-shrink-0" />
  } else if (isRecoverableInterrupted) {
    icon = <HourglassIcon className="h-4 w-4 text-warning flex-shrink-0" />
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
      className={`flex items-center gap-2 px-3 py-2 rounded-md border ${isHealthCheck && isFailed ? 'border-danger-border bg-danger-subtle' : isRecoverableInterrupted ? 'border-warning-border bg-warning-subtle' : 'border-border bg-card'} ${isPending ? 'opacity-50' : ''}`}
    >
      {icon}
      <span className="text-sm text-card-foreground flex-1 truncate">{displayName}</span>
      {isFailed && check.message && (
        <span className="text-xs text-danger flex-shrink-0 truncate max-w-48">{check.message}</span>
      )}
      {isRecoverableInterrupted && check.interruption && (
        <span
          className="text-xs text-warning flex-shrink-0 truncate max-w-48"
          title={`${check.interruption.reasonCode}; recovery deadline ${check.interruption.recoveryDeadlineAt}`}
        >
          recoverable-interrupted: {check.interruption.reasonCode}
        </span>
      )}
      {originLabel && (
        <span className="text-[11px] text-muted-foreground flex-shrink-0 font-mono" title={originTitle}>
          {originLabel}
        </span>
      )}
      {isHealthCheck && healthOutput && (
        <>
          {healthOutput.command && (
            <span
              className="text-xs text-muted-foreground flex-shrink-0 font-mono truncate max-w-32"
              title={healthOutput.command}
            >
              {healthOutput.command}
            </span>
          )}
          {healthOutput.duration != null && (
            <span className="text-xs text-muted-foreground flex-shrink-0">{formatDuration(healthOutput.duration)}</span>
          )}
          {isFailed && healthOutput.summary && (
            <span className="text-xs text-danger flex-shrink-0 truncate max-w-48" title={healthOutput.summary}>
              {healthOutput.summary}
            </span>
          )}
        </>
      )}
    </div>
  )
}
