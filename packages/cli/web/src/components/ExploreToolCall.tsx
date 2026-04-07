import { useState } from 'react'
import type { ToolCallRecord } from '../lib/types'

interface ExploreToolCallProps {
  toolCall: ToolCallRecord
}

export function ExploreToolCall({ toolCall }: ExploreToolCallProps) {
  const [expanded, setExpanded] = useState(false)

  return (
    <div className="my-1.5 rounded-md border border-gray-200 bg-gray-50 text-xs">
      <button
        onClick={() => setExpanded(!expanded)}
        className="w-full flex items-center gap-1.5 px-2.5 py-1.5 text-left hover:bg-gray-100 transition-colors rounded-md"
      >
        <svg
          className={`h-3 w-3 text-gray-400 shrink-0 transition-transform ${expanded ? 'rotate-90' : ''}`}
          viewBox="0 0 20 20"
          fill="currentColor"
        >
          <path
            fillRule="evenodd"
            d="M7.21 14.77a.75.75 0 01.02-1.06L11.168 10 7.23 6.29a.75.75 0 111.04-1.08l4.5 4.25a.75.75 0 010 1.08l-4.5 4.25a.75.75 0 01-1.06-.02z"
            clipRule="evenodd"
          />
        </svg>
        <span className="font-mono text-gray-600">{toolCall.name}</span>
        <span className="text-gray-400 truncate">
          {typeof toolCall.args === 'object'
            ? Object.entries(toolCall.args)
                .slice(0, 2)
                .map(([k, v]) => `${k}=${typeof v === 'string' ? v : JSON.stringify(v)}`)
                .join(', ')
            : ''}
        </span>
      </button>

      {expanded && (
        <div className="border-t border-gray-200 px-2.5 py-2 space-y-2">
          <div>
            <div className="font-medium text-gray-500 mb-1">Arguments</div>
            <pre className="whitespace-pre-wrap break-all text-gray-700 bg-gray-100 rounded p-2 max-h-40 overflow-auto">
              {typeof toolCall.args === 'object'
                ? JSON.stringify(toolCall.args, null, 2)
                : String(toolCall.args)}
            </pre>
          </div>
          <div>
            <div className="font-medium text-gray-500 mb-1">Result</div>
            <pre className="whitespace-pre-wrap break-all text-gray-700 bg-gray-100 rounded p-2 max-h-60 overflow-auto">
              {typeof toolCall.result === 'string'
                ? toolCall.result.length > 2000
                  ? toolCall.result.slice(0, 2000) + '\n... (truncated)'
                  : toolCall.result
                : JSON.stringify(toolCall.result, null, 2)}
            </pre>
          </div>
        </div>
      )}
    </div>
  )
}
