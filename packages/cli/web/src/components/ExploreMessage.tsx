import Markdown from 'react-markdown'
import type { ExploreMessage as ExploreMessageType, ToolCallRecord } from '../lib/types'
import { ExploreToolCall } from './ExploreToolCall'

interface ExploreMessageProps {
  message: ExploreMessageType
  streaming?: boolean
}

export function ExploreMessage({ message, streaming }: ExploreMessageProps) {
  const isUser = message.role === 'user'
  const toolCalls = message.toolCalls as ToolCallRecord[] | null

  if (isUser) {
    return (
      <div className="flex justify-end">
        <div className="max-w-[75%] bg-blue-600 text-white rounded-2xl rounded-br-md px-4 py-2.5 text-sm whitespace-pre-wrap">
          {message.content}
        </div>
      </div>
    )
  }

  return (
    <div className="flex justify-start">
      <div className={`max-w-[80%] rounded-2xl rounded-bl-md px-4 py-2.5 text-sm text-gray-800 ${streaming ? 'bg-white border border-gray-200' : 'bg-gray-100'}`}>
        {toolCalls && toolCalls.length > 0 && (
          <div className="mb-2 space-y-0.5">
            {toolCalls.map((tc, i) => (
              <ExploreToolCall key={i} toolCall={tc} />
            ))}
          </div>
        )}
        <div className="prose prose-sm prose-gray max-w-none prose-pre:bg-gray-800 prose-pre:text-gray-100 prose-code:text-gray-800 prose-code:before:content-none prose-code:after:content-none">
          <Markdown>{message.content || ''}</Markdown>
          {streaming && !message.content && (
            <span className="inline-block w-1.5 h-4 bg-gray-400 animate-pulse ml-0.5 align-middle" />
          )}
        </div>
      </div>
    </div>
  )
}
