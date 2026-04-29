import type { CoderSessionItem } from '../lib/types'

interface SessionDetailProps {
  session: CoderSessionItem
}

function computeSummary(logs: CoderSessionItem['workflowLogs']) {
  let filesChanged = 0
  let toolCalls = 0
  for (const log of logs) {
    if (log.eventType === 'tool_call') {
      toolCalls++
      const data = log.data as { toolName?: string } | undefined
      if (data?.toolName === 'edit' || data?.toolName === 'write') {
        filesChanged++
      }
    }
  }
  return { filesChanged, toolCalls }
}

export function SessionDetail({ session }: SessionDetailProps) {
  const { filesChanged, toolCalls } = computeSummary(session.workflowLogs)

  return (
    <div className="px-3 py-1.5 border-t border-gray-100">
      <span className="text-xs text-gray-400">
        {filesChanged} file{filesChanged !== 1 ? 's' : ''} changed · {toolCalls} tool call{toolCalls !== 1 ? 's' : ''}
      </span>
    </div>
  )
}
