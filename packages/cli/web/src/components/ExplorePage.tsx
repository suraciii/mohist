import { useCallback } from 'react'
import { useParams, useNavigate } from 'react-router-dom'
import { useExploreSession, useStatus } from '../hooks/useQueries'
import { useExploreStream } from '../hooks/useExploreStream'
import { useQueryClient } from '@tanstack/react-query'
import { ExploreChat } from './ExploreChat'
import { ExploreInput } from './ExploreInput'

function LlmGuidanceCard({ onRefresh }: { onRefresh: () => void }) {
  return (
    <div className="flex-1 flex items-center justify-center p-8">
      <div className="max-w-lg w-full bg-amber-50 border border-amber-200 rounded-lg p-6">
        <div className="flex items-start gap-3 mb-4">
          <svg className="h-5 w-5 text-amber-600 mt-0.5 shrink-0" viewBox="0 0 20 20" fill="currentColor">
            <path fillRule="evenodd" d="M8.485 2.495c.673-1.167 2.357-1.167 3.03 0l6.28 10.875c.673 1.167-.17 2.625-1.516 2.625H3.72c-1.347 0-2.189-1.458-1.515-2.625L8.485 2.495zM10 5a.75.75 0 01.75.75v3.5a.75.75 0 01-1.5 0v-3.5A.75.75 0 0110 5zm0 9a1 1 0 100-2 1 1 0 000 2z" clipRule="evenodd" />
          </svg>
          <div>
            <h3 className="text-amber-800 font-semibold">LLM Provider Not Configured</h3>
            <p className="text-amber-700 text-sm mt-1">
              To use the Explore feature, you need to configure an LLM provider.
            </p>
          </div>
        </div>

        <div className="space-y-4">
          <div>
            <p className="text-amber-800 text-sm font-medium mb-2">Configuration file:</p>
            <code className="block bg-amber-100 text-amber-900 text-xs rounded px-2 py-1.5">
              ~/.mohist/config.jsonc
            </code>
          </div>

          <div>
            <p className="text-amber-800 text-sm font-medium mb-2">Supported providers:</p>
            <div className="flex gap-2">
              {['anthropic', 'glm', 'openai'].map(p => (
                <span key={p} className="bg-amber-200 text-amber-900 text-xs px-2 py-1 rounded">
                  {p}
                </span>
              ))}
            </div>
          </div>

          <div>
            <p className="text-amber-800 text-sm font-medium mb-2">Configuration example:</p>
            <pre className="bg-amber-100 text-amber-900 text-xs rounded p-2 overflow-x-auto">{`{
  // Anthropic (recommended)
  "ANTHROPIC_API_KEY": "sk-...",
  
  // or GLM
  "ZHIPUAI_API_KEY": "...",
  
  // or OpenAI
  "OPENAI_API_KEY": "...",
  
  // Select provider & model
  "model": "anthropic/claude-sonnet-4-20250514"
}`}</pre>
          </div>

          <button
            onClick={onRefresh}
            className="w-full flex items-center justify-center gap-2 bg-amber-600 hover:bg-amber-700 text-white text-sm font-medium py-2 px-4 rounded transition-colors"
          >
            <svg className="h-4 w-4" viewBox="0 0 20 20" fill="currentColor">
              <path fillRule="evenodd" d="M4 2a1 1 0 011 1v2.101a7.002 7.002 0 0111.601 2.566 1 1 0 11-1.885.666A5.002 5.002 0 005.999 7H9a1 1 0 010 2H4a1 1 0 01-1-1V3a1 1 0 011-1zm.008 9.057a1 1 0 011.276.61A5.002 5.002 0 0014.001 13H11a1 1 0 110-2h5a1 1 0 011 1v5a1 1 0 11-2 0v-2.101a7.002 7.002 0 01-11.601-2.566 1 1 0 01.61-1.276z" clipRule="evenodd" />
            </svg>
            Refresh
          </button>
        </div>
      </div>
    </div>
  )
}

export function ExplorePage() {
  const { id } = useParams<{ id: string }>()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const { data, isLoading } = useExploreSession(id || '')
  const { data: statusData, refetch: refetchStatus } = useStatus()
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

  const llmConfigured = statusData?.llm?.configured !== false

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

      {!llmConfigured && (
        <LlmGuidanceCard onRefresh={() => refetchStatus()} />
      )}

      {llmConfigured && streamError && (
        <div className="mx-6 mt-3 rounded-md bg-red-50 px-3 py-2 text-xs text-red-600">
          {streamError}
        </div>
      )}

      {llmConfigured && (
        <>
          <ExploreChat
            messages={messages || []}
            streamingContent={streamContent}
            streamingToolCalls={streamToolCalls}
            streamingIssueId={streamIssueId}
            isStreaming={streaming}
          />

          <ExploreInput onSend={handleSend} disabled={streaming} />
        </>
      )}
    </div>
  )
}
