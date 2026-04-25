import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Dialog } from './Dialog'
import { api } from '../lib/api'
import { useLabels } from '../hooks/useQueries'
import { useProject } from '../context/ProjectContext'

interface Props {
  open: boolean
  onClose: () => void
}

export function CreateIssueDialog({ open, onClose }: Props) {
  const [title, setTitle] = useState('')
  const [body, setBody] = useState('')
  const [labels, setLabels] = useState<string[]>([])
  const { projectId } = useProject()
  const queryClient = useQueryClient()
  const { data: allLabels } = useLabels()

  const mutation = useMutation({
    mutationFn: () =>
      api.createIssue({
        title,
        body: body || undefined,
        labels: labels.length > 0 ? labels : undefined,
        ...(projectId ? { projectId } : {}),
      }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      resetAndClose()
    },
  })

  function resetAndClose() {
    setTitle('')
    setBody('')
    setLabels([])
    onClose()
  }

  function toggleLabel(label: string) {
    setLabels((prev) =>
      prev.includes(label) ? prev.filter((l) => l !== label) : [...prev, label],
    )
  }

  return (
    <Dialog open={open} onClose={resetAndClose} title="Create Issue">
      <div className="space-y-3">
        <div>
          <label className="block text-xs font-medium text-gray-700 mb-1">Title *</label>
          <input
            type="text"
            value={title}
            onChange={(e) => setTitle(e.target.value)}
            placeholder="Issue title"
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
            rows={3}
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

        {mutation.error && (
          <div className="rounded-md bg-red-50 px-3 py-2 text-xs text-red-600">
            {mutation.error.message}
          </div>
        )}

        <div className="flex justify-end gap-2 pt-1">
          <button
            onClick={resetAndClose}
            className="rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 hover:bg-gray-50 transition-colors"
          >
            Cancel
          </button>
          <button
            onClick={() => mutation.mutate()}
            disabled={!title.trim() || mutation.isPending}
            className="rounded-md bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50 transition-colors min-h-[44px]"
          >
            {mutation.isPending ? 'Creating...' : 'Create'}
          </button>
        </div>
      </div>
    </Dialog>
  )
}
