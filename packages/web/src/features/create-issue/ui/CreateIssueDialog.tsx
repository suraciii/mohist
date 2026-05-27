import { useState, useMemo, useRef, useCallback, Fragment } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Popover, Transition } from '@headlessui/react'
import { Dialog } from '../../../shared/ui/Dialog'
import { api } from '../../../shared/api/client'
import { useLabels } from '../../../entities/issue/api/queries'
import { useAvailableModelIds } from '../../../entities/settings/api/queries'
import { useProject } from '../../../entities/project/model/ProjectContext'
import { getPriorityStyle } from '../../../shared/lib/label-colors'

const PRIORITIES = ['p0', 'p1', 'p2', 'p3', 'p4']

interface Props {
  open: boolean
  onClose: () => void
}

function SearchIcon() {
  return (
    <svg className="h-4 w-4 text-gray-400" viewBox="0 0 20 20" fill="currentColor">
      <path
        fillRule="evenodd"
        d="M9 3.5a5.5 5.5 0 100 11 5.5 5.5 0 000-11zM2 9a7 7 0 1112.452 4.391l3.328 3.329a.75.75 0 11-1.06 1.06l-3.329-3.328A7 7 0 012 9z"
        clipRule="evenodd"
      />
    </svg>
  )
}

function ChevronDownIcon() {
  return (
    <svg className="h-4 w-4" viewBox="0 0 20 20" fill="currentColor">
      <path
        fillRule="evenodd"
        d="M5.23 7.21a.75.75 0 011.06.02L10 11.168l3.71-3.938a.75.75 0 111.08 1.04l-4.25 4.5a.75.75 0 01-1.08 0l-4.25-4.5a.75.75 0 01.02-1.06z"
        clipRule="evenodd"
      />
    </svg>
  )
}

function XIcon({ className }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 20 20" fill="currentColor">
      <path d="M6.28 5.22a.75.75 0 00-1.06 1.06L8.94 10l-3.72 3.72a.75.75 0 101.06 1.06L10 11.06l3.72 3.72a.75.75 0 101.06-1.06L11.06 10l3.72-3.72a.75.75 0 00-1.06-1.06L10 8.94 6.28 5.22z" />
    </svg>
  )
}

function ModelPresetSelect({ value, onChange, onClear }: { value: string | null; onChange: (id: string) => void; onClear: () => void }) {
  const { data: availableModelIds, isLoading } = useAvailableModelIds()
  const [search, setSearch] = useState('')
  const [highlightedIndex, setHighlightedIndex] = useState(0)
  const searchRef = useRef<HTMLInputElement>(null)

  const allModels: string[] = availableModelIds ?? []

  const filtered = useMemo(() => {
    if (!search.trim()) return allModels
    const q = search.toLowerCase()
    return allModels.filter(id => id.toLowerCase().includes(q) || (id.split('/').pop() || '').toLowerCase().includes(q))
  }, [allModels, search])

  const handleKeyDown = useCallback(
    (e: React.KeyboardEvent) => {
      if (e.key === 'ArrowDown') {
        e.preventDefault()
        setHighlightedIndex(i => Math.min(i + 1, filtered.length - 1))
      } else if (e.key === 'ArrowUp') {
        e.preventDefault()
        setHighlightedIndex(i => Math.max(i - 1, 0))
      } else if (e.key === 'Enter') {
        e.preventDefault()
        const m = filtered[highlightedIndex]
        if (m) onChange(m)
      }
    },
    [filtered, highlightedIndex, onChange],
  )

  const displayText = value
    ? (value.split('/').pop() || value)
    : 'Use default'

  return (
    <div className="flex items-center gap-2">
      <Popover as="div" className="relative flex-1">
        {({ open }) => (
          <>
            <Popover.Button
              className={`w-full inline-flex items-center justify-between gap-1.5 rounded-md border px-3 py-2 text-sm font-medium transition-colors min-h-[44px] md:min-h-0 ${
                open
                  ? 'border-blue-500 bg-blue-50 text-blue-700'
                  : value
                    ? 'border-blue-200 bg-blue-50 text-blue-700'
                    : 'border-gray-300 bg-white text-gray-500 hover:bg-gray-50'
              }`}
            >
              <span className="truncate">{displayText}</span>
              <ChevronDownIcon />
            </Popover.Button>

            <Transition
              as={Fragment}
              enter="transition ease-out duration-100"
              enterFrom="transform opacity-0 scale-95"
              enterTo="transform opacity-100 scale-100"
              leave="transition ease-in duration-75"
              leaveFrom="transform opacity-100 scale-100"
              leaveTo="transform opacity-0 scale-95"
            >
              <Popover.Panel portal={false} className="fixed inset-x-2 top-auto z-50 mt-1 md:absolute md:inset-x-auto md:right-0 md:w-72 origin-top-right rounded-lg bg-white shadow-lg ring-1 ring-black/5 focus:outline-none">
                <div className="p-2">
                  <div className="relative">
                    <div className="absolute left-3 top-1/2 -translate-y-1/2">
                      <SearchIcon />
                    </div>
                    <input
                      ref={searchRef}
                      type="text"
                      value={search}
                      onChange={e => setSearch(e.target.value)}
                      onKeyDown={handleKeyDown}
                      placeholder="Search models..."
                      className="w-full rounded-md border border-gray-300 pl-9 pr-3 py-1.5 text-sm text-gray-900 placeholder-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                      autoFocus
                    />
                  </div>
                </div>
                <div className="max-h-60 overflow-y-auto border-t border-gray-100">
                  {isLoading && (
                    <div className="px-3 py-4 text-center text-sm text-gray-400">Loading...</div>
                  )}
                  {!isLoading && filtered.length === 0 && (
                    <div className="px-3 py-4 text-center text-sm text-gray-400">No models found</div>
                  )}
                  {!isLoading && filtered.map((modelId, i) => (
                    <button
                      key={modelId}
                      onClick={() => onChange(modelId)}
                      onMouseEnter={() => setHighlightedIndex(i)}
                      className={`w-full flex items-center justify-between px-3 py-1.5 text-sm transition-colors ${
                        i === highlightedIndex
                          ? 'bg-blue-50 text-blue-700'
                          : modelId === value
                            ? 'bg-gray-50 text-gray-900'
                            : 'text-gray-700 hover:bg-gray-50'
                      }`}
                    >
                      <div className="flex flex-col items-start">
                        <span className="font-medium">{modelId.split('/').pop() || modelId}</span>
                        <span className="text-xs text-gray-400">{modelId}</span>
                      </div>
                    </button>
                  ))}
                </div>
              </Popover.Panel>
            </Transition>
          </>
        )}
      </Popover>
      {value && (
        <button
          onClick={onClear}
          className="inline-flex items-center justify-center p-2 text-gray-400 hover:text-red-500 hover:bg-red-50 rounded-md transition-colors"
          title="Clear"
        >
          <XIcon className="h-4 w-4" />
        </button>
      )}
    </div>
  )
}

export function CreateIssueDialog({ open, onClose }: Props) {
  const [title, setTitle] = useState('')
  const [body, setBody] = useState('')
  const [labels, setLabels] = useState<string[]>([])
  const [model, setModel] = useState<string | null>(null)
  const [priority, setPriority] = useState<string>('p2')
  const { projectId } = useProject()
  const queryClient = useQueryClient()
  const { data: allLabels } = useLabels()

  const mutation = useMutation({
    mutationFn: () =>
      api.createIssue({
        title,
        body: body || undefined,
        labels: labels.length > 0 ? labels : undefined,
        ...(model ? { model } : {}),
        ...(projectId ? { projectId } : {}),
        priority,
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
    setModel(null)
    setPriority('p2')
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

        <div>
          <label className="block text-xs font-medium text-gray-700 mb-1">Coder Model</label>
          <ModelPresetSelect
            value={model}
            onChange={setModel}
            onClear={() => setModel(null)}
          />
        </div>

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
