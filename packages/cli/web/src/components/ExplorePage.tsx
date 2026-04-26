import { useCallback, useState, useRef, useEffect } from 'react'
import { useParams, useNavigate } from 'react-router-dom'
import { useExploreSession, useStatus, useUpdateExploreSessionTitle } from '../hooks/useQueries'
import { useExploreStream } from '../hooks/useExploreStream'
import { useQueryClient } from '@tanstack/react-query'
import { ExploreChat } from './ExploreChat'
import { ExploreInput } from './ExploreInput'
import { ModelSelector } from './ModelSelector'

function LlmGuidanceCard() {
  const navigate = useNavigate()

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
            <p className="text-amber-800 text-sm font-medium mb-2">To get started:</p>
            <ol className="list-decimal list-inside text-amber-700 text-sm space-y-1">
              <li>Go to Settings page</li>
              <li>Connect a provider (e.g., Anthropic, OpenAI, or GLM)</li>
              <li>Return here to start exploring</li>
            </ol>
          </div>

          <button
            onClick={() => navigate('/settings')}
            className="w-full flex items-center justify-center gap-2 bg-amber-600 hover:bg-amber-700 text-white text-sm font-medium py-2 px-4 rounded transition-colors"
          >
            <svg className="h-4 w-4" viewBox="0 0 20 20" fill="currentColor">
              <path fillRule="evenodd" d="M12.586 4.586a2 2 0 112.828 2.828l-3 3a2 2 0 01-2.828 0 1 1 0 00-1.414 1.414 4 4 0 005.656 0l3-3a4 4 0 00-5.656-5.656l-1.5 1.5a1 1 0 101.414 1.414l1.5-1.5zm-5 5a2 2 0 012.828 0 1 1 0 101.414-1.414 4 4 0 00-5.656 0l-3 3a4 4 0 105.656 5.656l1.5-1.5a1 1 0 10-1.414-1.414l-1.5 1.5a2 2 0 11-2.828-2.828l3-3z" clipRule="evenodd" />
            </svg>
            Go to Settings
          </button>
        </div>
      </div>
    </div>
  )
}

function EditableTitle({ title, sessionId }: { title: string; sessionId: string }) {
  const updateTitle = useUpdateExploreSessionTitle()
  const [isEditing, setIsEditing] = useState(false)
  const [editValue, setEditValue] = useState(title)
  const inputRef = useRef<HTMLInputElement>(null)

  useEffect(() => {
    setEditValue(title)
  }, [title])

  useEffect(() => {
    if (isEditing && inputRef.current) {
      inputRef.current.select()
    }
  }, [isEditing])

  const handleSave = useCallback(() => {
    const trimmed = editValue.trim()
    if (trimmed && trimmed !== title) {
      updateTitle.mutate({ sessionId, title: trimmed })
    } else {
      setEditValue(title)
    }
    setIsEditing(false)
  }, [editValue, title, sessionId, updateTitle])

  const handleKeyDown = useCallback(
    (e: React.KeyboardEvent) => {
      if (e.key === 'Enter') {
        e.preventDefault()
        handleSave()
      } else if (e.key === 'Escape') {
        setEditValue(title)
        setIsEditing(false)
      }
    },
    [handleSave, title],
  )

  if (isEditing) {
    return (
      <input
        ref={inputRef}
        type="text"
        value={editValue}
        onChange={(e) => setEditValue(e.target.value)}
        onBlur={handleSave}
        onKeyDown={handleKeyDown}
        className="text-sm font-semibold text-gray-900 bg-white border border-blue-400 rounded px-1 outline-none w-full"
        autoFocus
      />
    )
  }

  return (
    <h2
      className="text-sm font-semibold text-gray-900 cursor-pointer hover:text-blue-600 transition-colors"
      onDoubleClick={() => setIsEditing(true)}
      title="Double-click to edit title"
    >
      {title}
    </h2>
  )
}

export function ExplorePage() {
  const { id } = useParams<{ id: string }>()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const { data, isLoading } = useExploreSession(id || '')
  const { data: statusData } = useStatus()
  const {
    streaming,
    streamContent,
    streamToolCalls,
    streamIssueId,
    streamError,
    streamUpdatedTitle,
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

  const displayTitle = streamUpdatedTitle || session.title

  const llmConfigured = statusData?.llm?.configured !== false

  return (
    <div className="flex flex-col flex-1 min-h-0">
      <div className="border-b border-gray-200 bg-white px-4 md:px-6 py-3 flex items-center gap-3 shrink-0">
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
        <div className="flex-1 min-w-0">
          <EditableTitle title={displayTitle} sessionId={session.id} />
          <div className="text-xs text-gray-400">
            {session.status === 'active' ? 'Active' : 'Crystallized'}
            {' · '}
            {new Date(session.createdAt).toLocaleString()}
          </div>
        </div>
        {llmConfigured && (
          <ModelSelector
            sessionId={session.id}
            currentModel={session.model}
            currentVariant={session.variant}
          />
        )}
      </div>

      {!llmConfigured && (
        <LlmGuidanceCard />
      )}

      {llmConfigured && streamError && (
        <div className="mx-4 md:mx-6 mt-3 rounded-md bg-red-50 px-3 py-2 text-xs text-red-600">
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
