import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { useProject } from '../context/ProjectContext'
import { useExploreSessions, useCreateExploreSession } from '../hooks/useQueries'
import { api } from '../lib/api'
import { useQueryClient } from '@tanstack/react-query'
import type { ExploreSession } from '../lib/types'

function SessionCard({ session, onDelete }: { session: ExploreSession; onDelete: (s: ExploreSession) => void }) {
  const navigate = useNavigate()

  const isActive = session.status === 'active' || session.status === 'crystallized'
  const timeAgo = formatTimeAgo(new Date(session.updatedAt))

  return (
    <div
      onClick={() => navigate(`/explore/${session.id}`)}
      className="bg-white border border-gray-200 rounded-lg p-4 hover:border-gray-300 hover:shadow-sm cursor-pointer transition-all group"
    >
      <div className="flex items-start justify-between gap-3">
        <div className="flex-1 min-w-0">
          <h3 className="text-sm font-medium text-gray-900 truncate">
            {session.title || 'Untitled Session'}
          </h3>
          <div className="flex items-center gap-2 mt-1.5 text-xs text-gray-400">
            <span className={`inline-flex items-center px-1.5 py-0.5 rounded text-[10px] font-medium ${
              isActive
                ? 'bg-green-50 text-green-700'
                : 'bg-gray-50 text-gray-500'
            }`}>
              {isActive ? 'Active' : 'Archived'}
            </span>
            {session.issueNumber != null && (
              <button
                onClick={(e) => {
                  e.stopPropagation()
                  navigate(`/issue/${session.issueNumber}`)
                }}
                className="inline-flex items-center gap-1 text-blue-600 hover:text-blue-700"
              >
                <svg className="h-3 w-3" viewBox="0 0 16 16" fill="currentColor">
                  <path d="M8 9.5a1.5 1.5 0 100-3 1.5 1.5 0 000 3z" />
                  <path fillRule="evenodd" d="M8 0a8 8 0 100 16A8 8 0 008 0zM1.5 8a6.5 6.5 0 1113 0 6.5 6.5 0 01-13 0z" clipRule="evenodd" />
                </svg>
                Issue #{session.issueNumber}
              </button>
            )}
            <span>{timeAgo}</span>
          </div>
        </div>
        <button
          onClick={(e) => {
            e.stopPropagation()
            onDelete(session)
          }}
          className="opacity-0 group-hover:opacity-100 p-1 text-gray-300 hover:text-red-500 transition-all"
          title="Delete session"
        >
          <svg className="h-4 w-4" viewBox="0 0 20 20" fill="currentColor">
            <path fillRule="evenodd" d="M9 2a1 1 0 00-.894.553L7.382 4H4a1 1 0 000 2v10a2 2 0 002 2h8a2 2 0 002-2V6a1 1 0 100-2h-3.382l-.724-1.447A1 1 0 0011 2H9zM7 8a1 1 0 012 0v6a1 1 0 11-2 0V8zm5-1a1 1 0 00-1 1v6a1 1 0 102 0V8a1 1 0 00-1-1z" clipRule="evenodd" />
          </svg>
        </button>
      </div>
    </div>
  )
}

function DeleteConfirmDialog({ session, onConfirm, onCancel }: { session: ExploreSession; onConfirm: () => void; onCancel: () => void }) {
  return (
    <div className="fixed inset-0 bg-black/30 flex items-center justify-center z-50" onClick={onCancel}>
      <div className="bg-white rounded-lg shadow-xl max-w-sm w-full mx-4 p-6" onClick={(e) => e.stopPropagation()}>
        <h3 className="text-sm font-semibold text-gray-900 mb-2">Delete Session</h3>
        <p className="text-sm text-gray-500 mb-4">
          Are you sure you want to delete &ldquo;{session.title || 'Untitled Session'}&rdquo;? This action cannot be undone.
        </p>
        <div className="flex justify-end gap-2">
          <button
            onClick={onCancel}
            className="px-3 py-1.5 text-sm text-gray-600 hover:text-gray-800"
          >
            Cancel
          </button>
          <button
            onClick={onConfirm}
            className="px-3 py-1.5 text-sm bg-red-600 text-white rounded hover:bg-red-700"
          >
            Delete
          </button>
        </div>
      </div>
    </div>
  )
}

function formatTimeAgo(date: Date): string {
  const now = new Date()
  const diffMs = now.getTime() - date.getTime()
  const diffMin = Math.floor(diffMs / 60000)
  const diffHr = Math.floor(diffMin / 60)
  const diffDay = Math.floor(diffHr / 24)

  if (diffMin < 1) return 'just now'
  if (diffMin < 60) return `${diffMin}m ago`
  if (diffHr < 24) return `${diffHr}h ago`
  if (diffDay < 30) return `${diffDay}d ago`
  return date.toLocaleDateString()
}

export function ExploreSessionList() {
  const { projectId } = useProject()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const { data: sessions, isLoading } = useExploreSessions(projectId || '')
  const createSession = useCreateExploreSession()
  const [deleteTarget, setDeleteTarget] = useState<ExploreSession | null>(null)
  const [deleting, setDeleting] = useState(false)

  const handleCreate = () => {
    if (!projectId || createSession.isPending) return
    createSession.mutate(
      { projectId, title: 'New Exploration' },
      {
        onSuccess: (session) => {
          navigate(`/explore/${session.id}`)
        },
      },
    )
  }

  const handleDelete = async () => {
    if (!deleteTarget) return
    setDeleting(true)
    try {
      await api.deleteExploreSession(deleteTarget.id)
      queryClient.invalidateQueries({ queryKey: ['explore-sessions'] })
      setDeleteTarget(null)
    } catch {
      // error handled by ui state
    } finally {
      setDeleting(false)
    }
  }

  if (isLoading) {
    return (
      <div className="flex items-center justify-center flex-1">
        <div className="text-gray-400">Loading...</div>
      </div>
    )
  }

  const sorted = [...(sessions ?? [])].sort(
    (a, b) => new Date(b.updatedAt).getTime() - new Date(a.updatedAt).getTime(),
  )

  return (
    <div className="flex flex-col flex-1">
      <div className="border-b border-gray-200 bg-white px-6 py-3 flex items-center justify-between shrink-0">
        <h2 className="text-sm font-semibold text-gray-900">Explore Sessions</h2>
        <button
          onClick={handleCreate}
          disabled={createSession.isPending}
          className="inline-flex items-center gap-1.5 px-3 py-1.5 bg-blue-600 text-white text-xs font-medium rounded hover:bg-blue-700 disabled:opacity-50 transition-colors"
        >
          <svg className="h-3.5 w-3.5" viewBox="0 0 20 20" fill="currentColor">
            <path fillRule="evenodd" d="M10 3a1 1 0 011 1v5h5a1 1 0 110 2h-5v5a1 1 0 11-2 0v-5H4a1 1 0 110-2h5V4a1 1 0 011-1z" clipRule="evenodd" />
          </svg>
          New Session
        </button>
      </div>

      <div className="flex-1 overflow-y-auto p-6">
        {sorted.length === 0 ? (
          <div className="flex items-center justify-center h-full">
            <div className="text-center">
              <div className="text-gray-400 text-lg mb-4">No explore sessions yet</div>
              <p className="text-gray-400 text-sm mb-4">
                Start a new exploration to brainstorm and refine ideas with AI.
              </p>
              <button
                onClick={handleCreate}
                disabled={createSession.isPending}
                className="inline-flex items-center gap-1.5 px-4 py-2 bg-blue-600 text-white text-sm font-medium rounded hover:bg-blue-700 disabled:opacity-50 transition-colors"
              >
                <svg className="h-4 w-4" viewBox="0 0 20 20" fill="currentColor">
                  <path fillRule="evenodd" d="M10 3a1 1 0 011 1v5h5a1 1 0 110 2h-5v5a1 1 0 11-2 0v-5H4a1 1 0 110-2h5V4a1 1 0 011-1z" clipRule="evenodd" />
                </svg>
                New Session
              </button>
            </div>
          </div>
        ) : (
          <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-3">
            {sorted.map((session) => (
              <SessionCard
                key={session.id}
                session={session}
                onDelete={setDeleteTarget}
              />
            ))}
          </div>
        )}
      </div>

      {deleteTarget && (
        <DeleteConfirmDialog
          session={deleteTarget}
          onConfirm={handleDelete}
          onCancel={() => setDeleteTarget(null)}
        />
      )}

      {deleting && (
        <div className="fixed inset-0 bg-black/10 flex items-center justify-center z-40 pointer-events-none">
          <div className="bg-white rounded-lg px-4 py-2 shadow text-sm text-gray-600">Deleting...</div>
        </div>
      )}
    </div>
  )
}
