import { useState, useMemo } from 'react'
import { useNavigate } from 'react-router-dom'
import { useArchivedIssues } from '../../../entities/issue'
import { useProject, useProjectPath } from '../../../entities/project'
import { getLabelStyle, sortLabels } from '../../../shared/lib/label-colors'
import { formatRelativeTime } from '../../../shared/lib/relative-time'
import { useDocumentTitle } from '../../../shared/lib/useDocumentTitle'
import { Button } from '@/shared/ui/components/button'
import { Input } from '@/shared/ui/components/input'

export function ArchivedPage() {
  const { projectId } = useProject()
  const navigate = useNavigate()
  const toProjectPath = useProjectPath()
  const { data: archivedIssues, isLoading } = useArchivedIssues(
    projectId ? { projectId } : undefined,
  )
  const [search, setSearch] = useState('')

  useDocumentTitle('Archived — Mohist')

  const sorted = useMemo(() => {
    if (!archivedIssues) return []
    return [...archivedIssues].sort((a, b) => {
      const aTime = a.archivedAt ? new Date(a.archivedAt).getTime() : 0
      const bTime = b.archivedAt ? new Date(b.archivedAt).getTime() : 0
      return bTime - aTime
    })
  }, [archivedIssues])

  const filtered = useMemo(() => {
    if (!search.trim()) return sorted
    const q = search.toLowerCase()
    return sorted.filter((issue) => issue.title.toLowerCase().includes(q))
  }, [sorted, search])

  return (
    <div className="flex-1 overflow-y-auto">
      <div className="max-w-3xl mx-auto px-6 py-6">
        <Button
          variant="link"
          onClick={() => navigate(toProjectPath())}
          className="mb-4 inline-flex h-auto gap-1 px-0 text-muted-foreground"
        >
          <svg className="h-4 w-4" viewBox="0 0 20 20" fill="currentColor">
            <path
              fillRule="evenodd"
              d="M17 10a.75.75 0 01-.75.75H5.612l4.158 3.96a.75.75 0 11-1.04 1.08l-5.5-5.25a.75.75 0 010-1.08l5.5-5.25a.75.75 0 111.04 1.08L5.612 9.25H16.25A.75.75 0 0117 10z"
              clipRule="evenodd"
            />
          </svg>
          Back to board
        </Button>

        <div className="flex items-center justify-between mb-4">
          <h1 className="text-xl font-bold text-gray-900">Archived Issues</h1>
          {sorted.length > 0 && (
            <span className="text-sm text-gray-500">{sorted.length} archived</span>
          )}
        </div>

        {sorted.length > 0 && (
          <div className="mb-4">
            <Input
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="Search archived issues..."
            />
          </div>
        )}

        {isLoading ? (
          <div className="flex items-center justify-center py-12">
            <div className="text-gray-400">Loading...</div>
          </div>
        ) : sorted.length === 0 ? (
          <div className="flex items-center justify-center py-12">
            <div className="text-center">
              <div className="text-gray-400 text-lg mb-1">No archived issues</div>
              <div className="text-gray-400 text-sm">
                Completed issues that you archive will appear here.
              </div>
            </div>
          </div>
        ) : filtered.length === 0 ? (
          <div className="flex items-center justify-center py-12">
            <div className="text-gray-400">No issues match your search.</div>
          </div>
        ) : (
          <div className="space-y-3">
            {filtered.map((issue) => {
              const sortedLabels = sortLabels(issue.labels)
              return (
                <a
                  key={`${issue.projectId}:${issue.number}`}
                  href={toProjectPath(`/issues/${issue.number}`)}
                  className="block rounded-lg border border-gray-200 bg-white shadow-sm hover:border-gray-300 hover:shadow-md transition-colors p-4"
                >
                  <div className="flex items-start justify-between gap-3">
                    <div className="flex-1 min-w-0">
                      <div className="flex items-center gap-2 mb-1">
                        <span className="text-xs font-mono text-gray-400">
                          #{issue.number}
                        </span>
                      </div>
                      <h3 className="text-sm font-medium text-gray-900 truncate">
                        {issue.title}
                      </h3>
                      {sortedLabels.length > 0 && (
                        <div className="mt-2 flex items-center gap-1 flex-nowrap overflow-hidden">
                          {sortedLabels.map((label) => {
                            const s = getLabelStyle(label)
                            return (
                              <span
                                key={label}
                                className={`inline-block rounded-full px-1.5 font-medium whitespace-nowrap ${
                                  s.size === 'sm' ? 'text-[10px] py-px' : 'text-xs py-0.5'
                                }`}
                                style={{ backgroundColor: s.bg, color: s.text }}
                              >
                                {label}
                              </span>
                            )
                          })}
                        </div>
                      )}
                      <div className="mt-2 flex items-center gap-3 text-[10px] text-gray-400">
                        {(issue.completedAt ?? issue.updatedAt) && (
                          <span>Completed {formatRelativeTime(issue.completedAt ?? issue.updatedAt)}</span>
                        )}
                        {issue.archivedAt && (
                          <span>Archived {formatRelativeTime(issue.archivedAt)}</span>
                        )}
                      </div>
                    </div>
                  </div>
                </a>
              )
            })}
          </div>
        )}
      </div>
    </div>
  )
}
