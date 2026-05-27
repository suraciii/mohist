import { useState, useEffect } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Dialog } from '../../../shared/ui/Dialog'
import { api } from '../../../shared/api/client'
import { useLabels } from '../../../entities/project/api/queries'
import type { Issue } from '../../../shared/api/types'
import { getPriorityStyle } from '../../../shared/lib/label-colors'

const PRIORITIES = ['p0', 'p1', 'p2', 'p3', 'p4']

interface Props {
  open: boolean
  onClose: () => void
  issue: Issue
}

export function EditIssueDialog({ open, onClose, issue }: Props) {
  const [title, setTitle] = useState(issue.title)
  const [body, setBody] = useState(issue.body ?? '')
  const [labels, setLabels] = useState<string[]>(issue.labels)
  const [priority, setPriority] = useState<string>(issue.priority ?? 'p2')
  const queryClient = useQueryClient()
  const { data: allLabels } = useLabels()

  useEffect(() => {
    if (open) {
      setTitle(issue.title)
      setBody(issue.body ?? '')
      setLabels(issue.labels)
      setPriority(issue.priority ?? 'p2')
    }
  }, [open, issue])

  const mutation = useMutation({
    mutationFn: () => {
      const add = labels.filter((l) => !issue.labels.includes(l))
      const remove = issue.labels.filter((l) => !labels.includes(l))
      return api.updateIssue(issue.number, {
        title,
        body: body || undefined,
        ...(add.length > 0 ? { addLabels: add } : {}),
        ...(remove.length > 0 ? { removeLabels: remove } : {}),
        priority,
      })
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
      onClose()
    },
  })

  function toggleLabel(label: string) {
    setLabels((prev) =>
      prev.includes(label) ? prev.filter((l) => l !== label) : [...prev, label],
    )
  }

  return (
    <Dialog open={open} onClose={onClose} title={`Edit Issue #${issue.number}`}>
      <div className="space-y-3">
        <div>
          <label className="block text-xs font-medium text-gray-700 mb-1">Title</label>
          <input
            type="text"
            value={title}
            onChange={(e) => setTitle(e.target.value)}
            className="w-full rounded-md border border-gray-300 px-3 py-2 text-sm text-gray-900 placeholder-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
            autoFocus
          />
        </div>

        <div>
          <label className="block text-xs font-medium text-gray-700 mb-1">Description</label>
          <textarea
            value={body}
            onChange={(e) => setBody(e.target.value)}
            placeholder="Optional description"
            rows={4}
            className="w-full rounded-md border border-gray-300 px-3 py-2 text-sm text-gray-900 placeholder-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 resize-none"
          />
        </div>

        {allLabels && allLabels.length > 0 && (
          <div>
            <label className="block text-xs font-medium text-gray-700 mb-1">Labels</label>
            <div className="flex flex-wrap gap-1.5">
              {allLabels.map((label) => (
                <button
                  key={label}
                  type="button"
                  onClick={() => toggleLabel(label)}
                  className={`rounded-full px-2.5 py-0.5 text-xs font-medium transition-colors ${
                    labels.includes(label)
                      ? 'bg-blue-100 text-blue-700 ring-1 ring-blue-300'
                      : 'bg-gray-100 text-gray-600 hover:bg-gray-200'
                  }`}
                >
                  {label}
                </button>
              ))}
            </div>
          </div>
        )}

        <div>
          <label className="block text-xs font-medium text-gray-700 mb-1">Priority</label>
          <div className="flex gap-1.5">
            {PRIORITIES.map((p) => {
              const style = getPriorityStyle(p)
              return (
                <button
                  key={p}
                  type="button"
                  onClick={() => setPriority(p)}
                  className={`rounded-full px-2.5 py-0.5 text-xs font-medium transition-colors ${
                    priority === p
                      ? 'ring-1 ring-offset-1'
                      : 'hover:opacity-80'
                  }`}
                  style={{
                    backgroundColor: style.bg,
                    color: style.text,
                    ...(priority === p ? { ringColor: style.text } : {}),
                  }}
                >
                  {p.toUpperCase()}
                </button>
              )
            })}
          </div>
        </div>

        {mutation.error && (
          <div className="rounded-md bg-red-50 px-3 py-2 text-xs text-red-600">
            {mutation.error.message}
          </div>
        )}

        <div className="flex justify-end gap-2 pt-1">
          <button
            onClick={onClose}
            className="rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 hover:bg-gray-50 transition-colors"
          >
            Cancel
          </button>
          <button
            onClick={() => mutation.mutate()}
            disabled={!title.trim() || mutation.isPending}
            className="rounded-md bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50 transition-colors"
          >
            {mutation.isPending ? 'Saving...' : 'Save'}
          </button>
        </div>
      </div>
    </Dialog>
  )
}
