import { useEffect, useRef } from 'react'
import { Link } from 'react-router-dom'
import type { ExploreMessage as ExploreMessageType, ToolCallRecord } from '../lib/types'
import { ExploreMessage } from './ExploreMessage'
import { ExploreToolCall } from './ExploreToolCall'

interface ExploreChatProps {
  messages: ExploreMessageType[]
  streamingContent: string
  streamingToolCalls: ToolCallRecord[]
  streamingIssueId: string | null
  isStreaming: boolean
}

export function ExploreChat({
  messages,
  streamingContent,
  streamingToolCalls,
  streamingIssueId,
  isStreaming,
}: ExploreChatProps) {
  const bottomRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: 'smooth' })
  }, [messages, streamingContent])

  return (
    <div className="flex-1 overflow-y-auto px-4 py-6 space-y-4">
      {messages.length === 0 && !isStreaming && (
        <div className="flex items-center justify-center h-full">
          <div className="text-center text-gray-400">
            <div className="text-lg mb-1">Start exploring</div>
            <div className="text-sm">Ask questions about your codebase, explore requirements, or clarify ideas.</div>
          </div>
        </div>
      )}

      {messages.map((msg) => (
        <ExploreMessage key={msg.id} message={msg} />
      ))}

      {isStreaming && (
        <>
          {streamingToolCalls.map((tc, i) => (
            <div key={`stream-tc-${i}`} className="flex justify-start">
              <div className="max-w-[80%] rounded-2xl rounded-bl-md px-4 py-2.5 text-sm text-gray-800 bg-white border border-gray-200">
                <ExploreToolCall toolCall={tc} />
              </div>
            </div>
          ))}
          <ExploreMessage
            message={{
              id: 'streaming',
              sessionId: '',
              role: 'assistant',
              content: streamingContent,
              toolCalls: null,
              createdAt: new Date().toISOString(),
            }}
            streaming
          />
        </>
      )}

      {!isStreaming && streamingIssueId && (
        <div className="flex justify-center">
          <Link
            to={`/issue/${streamingIssueId}`}
            className="inline-flex items-center gap-1.5 rounded-md bg-green-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-green-700 transition-colors"
          >
            <svg className="h-4 w-4" viewBox="0 0 20 20" fill="currentColor">
              <path
                fillRule="evenodd"
                d="M2 3.5A1.5 1.5 0 013.5 2h9A1.5 1.5 0 0114 3.5v11.75A2.75 2.75 0 0016.75 18h-12A2.75 2.75 0 012 15.25V3.5zm3.75 7a.75.75 0 000 1.5h4.5a.75.75 0 000-1.5h-4.5zm0 3a.75.75 0 000 1.5h4.5a.75.75 0 000-1.5h-4.5zM5 5.75A.75.75 0 015.75 5h4.5a.75.75 0 01.75.75v2.5a.75.75 0 01-.75.75h-4.5A.75.75 0 015 8.25v-2.5z"
                clipRule="evenodd"
              />
            </svg>
            View Issue #{streamingIssueId}
          </Link>
        </div>
      )}

      <div ref={bottomRef} />
    </div>
  )
}
