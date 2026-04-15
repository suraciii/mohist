import { useState } from 'react'
import type { ToolCallEntry, CoderSessionItem, CoderTextBuffer } from '../lib/types'

interface AgentSessionPanelProps {
  agentText: string
  toolCalls: ToolCallEntry[]
  coderSessions: CoderSessionItem[]
  coderTexts: CoderTextBuffer[]
  isStreaming: boolean
  isLive: boolean
}

function formatDuration(ms: number): string {
  if (ms < 1000) return `${ms}ms`
  if (ms < 60000) return `${(ms / 1000).toFixed(1)}s`
  return `${Math.floor(ms / 60000)}m ${Math.round((ms % 60000) / 1000)}s`
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
  const isCoder = !!entry.acpSessionId

  return (
    <div className={`flex gap-2 ${isCoder ? 'ml-4' : ''}`}>
      <div className="flex flex-col items-center shrink-0 pt-0.5">
        <StatusIcon state={entry.state} />
        <div className="w-px flex-1 bg-gray-200 mt-1" />
      </div>
      <div className="flex-1 min-w-0 pb-3">
        <button
          onClick={() => entry.state !== 'started' && setExpanded(!expanded)}
          className={`flex items-center gap-2 w-full text-left ${entry.state !== 'started' ? 'cursor-pointer hover:bg-gray-50 rounded px-1 -mx-1' : 'cursor-default'}`}
        >
          <span className={`font-mono text-xs ${isCoder ? 'text-purple-600' : 'text-gray-700'}`}>
            {entry.toolName}
          </span>
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
            {entry.args && (
              <div>
                <div className="font-medium text-gray-500 mb-0.5">Arguments</div>
                <pre className="whitespace-pre-wrap break-all text-gray-700 bg-gray-50 rounded p-2 max-h-32 overflow-auto">
                  {tryFormatJson(entry.args)}
                </pre>
              </div>
            )}
            {entry.result && (
              <div>
                <div className="font-medium text-gray-500 mb-0.5">Result</div>
                <pre className="whitespace-pre-wrap break-all text-gray-700 bg-gray-50 rounded p-2 max-h-48 overflow-auto">
                  {truncate(tryFormatJson(entry.result), 2000)}
                </pre>
              </div>
            )}
          </div>
        )}
      </div>
    </div>
  )
}

function CoderSubTimeline({
  session,
  coderTexts,
  toolCalls,
}: {
  session: CoderSessionItem
  coderTexts: CoderTextBuffer[]
  toolCalls: ToolCallEntry[]
}) {
  const [expanded, setExpanded] = useState(true)
  const text = coderTexts.find((t) => t.acpSessionId === session.acpSessionId)?.text ?? ''
  const sessionToolCalls = toolCalls.filter(
    (tc) => tc.acpSessionId === session.acpSessionId,
  )
  const statusColor = session.status === 'completed'
    ? 'text-green-500'
    : session.status === 'failed'
      ? 'text-red-500'
      : 'text-blue-500 animate-pulse'

  return (
    <div className="ml-4 border-l-2 border-purple-200 pl-3">
      <button
        onClick={() => setExpanded(!expanded)}
        className="flex items-center gap-2 w-full text-left hover:bg-purple-50 rounded px-1 -mx-1"
      >
        <svg
          className={`h-3 w-3 text-gray-400 shrink-0 transition-transform ${expanded ? 'rotate-90' : ''}`}
          viewBox="0 0 20 20"
          fill="currentColor"
        >
          <path fillRule="evenodd" d="M7.21 14.77a.75.75 0 01.02-1.06L11.168 10 7.23 6.29a.75.75 0 111.04-1.08l4.5 4.25a.75.75 0 010 1.08l-4.5 4.25a.75.75 0 01-1.06-.02z" clipRule="evenodd" />
        </svg>
        <span className="inline-block h-2 w-2 rounded-full bg-purple-400" />
        <span className="text-xs font-medium text-purple-700">Coder Session</span>
        {session.taskDescription && (
          <span className="text-xs text-gray-500 truncate">{session.taskDescription}</span>
        )}
        <span className={`text-xs ${statusColor}`}>
          {session.status === 'running' ? 'running...' : session.status}
        </span>
      </button>

      {expanded && (
        <div className="mt-2 space-y-0">
          {text && (
            <div className="text-xs text-gray-600 whitespace-pre-wrap mb-2 pl-3 border-l border-purple-100">
              {text}
            </div>
          )}
          {sessionToolCalls.map((tc) => (
            <ToolCallTimelineEntry key={tc.executionId} entry={tc} />
          ))}
          {session.workflowLogs.length > 0 && (
            <div className="text-xs text-gray-400 pl-3">
              {session.workflowLogs.length} log entries
            </div>
          )}
        </div>
      )}
    </div>
  )
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

export function AgentSessionPanel({
  agentText,
  toolCalls,
  coderSessions,
  coderTexts,
  isStreaming,
  isLive,
}: AgentSessionPanelProps) {
  const mainToolCalls = toolCalls.filter((tc) => !tc.executionId.startsWith('coder-'))
  const hasContent = agentText || mainToolCalls.length > 0 || coderSessions.length > 0

  return (
    <div className="rounded-lg border border-blue-200 bg-blue-50/50">
      <div className="px-3 py-2 border-b border-blue-200 flex items-center gap-2">
        <span className="inline-block h-2.5 w-2.5 rounded-full bg-blue-500 animate-pulse" />
        <span className="text-sm text-blue-800 font-semibold">Agent Session</span>
        {isLive && isStreaming && (
          <span className="text-xs text-blue-500 ml-auto">Live</span>
        )}
        {!isLive && hasContent && (
          <span className="text-xs text-gray-400 ml-auto">History</span>
        )}
      </div>

      <div className="px-3 py-3 space-y-3 max-h-[600px] overflow-y-auto">
        {agentText && (
          <div className="text-sm text-gray-700 whitespace-pre-wrap leading-relaxed">
            {agentText}
            {isStreaming && (
              <span className="inline-block w-1.5 h-4 bg-blue-500 ml-0.5 animate-pulse align-text-bottom" />
            )}
          </div>
        )}

        {!hasContent && !isStreaming && (
          <div className="text-sm text-gray-400 text-center py-2">
            Waiting for agent activity...
          </div>
        )}

        {mainToolCalls.length > 0 && (
          <div className="space-y-0">
            {mainToolCalls.map((tc) => (
              <ToolCallTimelineEntry key={tc.executionId} entry={tc} />
            ))}
          </div>
        )}

        {coderSessions.map((cs) => (
          <CoderSubTimeline
            key={cs.id}
            session={cs}
            coderTexts={coderTexts}
            toolCalls={toolCalls}
          />
        ))}
      </div>
    </div>
  )
}
