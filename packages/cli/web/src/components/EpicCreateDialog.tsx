import { useState } from 'react'
import { useCreateEpic } from '../hooks/useQueries'
import { Dialog } from './Dialog'
import type { EpicPriority } from '../lib/types'

interface EpicCreateDialogProps {
  open: boolean
  onClose: () => void
}

const PRIORITIES: { value: EpicPriority; label: string }[] = [
  { value: 'p0', label: 'P0 - Critical' },
  { value: 'p1', label: 'P1 - High' },
  { value: 'p2', label: 'P2 - Medium' },
  { value: 'p3', label: 'P3 - Low' },
  { value: 'p4', label: 'P4 - Nice to have' },
]

export function EpicCreateDialog({ open, onClose }: EpicCreateDialogProps) {
  const [title, setTitle] = useState('')
  const [description, setDescription] = useState('')
  const [priority, setPriority] = useState<EpicPriority>('p2')
  const createEpic = useCreateEpic()

  function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    if (!title.trim()) return

    createEpic.mutate(
      { title: title.trim(), description, priority },
      {
        onSuccess: () => {
          setTitle('')
          setDescription('')
          setPriority('p2')
          onClose()
        },
      }
    )
  }

  function handleClose() {
    setTitle('')
    setDescription('')
    setPriority('p2')
    onClose()
  }

  return (
    <Dialog open={open} onClose={handleClose} title="Create Epic">
      <form onSubmit={handleSubmit} className="space-y-4">
        <div>
          <label htmlFor="epic-title" className="block text-sm font-medium text-gray-700 mb-1">
            Title
          </label>
          <input
            id="epic-title"
            type="text"
            value={title}
            onChange={e => setTitle(e.target.value)}
            placeholder="e.g., Workflow runtime model cleanup"
            className="w-full rounded-md border border-gray-300 px-3 py-2 text-sm placeholder:text-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
            required
          />
        </div>

        <div>
          <label htmlFor="epic-description" className="block text-sm font-medium text-gray-700 mb-1">
            Description
          </label>
          <textarea
            id="epic-description"
            value={description}
            onChange={e => setDescription(e.target.value)}
            placeholder="Describe the goal and scope of this epic..."
            rows={4}
            className="w-full rounded-md border border-gray-300 px-3 py-2 text-sm placeholder:text-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
            required
          />
        </div>

        <div>
          <label htmlFor="epic-priority" className="block text-sm font-medium text-gray-700 mb-1">
            Priority
          </label>
          <select
            id="epic-priority"
            value={priority}
            onChange={e => setPriority(e.target.value as EpicPriority)}
            className="w-full rounded-md border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
          >
            {PRIORITIES.map(p => (
              <option key={p.value} value={p.value}>
                {p.label}
              </option>
            ))}
          </select>
        </div>

        {createEpic.isError && (
          <div className="rounded-md bg-red-50 px-3 py-2 text-xs text-red-600">
            {createEpic.error?.message || 'Failed to create epic'}
          </div>
        )}

        <div className="flex justify-end gap-2 pt-2">
          <button
            type="button"
            onClick={handleClose}
            className="rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 hover:bg-gray-50 transition-colors"
          >
            Cancel
          </button>
          <button
            type="submit"
            disabled={createEpic.isPending || !title.trim()}
            className="rounded-md bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50 transition-colors"
          >
            {createEpic.isPending ? 'Creating...' : 'Create Epic'}
          </button>
        </div>
      </form>
    </Dialog>
  )
}