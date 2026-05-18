import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { useEpics } from '../hooks/useQueries'
import { EpicStatus, type EpicWithProgress } from '../lib/types'
import { EpicCreateDialog } from './EpicCreateDialog'

function PriorityBadge({ priority }: { priority: string }) {
  const colors: Record<string, string> = {
    p0: 'bg-red-100 text-red-700',
    p1: 'bg-orange-100 text-orange-700',
    p2: 'bg-yellow-100 text-yellow-700',
    p3: 'bg-blue-100 text-blue-700',
    p4: 'bg-gray-100 text-gray-700',
  }
  return (
    <span className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium ${colors[priority] || 'bg-gray-100 text-gray-700'}`}>
      {priority.toUpperCase()}
    </span>
  )
}

function StatusBadge({ status }: { status: EpicStatus }) {
  const colors: Record<EpicStatus, string> = {
    [EpicStatus.Active]: 'bg-green-100 text-green-700',
    [EpicStatus.Done]: 'bg-blue-100 text-blue-700',
    [EpicStatus.Closed]: 'bg-gray-100 text-gray-700',
  }
  const labels: Record<EpicStatus, string> = {
    [EpicStatus.Active]: 'Active',
    [EpicStatus.Done]: 'Done',
    [EpicStatus.Closed]: 'Closed',
  }
  return (
    <span className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium ${colors[status]}`}>
      {labels[status]}
    </span>
  )
}

function EpicCard({ epic }: { epic: EpicWithProgress }) {
  const navigate = useNavigate()
  const { progress } = epic

  return (
    <div
      className="bg-white rounded-lg border border-gray-200 p-4 hover:border-gray-300 transition-colors cursor-pointer"
      onClick={() => navigate(`/epic/${epic.id}`)}
    >
      <div className="flex items-start justify-between gap-3">
        <div className="flex-1 min-w-0">
          <div className="flex items-center gap-2 mb-1">
            <span className="text-sm font-medium text-gray-500">#{epic.id.slice(0, 8)}</span>
            <StatusBadge status={epic.status} />
            <PriorityBadge priority={epic.priority} />
          </div>
          <h3 className="text-base font-semibold text-gray-900 truncate">{epic.title}</h3>
        </div>
      </div>

      <div className="mt-3">
        <div className="flex items-center justify-between text-sm mb-1">
          <span className="text-gray-500">Progress</span>
          <span className="font-medium text-gray-900">
            {progress.deliveredCount} / {progress.totalIssueCount} delivered
          </span>
        </div>
        <div className="w-full bg-gray-100 rounded-full h-1.5">
          <div
            className="bg-blue-600 h-1.5 rounded-full transition-all"
            style={{
              width: progress.totalIssueCount > 0
                ? `${(progress.deliveredCount / progress.totalIssueCount) * 100}%`
                : '0%'
            }}
          />
        </div>
      </div>

      <div className="mt-3 flex items-center justify-between">
        <div className="text-sm">
          {progress.nextIssue ? (
            <span className="text-gray-500">
              Next: <span className="text-gray-700 font-medium">#{progress.nextIssue.number}</span>
              <span className="text-gray-400 ml-1">{progress.nextIssue.title}</span>
            </span>
          ) : progress.readyToMarkDone ? (
            <span className="text-green-600 font-medium">Ready to mark done</span>
          ) : (
            <span className="text-gray-400">No linked issues</span>
          )}
        </div>
      </div>
    </div>
  )
}

export function EpicListPage() {
  const { data: epics, isLoading } = useEpics()
  const [showCreate, setShowCreate] = useState(false)

  const activeEpics = epics?.filter(e => e.status === EpicStatus.Active) ?? []
  const doneEpics = epics?.filter(e => e.status === EpicStatus.Done) ?? []
  const closedEpics = epics?.filter(e => e.status === EpicStatus.Closed) ?? []

  return (
    <div className="max-w-4xl mx-auto p-6">
      <div className="flex items-center justify-between mb-6">
        <h1 className="text-2xl font-bold text-gray-900">Epics</h1>
        <button
          onClick={() => setShowCreate(true)}
          className="inline-flex items-center gap-1.5 rounded-md bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700 transition-colors"
        >
          <svg className="h-4 w-4" viewBox="0 0 20 20" fill="currentColor">
            <path d="M10.75 4.75a.75.75 0 00-1.5 0v4.5h-4.5a.75.75 0 000 1.5h4.5v4.5a.75.75 0 001.5 0v-4.5h4.5a.75.75 0 000-1.5h-4.5v-4.5z" />
          </svg>
          New Epic
        </button>
      </div>

      {isLoading ? (
        <div className="flex items-center justify-center py-12">
          <div className="text-gray-400">Loading...</div>
        </div>
      ) : epics && epics.length === 0 ? (
        <div className="text-center py-12">
          <div className="text-gray-400 text-lg mb-4">No epics yet</div>
          <button
            onClick={() => setShowCreate(true)}
            className="px-4 py-2 bg-blue-600 text-white rounded hover:bg-blue-700 text-sm"
          >
            Create your first Epic
          </button>
        </div>
      ) : (
        <div className="space-y-8">
          {activeEpics.length > 0 && (
            <section>
              <h2 className="text-sm font-semibold text-gray-500 uppercase tracking-wide mb-3">Active</h2>
              <div className="grid gap-4">
                {activeEpics.map(epic => (
                  <EpicCard key={epic.id} epic={epic} />
                ))}
              </div>
            </section>
          )}

          {doneEpics.length > 0 && (
            <section>
              <h2 className="text-sm font-semibold text-gray-500 uppercase tracking-wide mb-3">Done</h2>
              <div className="grid gap-4">
                {doneEpics.map(epic => (
                  <EpicCard key={epic.id} epic={epic} />
                ))}
              </div>
            </section>
          )}

          {closedEpics.length > 0 && (
            <section>
              <h2 className="text-sm font-semibold text-gray-500 uppercase tracking-wide mb-3">Closed</h2>
              <div className="grid gap-4">
                {closedEpics.map(epic => (
                  <EpicCard key={epic.id} epic={epic} />
                ))}
              </div>
            </section>
          )}
        </div>
      )}

      <EpicCreateDialog open={showCreate} onClose={() => setShowCreate(false)} />
    </div>
  )
}