import { useCallback } from 'react'
import { useParams, useNavigate } from 'react-router-dom'
import { useExploreSession } from '../hooks/useQueries'
import { useExploreStream } from '../hooks/useExploreStream'
import { useQueryClient } from '@tanstack/react-query'
import { ExploreChat } from './ExploreChat'
import { ExploreInput } from './ExploreInput'

export function ExplorePage() {
  const { id } = useParams<{ id: string }>()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const { data, isLoading } = useExploreSession(id || '')
  const {
    streaming,
    streamContent,
    streamToolCalls,
    streamIssueId,
    streamError,
    send,
  } = useExploreStream()

  const handleSend = useCallback(
    async (content: string) => {
      if (!id) return
      await send(id, content)
      queryClient.invalidateQueries({ queryKey: ['explore', id] })
    },
    [id, send, queryClient],
  )

  if (isLoading) {
    return (
      <div className="flex items-center justify-center flex-1">
        <div className="text-gray-400">Loading...</div>
      </div>
    )
  }

  if (!data) {
    return (
      <div className="flex items-center justify-center flex-1">
        <div className="text-center text-gray-400">
          <div className="text-lg mb-2">Session not found</div>
          <button
            onClick={() => navigate('/')}
            className="text-sm text-blue-600 hover:text-blue-700"
          >
            Go back
          </button>
        </div>
      </div>
    )
  }

  const { session, messages } = data

  return (
    <div className="flex flex-col flex-1 min-h-0">
      <div className="border-b border-gray-200 bg-white px-6 py-3 flex items-center gap-3 shrink-0">
        <button
          onClick={() => navigate('/')}
          className="text-gray-400 hover:text-gray-600 transition-colors"
        >
          <svg className="h-5 w-5" viewBox="0 0 20 20" fill="currentColor">
            <path
              fillRule="evenodd"
              d="M17 10a.75.75 0 01-.75.75H5.612l4.158 3.96a.75.75 0 11-1.04 1.08l-5.5-5.25a.75.75 0 010-1.08l5.5-5.25a.75.75 0 111.04 1.08L5.612 9.25H16.25A.75.75 0 0117 10z"
              clipRule="evenodd"
            />
          </svg>
        </button>
        <div>
          <h2 className="text-sm font-semibold text-gray-900">{session.title}</h2>
          <div className="text-xs text-gray-400">
            {session.status === 'active' ? 'Active' : 'Crystallized'}
            {' · '}
            {new Date(session.createdAt).toLocaleString()}
          </div>
        </div>
      </div>

      {streamError && (
        <div className="mx-6 mt-3 rounded-md bg-red-50 px-3 py-2 text-xs text-red-600">
          {streamError}
        </div>
      )}

      <ExploreChat
        messages={messages || []}
        streamingContent={streamContent}
        streamingToolCalls={streamToolCalls}
        streamingIssueId={streamIssueId}
        isStreaming={streaming}
      />

      <ExploreInput onSend={handleSend} disabled={streaming} />
    </div>
  )
}
