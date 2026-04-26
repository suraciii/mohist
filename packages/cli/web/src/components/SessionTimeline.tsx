import { useState } from 'react'
import type { ToolCallEntry, Stage } from '../lib/types'
import type { Round } from '../hooks/useSessionTimeline'

interface SessionTimelineProps {
  rounds: Round[]
  isStreaming: boolean
  isLoading: boolean
  currentStage: Stage | string
  isLive: boolean
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
      <svg className="h-3.5 w-3.5 text-blue-500 animate-spin" viewBox="0 0 24 24" fill="none">
        <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
        <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
      </svg>
    )
  }
  if (state === 'completed') {
    return (
      <svg className="h-3.5 w-3.5 text-green-500" viewBox="0 0 20 20" fill="currentColor">
        <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.857-9.809a.75.75 0 00-1.214-.882l-3.483 4.79-1.88-1.88a.75.75 0 10-1.06 1.061l2.5 2.5a.75.75 0 001.137-.089l4-5.5z" clipRule="evenodd" />
      </svg>
    )
  }
  return (
    <svg className="h-3.5 w-3.5 text-red-500" viewBox="0 0 20 20" fill="currentColor">
      <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zM8.28 7.22a.75.75 0 00-1.06 1.06L8.94 10l-1.72 1.72a.75.75 0 101.06 1.06L10 11.06l1.72 1.72a.75.75 0 101.06-1.06L11.06 10l1.72-1.72a.75.75 0 00-1.06-1.06L10 8.94 8.28 7.22z" clipRule="evenodd" />
    </svg>
  )
}

function ToolCallTimelineEntry({ entry }: { entry: ToolCallEntry }) {
  const [expanded, setExpanded] = useState(false)

  const displayInput = entry.rawInput ?? entry.args
  const displayOutput = entry.rawOutput ?? entry.result

  return (
    <div className="flex gap-2">
      <div className="flex flex-col items-center shrink-0 pt-0.5">
        <StatusIcon state={entry.state} />
        <div className="w-px flex-1 bg-gray-200 mt-1" />
      </div>
      <div className="flex-1 min-w-0 pb-3">
        <button
          onClick={() => entry.state !== 'started' && setExpanded(!expanded)}
          className={`flex items-center gap-2 w-full text-left ${entry.state !== 'started' ? 'cursor-pointer hover:bg-gray-50 rounded px-1 -mx-1' : 'cursor-default'}`}
        >
          <span className="font-mono text-xs text-gray-700">
            {entry.toolName}
          </span>
          {entry.title && (
            <span className="text-xs text-gray-500 truncate">{entry.title}</span>
          )}
          {entry.state === 'started' && (
            <span className="text-xs text-blue-500">running...</span>
          )}
          {entry.duration != null && entry.state !== 'started' && (
            <span className="text-xs text-gray-400">{formatDuration(entry.duration)}</span>
          )}
          {entry.state === 'failed' && entry.error && (
            <span className="text-xs text-red-500 truncate">{entry.error}</span>
          )}
          {entry.state !== 'started' && (
            <svg
              className={`h-3 w-3 text-gray-400 shrink-0 transition-transform ml-auto ${expanded ? 'rotate-90' : ''}`}
              viewBox="0 0 20 20"
              fill="currentColor"
            >
              <path fillRule="evenodd" d="M7.21 14.77a.75.75 0 01.02-1.06L11.168 10 7.23 6.29a.75.75 0 111.04-1.08l4.5 4.25a.75.75 0 010 1.08l-4.5 4.25a.75.75 0 01-1.06-.02z" clipRule="evenodd" />
            </svg>
          )}
        </button>

        {expanded && (
          <div className="mt-1.5 space-y-1.5 text-xs">
            {displayInput && (
              <div>
                <div className="font-medium text-gray-500 mb-0.5">Input</div>
                <pre className="whitespace-pre-wrap break-all text-gray-700 bg-gray-50 rounded p-2 max-h-32 overflow-auto">
                  {tryFormatJson(typeof displayInput === 'string' ? displayInput : JSON.stringify(displayInput))}
                </pre>
              </div>
            )}
            {displayOutput && (
              <div>
                <div className="font-medium text-gray-500 mb-0.5">Output</div>
                <pre className="whitespace-pre-wrap break-all text-gray-700 bg-gray-50 rounded p-2 max-h-48 overflow-auto">
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

const PIPELINE_STAGES = [
  { key: 'plan', label: 'Plan' },
  { key: 'build', label: 'Build' },
  { key: 'review', label: 'Review' },
  { key: 'done', label: 'Done' },
]

function PipelineStatusTimeline({ currentStage }: { currentStage: string }) {
  const stageOrder = ['draft', 'explore', 'plan', 'build', 'review', 'done']
  const currentIndex = stageOrder.indexOf(currentStage)

  return (
    <div className="flex items-center gap-1 mb-4">
      {PIPELINE_STAGES.map((stage, i) => {
        const stageIdx = stageOrder.indexOf(stage.key)
        const isCompleted = currentIndex > stageIdx
        const isCurrent = currentIndex === stageIdx

        return (
          <div key={stage.key} className="flex items-center gap-1">
            {i > 0 && (
              <div className={`h-0.5 w-4 ${isCompleted || isCurrent ? 'bg-blue-500' : 'bg-gray-200'}`} />
            )}
            <div className="flex items-center gap-1">
              {isCompleted ? (
                <svg className="h-3.5 w-3.5 text-green-500" viewBox="0 0 20 20" fill="currentColor">
                  <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.857-9.809a.75.75 0 00-1.214-.882l-3.483 4.79-1.88-1.88a.75.75 0 10-1.06 1.061l2.5 2.5a.75.75 0 001.137-.089l4-5.5z" clipRule="evenodd" />
                </svg>
              ) : isCurrent ? (
                <span className="inline-block h-2.5 w-2.5 rounded-full bg-blue-500 animate-pulse" />
              ) : (
                <span className="inline-block h-2 w-2 rounded-full bg-gray-200" />
              )}
              <span className={`text-xs ${isCompleted || isCurrent ? 'text-blue-600 font-medium' : 'text-gray-400'}`}>
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
      dot: 'bg-purple-400',
      border: 'border-purple-200',
      bg: 'bg-purple-50/30',
      text: 'text-purple-700',
      labelBg: 'bg-purple-100',
    }
  }
  if (isLive && !round.completedAt) {
    return {
      dot: 'bg-blue-500 animate-pulse',
      border: 'border-blue-200',
      bg: 'bg-blue-50/30',
      text: 'text-blue-700',
      labelBg: 'bg-blue-100',
    }
  }
  return {
    dot: 'bg-gray-400',
    border: 'border-gray-200',
    bg: 'bg-white',
    text: 'text-gray-700',
    labelBg: 'bg-gray-100',
  }
}

function RoundSection({
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
  const hasContent = round.agentText || round.toolCalls.length > 0

  return (
    <div className={`rounded-lg border ${colors.border} ${colors.bg}`}>
      <button
        onClick={() => setExpanded(!expanded)}
        className="flex items-center gap-2 w-full text-left px-3 py-2 hover:bg-gray-50/50 rounded-t-lg"
      >
        <svg
          className={`h-3 w-3 text-gray-400 shrink-0 transition-transform ${expanded ? 'rotate-90' : ''}`}
          viewBox="0 0 20 20"
          fill="currentColor"
        >
          <path fillRule="evenodd" d="M7.21 14.77a.75.75 0 01.02-1.06L11.168 10 7.23 6.29a.75.75 0 111.04-1.08l4.5 4.25a.75.75 0 010 1.08l-4.5 4.25a.75.75 0 01-1.06-.02z" clipRule="evenodd" />
        </svg>
        <span className={`inline-block h-2 w-2 rounded-full ${colors.dot}`} />
        <span className={`text-xs font-medium ${colors.text}`}>{round.label}</span>
        {round.startedAt && (
          <span className="text-xs text-gray-400">
            {new Date(round.startedAt).toLocaleTimeString()}
          </span>
        )}
        {isLiveRound && (
          <span className="text-xs text-blue-500 ml-auto">Live</span>
        )}
        {!round.completedAt && !isLiveRound && round.agentText && (
          <span className="text-xs text-gray-400 ml-auto">In progress</span>
        )}
      </button>

      {expanded && (
        <div className="px-3 pb-3 space-y-2 border-t border-gray-100">
          {round.agentText && (
            <div className="text-sm text-gray-700 whitespace-pre-wrap leading-relaxed pt-2">
              {round.agentText}
              {isLiveRound && isStreaming && (
                <span className="inline-block w-1.5 h-4 bg-blue-500 ml-0.5 animate-pulse align-text-bottom" />
              )}
            </div>
          )}

          {round.toolCalls.length > 0 && (
            <div className="space-y-0">
              {round.toolCalls.map((tc) => (
                <ToolCallTimelineEntry key={tc.toolCallId ?? tc.executionId} entry={tc} />
              ))}
            </div>
          )}

          {!hasContent && !isLiveRound && (
            <div className="text-xs text-gray-400 pt-2">No output recorded</div>
          )}
        </div>
      )}
    </div>
  )
}

export function SessionTimeline({
  rounds,
  isStreaming,
  isLoading,
  currentStage,
  isLive,
}: SessionTimelineProps) {
  if (isLoading) {
    return (
      <div className="rounded-lg border border-gray-200 bg-white p-4">
        <div className="text-sm text-gray-400 text-center">Loading session...</div>
      </div>
    )
  }

  if (rounds.length === 0 && !isStreaming) {
    return (
      <div className="rounded-lg border border-gray-200 bg-white p-4">
        <div className="text-sm text-gray-400 text-center">No agent activity yet</div>
      </div>
    )
  }

  return (
    <div className="rounded-lg border border-blue-200 bg-blue-50/30">
      <div className="px-3 py-2 border-b border-blue-200 flex items-center gap-2">
        <span className="inline-block h-2.5 w-2.5 rounded-full bg-blue-500" />
        <span className="text-sm text-blue-800 font-semibold">Agent Session</span>
        {isLive && isStreaming && (
          <span className="text-xs text-blue-500 ml-auto flex items-center gap-1">
            <span className="inline-block h-2 w-2 rounded-full bg-blue-500 animate-pulse" />
            Live
          </span>
        )}
        {!isLive && rounds.length > 0 && (
          <span className="text-xs text-gray-400 ml-auto">History</span>
        )}
      </div>

      <div className="px-3 py-3 space-y-2 max-h-[600px] overflow-y-auto">
        <PipelineStatusTimeline currentStage={currentStage} />

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
