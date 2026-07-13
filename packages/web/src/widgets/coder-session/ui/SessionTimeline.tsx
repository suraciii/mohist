import { useState } from 'react'
import { Button } from '@/shared/ui/components/button'
import type { ToolCallEntry, LoopProgress } from '../../../entities/coder-session'
import type { WorkflowStage } from '../../../entities/issue'
import type { Round, RecoveryEvent, RecoveryStatus, PlanProgress, ContextHealthState } from '../model/useSessionTimeline'
import { deriveToolCallTitle } from '../model/useSessionTimeline'
import { PlanProgressPanel } from './PlanProgressPanel'
import { ContextHealthBar } from './session-health/ContextHealthBar'
import { CompactionTimelineEntry } from './session-health/CompactionTimelineEntry'
import { CompactionCompactSummary } from './session-health/CompactionCompactSummary'

interface SessionTimelineProps {
  rounds: Round[]
  isStreaming: boolean
  isLoading: boolean
  currentStage: WorkflowStage | string
  isLive: boolean
  recoveryStatus: RecoveryStatus | null
  planProgress: PlanProgress | null
  contextHealth?: ContextHealthState | null
  onCompact?: () => void
  onReset?: () => void
}

function formatDuration(ms: number): string {
  if (ms < 1000) return `${ms}ms`
  if (ms < 60000) return `${(ms / 1000).toFixed(1)}s`
  return `${Math.floor(ms / 60000)}m ${Math.round((ms % 60000) / 1000)}s`
}

function tryFormatJson(text: string): string {
  try {
    const parsed = JSON.parse(text)
    return JSON.stringify(parsed, null, 2)
  } catch {
    return text
  }
}

function truncate(text: string, max: number): string {
  if (text.length <= max) return text
  return text.slice(0, max) + '\n... (truncated)'
}

function StatusIcon({ state }: { state: ToolCallEntry['state'] }) {
  if (state === 'started') {
    return (
      <svg className="h-3.5 w-3.5 text-info animate-spin" viewBox="0 0 24 24" fill="none">
        <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
        <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
      </svg>
    )
  }
  if (state === 'completed') {
    return (
      <svg className="h-3.5 w-3.5 text-success" viewBox="0 0 20 20" fill="currentColor">
        <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.857-9.809a.75.75 0 00-1.214-.882l-3.483 4.79-1.88-1.88a.75.75 0 10-1.06 1.061l2.5 2.5a.75.75 0 001.137-.089l4-5.5z" clipRule="evenodd" />
      </svg>
    )
  }
  return (
    <svg className="h-3.5 w-3.5 text-danger" viewBox="0 0 20 20" fill="currentColor">
      <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zM8.28 7.22a.75.75 0 00-1.06 1.06L8.94 10l-1.72 1.72a.75.75 0 101.06 1.06L10 11.06l1.72 1.72a.75.75 0 101.06-1.06L11.06 10l1.72-1.72a.75.75 0 00-1.06-1.06L10 8.94 8.28 7.22z" clipRule="evenodd" />
    </svg>
  )
}

export function ToolCallTimelineEntry({ entry }: { entry: ToolCallEntry }) {
  const [expanded, setExpanded] = useState(false)

  const displayInput = entry.rawInput ?? entry.args
  const displayOutput = entry.rawOutput ?? entry.result
  const displayTitle = deriveToolCallTitle(entry.toolName, entry.title, entry.rawInput)

  return (
    <div className="flex gap-2">
      <div className="flex flex-col items-center shrink-0 pt-0.5">
        <StatusIcon state={entry.state} />
        <div className="w-px flex-1 bg-muted mt-1" />
      </div>
      <div className="flex-1 min-w-0 pb-3">
        <Button
          variant="ghost"
          size="sm"
          onClick={() => entry.state !== 'started' && setExpanded(!expanded)}
          className={`flex h-auto items-center justify-start gap-2 w-full text-left py-0 ${entry.state !== 'started' ? 'cursor-pointer hover:bg-muted rounded px-1 -mx-1' : 'cursor-default px-0'}`}
        >
          <span className="font-mono text-xs text-foreground">
            {entry.toolName}
          </span>
          {displayTitle !== entry.toolName && (
            <span className="text-xs text-muted-foreground truncate">
              {displayTitle}
            </span>
          )}
          {entry.state === 'started' && (
            <span className="text-xs text-info">running...</span>
          )}
          {entry.duration != null && entry.state !== 'started' && (
            <span className="text-xs text-muted-foreground/70">{formatDuration(entry.duration)}</span>
          )}
          {entry.state === 'failed' && entry.error && (
            <span className="text-xs text-danger truncate">{entry.error}</span>
          )}
          {entry.state !== 'started' && (
            <svg
              className={`h-3 w-3 text-muted-foreground/70 shrink-0 transition-transform ml-auto ${expanded ? 'rotate-90' : ''}`}
              viewBox="0 0 20 20"
              fill="currentColor"
            >
              <path fillRule="evenodd" d="M7.21 14.77a.75.75 0 01.02-1.06L11.168 10 7.23 6.29a.75.75 0 111.04-1.08l4.5 4.25a.75.75 0 010 1.08l-4.5 4.25a.75.75 0 01-1.06-.02z" clipRule="evenodd" />
            </svg>
          )}
        </Button>

        {expanded && (
          <div className="mt-1.5 space-y-1.5 text-xs">
            {displayInput && (
              <div>
                <div className="font-medium text-muted-foreground mb-0.5">Input</div>
                <pre className="whitespace-pre-wrap break-all text-foreground bg-muted rounded p-2 max-h-32 overflow-auto">
                  {tryFormatJson(typeof displayInput === 'string' ? displayInput : JSON.stringify(displayInput))}
                </pre>
              </div>
            )}
            {displayOutput && (
              <div>
                <div className="font-medium text-muted-foreground mb-0.5">Output</div>
                <pre className="whitespace-pre-wrap break-all text-foreground bg-muted rounded p-2 max-h-48 overflow-auto">
                  {truncate(tryFormatJson(typeof displayOutput === 'string' ? displayOutput : JSON.stringify(displayOutput)), 2000)}
                </pre>
              </div>
            )}
          </div>
        )}
      </div>
    </div>
  )
}

const WORKFLOW_STAGES = [
  { key: 'plan', label: 'Plan' },
  { key: 'build', label: 'Build' },
  { key: 'check', label: 'Check' },
  { key: 'integrate', label: 'Integrate' },
]

export function WorkflowStatusTimeline({ currentStage }: { currentStage: string }) {
  const stageOrder = ['backlog', 'plan', 'build', 'check', 'integrate']
  const currentIndex = currentStage === 'done'
    ? stageOrder.length
    : stageOrder.indexOf(currentStage)

  return (
    <div className="flex items-center gap-1 mb-4">
      {WORKFLOW_STAGES.map((stage, i) => {
        const stageIdx = stageOrder.indexOf(stage.key)
        const isCompleted = currentIndex > stageIdx
        const isCurrent = currentIndex === stageIdx
        const stageState = isCompleted ? 'completed' : isCurrent ? 'current' : 'pending'

        return (
          <div
            key={stage.key}
            className="flex items-center gap-1"
            data-testid={`workflow-status-stage-${stage.key}`}
            data-state={stageState}
          >
            {i > 0 && (
              <div className={`h-0.5 w-4 ${isCompleted || isCurrent ? 'bg-info-subtle0' : 'bg-muted'}`} />
            )}
            <div className="flex items-center gap-1">
              {isCompleted ? (
                <svg className="h-3.5 w-3.5 text-success" viewBox="0 0 20 20" fill="currentColor">
                  <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.857-9.809a.75.75 0 00-1.214-.882l-3.483 4.79-1.88-1.88a.75.75 0 10-1.06 1.061l2.5 2.5a.75.75 0 001.137-.089l4-5.5z" clipRule="evenodd" />
                </svg>
              ) : isCurrent ? (
                <span className="inline-block h-2.5 w-2.5 rounded-full bg-info-subtle0 animate-pulse" />
              ) : (
                <span className="inline-block h-2 w-2 rounded-full bg-muted" />
              )}
              <span className={`text-xs ${isCompleted || isCurrent ? 'text-info font-medium' : 'text-muted-foreground/70'}`}>
                {stage.label}
              </span>
            </div>
          </div>
        )
      })}
    </div>
  )
}

function isBuildRoundLabel(label: string): boolean {
  return /^\[T-\d+\]/.test(label) || /^T-\d+/.test(label)
}

function getRoundColor(round: Round, isLive: boolean) {
  if (isBuildRoundLabel(round.label)) {
    return {
      dot: 'bg-info',
      border: 'border-info-border',
      bg: 'bg-info-subtle/30',
      text: 'text-info',
      labelBg: 'bg-info-subtle',
    }
  }
  if (isLive && !round.completedAt) {
    return {
      dot: 'bg-info-subtle0 animate-pulse',
      border: 'border-info-border',
      bg: 'bg-info-subtle/30',
      text: 'text-info',
      labelBg: 'bg-info-subtle',
    }
  }
  return {
    dot: 'bg-muted-foreground/60',
    border: 'border-border',
    bg: 'bg-background',
    text: 'text-foreground',
    labelBg: 'bg-muted',
  }
}

export function RoundSection({
  round,
  isLive,
  isStreaming,
}: {
  round: Round
  isLive: boolean
  isStreaming: boolean
}) {
  const [expanded, setExpanded] = useState(true)
  const colors = getRoundColor(round, isLive)
  const isLiveRound = isLive && !round.completedAt
  const hasContent = round.agentText || round.toolCalls.length > 0 || round.recoveryEvents.length > 0 || round.compactions.length > 0

  return (
    <div className={`rounded-lg border ${colors.border} ${colors.bg}`}>
      <Button
        variant="ghost"
        size="sm"
        onClick={() => setExpanded(!expanded)}
        className="flex h-auto items-center justify-start gap-2 w-full text-left px-3 py-2 hover:bg-muted/50 rounded-t-lg"
      >
        <svg
          className={`h-3 w-3 text-muted-foreground/70 shrink-0 transition-transform ${expanded ? 'rotate-90' : ''}`}
          viewBox="0 0 20 20"
          fill="currentColor"
        >
          <path fillRule="evenodd" d="M7.21 14.77a.75.75 0 01.02-1.06L11.168 10 7.23 6.29a.75.75 0 111.04-1.08l4.5 4.25a.75.75 0 010 1.08l-4.5 4.25a.75.75 0 01-1.06-.02z" clipRule="evenodd" />
        </svg>
        <span className={`inline-block h-2 w-2 rounded-full ${colors.dot}`} />
        <span className={`text-xs font-medium ${colors.text}`}>{round.label}</span>
        {round.startedAt && (
          <span className="text-xs text-muted-foreground/70">
            {new Date(round.startedAt).toLocaleTimeString()}
          </span>
        )}
        {isLiveRound && (
          <span className="text-xs text-info ml-auto">Live</span>
        )}
        {!round.completedAt && !isLiveRound && round.agentText && (
          <span className="text-xs text-muted-foreground/70 ml-auto">In progress</span>
        )}
      </Button>

      {expanded && (
        <div className="px-3 pb-3 space-y-2 border-t border-border">
          {round.agentText && (
            <div className="text-sm text-foreground whitespace-pre-wrap leading-relaxed pt-2">
              {round.agentText}
              {isLiveRound && isStreaming && (
                <span className="inline-block w-1.5 h-4 bg-info-subtle0 ml-0.5 animate-pulse align-text-bottom" />
              )}
            </div>
          )}

          {round.thoughtText && (
            <details className="pt-1">
              <summary className="text-xs text-muted-foreground/70 cursor-pointer hover:text-foreground select-none">
                Thinking...{round.thoughtText.length > 500 ? ` (${(round.thoughtText.length / 1024).toFixed(1)}KB)` : ''}
              </summary>
              <pre className="mt-1 text-xs text-muted-foreground whitespace-pre-wrap break-all max-h-48 overflow-auto bg-muted rounded p-2">
                {round.thoughtText.length > 20000
                  ? round.thoughtText.slice(0, 20000) + '\n... (truncated)'
                  : round.thoughtText}
              </pre>
            </details>
          )}

          {round.toolCalls.length > 0 && (
            <div className="space-y-0">
              {round.toolCalls.map((tc) => (
                <ToolCallTimelineEntry key={tc.toolCallId ?? tc.executionId} entry={tc} />
              ))}
            </div>
          )}

          {round.recoveryEvents.length > 0 && (
            <div className="space-y-0.5 pt-1">
              {round.recoveryEvents.map((evt, i) => (
                <RecoveryEventIndicator key={i} event={evt} />
              ))}
            </div>
          )}

          {round.compactions.length > 0 && (
            <div className="space-y-0 pt-1">
              {round.compactions.map((entry) => (
                <CompactionTimelineEntry
                  key={entry.id}
                  entry={{
                    id: entry.id,
                    strategy: entry.strategy ?? null,
                    contextWindowUsedBefore: entry.contextWindowUsedBefore,
                    contextWindowUsedAfter: entry.contextWindowUsedAfter,
                    contextWindowSize: entry.contextWindowSize,
                    summary: entry.summary ?? null,
                    recordedAt: entry.recordedAt,
                  }}
                />
              ))}
            </div>
          )}

          {!hasContent && !isLiveRound && (
            <div className="text-xs text-muted-foreground/70 pt-2">No output recorded</div>
          )}
        </div>
      )}
    </div>
  )
}

function TaskStatusIcon({ status }: { status: string }) {
  switch (status) {
    case 'passed':
      return (
        <svg className="h-3.5 w-3.5 text-success" viewBox="0 0 20 20" fill="currentColor">
          <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.857-9.809a.75.75 0 00-1.214-.882l-3.483 4.79-1.88-1.88a.75.75 0 10-1.06 1.061l2.5 2.5a.75.75 0 001.137-.089l4-5.5z" clipRule="evenodd" />
        </svg>
      )
    case 'running':
      return (
        <svg className="h-3.5 w-3.5 text-info animate-spin" viewBox="0 0 24 24" fill="none">
          <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
          <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
        </svg>
      )
    case 'failed':
      return (
        <svg className="h-3.5 w-3.5 text-danger" viewBox="0 0 20 20" fill="currentColor">
          <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zM8.28 7.22a.75.75 0 00-1.06 1.06L8.94 10l-1.72 1.72a.75.75 0 101.06 1.06L10 11.06l1.72 1.72a.75.75 0 101.06-1.06L11.06 10l1.72-1.72a.75.75 0 00-1.06-1.06L10 8.94 8.28 7.22z" clipRule="evenodd" />
        </svg>
      )
    case 'retrying':
      return (
        <svg
          data-testid="task-status-retrying"
          data-tone="warning"
          className="h-3.5 w-3.5 text-warning"
          viewBox="0 0 20 20"
          fill="currentColor"
        >
          <path fillRule="evenodd" d="M15.312 11.424a5.5 5.5 0 01-9.201 2.466l-.312-.311h2.433a.75.75 0 000-1.5H4.598a.75.75 0 00-.75.75v3.634a.75.75 0 001.5 0v-2.233l.312.311a7 7 0 0011.712-3.138.75.75 0 00-1.449-.39zm-10.624-2.85a5.5 5.5 0 019.2-2.464l.311.311h-2.432a.75.75 0 000 1.5h3.634a.75.75 0 00.75-.75V3.538a.75.75 0 00-1.5 0v2.234l-.311-.312a7 7 0 00-11.712 3.138.75.75 0 001.449.39z" clipRule="evenodd" />
        </svg>
      )
    default:
      return (
        <span className="inline-block h-2.5 w-2.5 rounded-full bg-muted-foreground/60" />
      )
  }
}

export function TaskProgressPanel({
  tasks,
  loopProgress,
}: {
  tasks: Array<{ taskId: string; status: string; error?: string }>
  loopProgress: LoopProgress | null
}) {
  const passed = tasks.filter((t) => t.status === 'passed').length
  const total = loopProgress?.total ?? tasks.length

  return (
    <div className="rounded-lg border border-info-border bg-info-subtle/30 p-3 mb-2">
      <div className="flex items-center justify-between mb-2">
        <span className="text-xs font-medium text-info">Task Progress</span>
        <span className="text-xs text-info">{passed}/{total} passed</span>
      </div>
      <div className="flex flex-wrap gap-1.5">
        {tasks.map((task) => (
          <div key={task.taskId} className="flex items-center gap-1" title={task.error ?? task.status}>
            <TaskStatusIcon status={task.status} />
            <span className="text-xs font-mono text-foreground">{task.taskId}</span>
          </div>
        ))}
      </div>
      {tasks.some((t) => t.status === 'failed' && t.error) && (
        <div className="mt-2 space-y-1">
          {tasks
            .filter((t) => t.status === 'failed' && t.error)
            .map((t) => (
              <div key={t.taskId} className="text-xs text-danger bg-danger-subtle rounded px-2 py-1">
                <span className="font-mono">{t.taskId}:</span> {t.error}
              </div>
            ))}
        </div>
      )}
    </div>
  )
}

function RecoveryBanner({ status }: { status: RecoveryStatus }) {
  if (status.status === 'detected') {
    return (
      <div className="flex items-center gap-2 rounded-md border border-warning-border bg-warning-subtle px-3 py-2 text-xs text-warning">
        <svg className="h-4 w-4 shrink-0 text-warning" viewBox="0 0 20 20" fill="currentColor">
          <path fillRule="evenodd" d="M8.485 2.495c.673-1.167 2.357-1.167 3.03 0l6.28 10.875c.673 1.167-.17 2.625-1.516 2.625H3.72c-1.347 0-2.189-1.458-1.515-2.625L8.485 2.495zM10 5a.75.75 0 01.75.75v3.5a.75.75 0 01-1.5 0v-3.5A.75.75 0 0110 5zm0 9a1 1 0 100-2 1 1 0 000 2z" clipRule="evenodd" />
        </svg>
        <span className="font-medium">Coder agent 连接中断，正在尝试恢复...</span>
      </div>
    )
  }

  if (status.status === 'recovering') {
    return (
      <div className="flex items-center gap-2 rounded-md border border-info-border bg-info-subtle px-3 py-2 text-xs text-info">
        <svg className="h-4 w-4 shrink-0 text-info animate-spin" viewBox="0 0 24 24" fill="none">
          <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
          <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
        </svg>
        <span className="font-medium">正在恢复 (attempt {status.attempt})...</span>
      </div>
    )
  }

  if (status.status === 'failed') {
    return (
      <div className="flex items-center gap-2 rounded-md border border-danger-border bg-danger-subtle px-3 py-2 text-xs text-danger">
        <svg className="h-4 w-4 shrink-0 text-danger" viewBox="0 0 20 20" fill="currentColor">
          <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zM8.28 7.22a.75.75 0 00-1.06 1.06L8.94 10l-1.72 1.72a.75.75 0 101.06 1.06L10 11.06l1.72 1.72a.75.75 0 101.06-1.06L11.06 10l1.72-1.72a.75.75 0 00-1.06-1.06L10 8.94 8.28 7.22z" clipRule="evenodd" />
        </svg>
        <span className="font-medium">恢复失败{status.reason ? `: ${status.reason}` : ''}</span>
      </div>
    )
  }

  return null
}

function RecoveryEventIndicator({ event }: { event: RecoveryEvent }) {
  if (event.status === 'detected') {
    return (
      <div className="flex items-center gap-1.5 text-xs text-warning py-0.5">
        <svg className="h-3 w-3 shrink-0 text-warning" viewBox="0 0 20 20" fill="currentColor">
          <path fillRule="evenodd" d="M8.485 2.495c.673-1.167 2.357-1.167 3.03 0l6.28 10.875c.673 1.167-.17 2.625-1.516 2.625H3.72c-1.347 0-2.189-1.458-1.515-2.625L8.485 2.495zM10 5a.75.75 0 01.75.75v3.5a.75.75 0 01-1.5 0v-3.5A.75.75 0 0110 5zm0 9a1 1 0 100-2 1 1 0 000 2z" clipRule="evenodd" />
        </svg>
        <span>Coder agent 连接中断</span>
      </div>
    )
  }

  if (event.status === 'recovering') {
    return (
      <div className="flex items-center gap-1.5 text-xs text-info py-0.5">
        <svg className="h-3 w-3 shrink-0 text-info" viewBox="0 0 20 20" fill="currentColor">
          <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.857-9.809a.75.75 0 00-1.214-.882l-3.483 4.79-1.88-1.88a.75.75 0 10-1.06 1.061l2.5 2.5a.75.75 0 001.137-.089l4-5.5z" clipRule="evenodd" />
        </svg>
        <span>尝试恢复 (attempt {event.attempt})</span>
      </div>
    )
  }

  if (event.status === 'recovered') {
    return (
      <div className="flex items-center gap-1.5 text-xs text-success py-0.5">
        <svg className="h-3 w-3 shrink-0 text-success" viewBox="0 0 20 20" fill="currentColor">
          <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.857-9.809a.75.75 0 00-1.214-.882l-3.483 4.79-1.88-1.88a.75.75 0 10-1.06 1.061l2.5 2.5a.75.75 0 001.137-.089l4-5.5z" clipRule="evenodd" />
        </svg>
        <span>恢复成功</span>
      </div>
    )
  }

  if (event.status === 'failed') {
    return (
      <div className="flex items-center gap-1.5 text-xs text-danger py-0.5">
        <svg className="h-3 w-3 shrink-0 text-danger" viewBox="0 0 20 20" fill="currentColor">
          <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zM8.28 7.22a.75.75 0 00-1.06 1.06L8.94 10l-1.72 1.72a.75.75 0 101.06 1.06L10 11.06l1.72 1.72a.75.75 0 101.06-1.06L11.06 10l1.72-1.72a.75.75 0 00-1.06-1.06L10 8.94 8.28 7.22z" clipRule="evenodd" />
        </svg>
        <span>恢复失败{event.reason ? `: ${event.reason}` : ''}</span>
      </div>
    )
  }

  return null
}

export function SessionTimeline({
  rounds,
  isStreaming,
  isLoading,
  currentStage,
  isLive,
  recoveryStatus,
  planProgress,
  contextHealth,
  onCompact,
  onReset,
}: SessionTimelineProps) {
  if (isLoading) {
    return (
      <div className="rounded-lg border border-border bg-background p-4">
        <div className="text-sm text-muted-foreground/70 text-center">Loading session...</div>
      </div>
    )
  }

  if (rounds.length === 0 && !isStreaming) {
    return (
      <div className="rounded-lg border border-border bg-background p-4">
        <div className="text-sm text-muted-foreground/70 text-center">No agent activity yet</div>
      </div>
    )
  }

  const showContextHealth = contextHealth != null
    && contextHealth.status != null
    && contextHealth.contextUsagePercent != null
    && contextHealth.contextWindowSize != null
    && contextHealth.contextWindowSize > 0
    && (onCompact != null || onReset != null)

  const allCompactions = rounds.flatMap((round) => round.compactions)
  const hasCompactions = allCompactions.length > 0

  return (
    <div className="rounded-lg border border-info-border bg-info-subtle/30">
      <div className="px-3 py-2 border-b border-info-border flex items-center gap-2">
        <span className="inline-block h-2.5 w-2.5 rounded-full bg-info" />
        <span className="text-sm text-info font-semibold">Agent Session</span>
        {isLive && isStreaming && (
          <span className="text-xs text-info ml-auto flex items-center gap-1">
            <span className="inline-block h-2 w-2 rounded-full bg-info animate-pulse" />
            Live
          </span>
        )}
        {!isLive && rounds.length > 0 && (
          <span className="text-xs text-muted-foreground/70 ml-auto">History</span>
        )}
      </div>

      <div className="px-3 py-3 space-y-2 max-h-[600px] overflow-y-auto">
        {showContextHealth && (
          <div data-testid="context-health-section">
            <ContextHealthBar
              contextWindowUsed={contextHealth.contextWindowUsed}
              contextWindowSize={contextHealth.contextWindowSize}
              contextUsagePercent={contextHealth.contextUsagePercent}
              healthStatus={contextHealth.status}
              onCompact={onCompact}
              onReset={onReset}
            />
          </div>
        )}

        {hasCompactions && (
          <CompactionCompactSummary entries={allCompactions} />
        )}

        <WorkflowStatusTimeline currentStage={currentStage} />

        {currentStage === 'plan' && planProgress && planProgress.steps.length > 0 && (
          <PlanProgressPanel planProgress={planProgress} />
        )}

        {recoveryStatus && (
          <RecoveryBanner status={recoveryStatus} />
        )}

        {rounds.map((round) => (
          <RoundSection
            key={`${round.roundIndex}-${round.label}`}
            round={round}
            isLive={isLive}
            isStreaming={isStreaming}
          />
        ))}
      </div>
    </div>
  )
}
