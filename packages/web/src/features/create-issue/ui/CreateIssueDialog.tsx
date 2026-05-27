import { useState, useMemo, useRef, useCallback } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
} from '@/shared/ui/components/dialog'
import { Button } from '@/shared/ui/components/button'
import { Input } from '@/shared/ui/components/input'
import { Textarea } from '@/shared/ui/components/textarea'
import {
  Popover,
  PopoverContent,
  PopoverTrigger,
} from '@/shared/ui/components/popover'
import { createIssue, useLabels } from '../../../entities/issue'
import { useAvailableModelIds } from '../../../entities/settings'
import { useProject } from '../../../entities/project'
import { getPriorityStyle } from '../../../shared/lib/label-colors'

const PRIORITIES = ['p0', 'p1', 'p2', 'p3', 'p4']

interface Props {
  open: boolean
  onClose: () => void
}

function SearchIcon() {
  return (
    <svg className="h-4 w-4 text-muted-foreground" viewBox="0 0 20 20" fill="currentColor">
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
  const [popoverOpen, setPopoverOpen] = useState(false)
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
        if (m) {
          onChange(m)
          setPopoverOpen(false)
        }
      }
    },
    [filtered, highlightedIndex, onChange],
  )

  const displayText = value
    ? (value.split('/').pop() || value)
    : 'Use default'

  return (
    <div className="flex items-center gap-2">
      <Popover open={popoverOpen} onOpenChange={setPopoverOpen}>
        <PopoverTrigger>
          <Button
            variant="outline"
            className={`w-full inline-flex items-center justify-between gap-1.5 text-sm font-medium min-h-[44px] md:min-h-0 ${
              value
                ? 'border-blue-200 bg-blue-50 text-blue-700'
                : ''
            }`}
          >
            <span className="truncate">{displayText}</span>
            <ChevronDownIcon />
          </Button>
        </PopoverTrigger>
        <PopoverContent className="w-72 p-0" align="end">
          <div className="p-2">
            <div className="relative">
              <div className="absolute left-3 top-1/2 -translate-y-1/2">
                <SearchIcon />
              </div>
              <Input
                ref={searchRef}
                type="text"
                value={search}
                onChange={e => setSearch(e.target.value)}
                onKeyDown={handleKeyDown}
                placeholder="Search models..."
                className="pl-9"
                autoFocus
              />
            </div>
          </div>
          <div className="max-h-60 overflow-y-auto border-t">
            {isLoading && (
              <div className="px-3 py-4 text-center text-sm text-muted-foreground">Loading...</div>
            )}
            {!isLoading && filtered.length === 0 && (
              <div className="px-3 py-4 text-center text-sm text-muted-foreground">No models found</div>
            )}
            {!isLoading && filtered.map((modelId, i) => (
              <Button
                key={modelId}
                variant="ghost"
                onClick={() => {
                  onChange(modelId)
                  setPopoverOpen(false)
                }}
                onMouseEnter={() => setHighlightedIndex(i)}
                className={`w-full justify-between rounded-none h-auto px-3 py-1.5 ${
                  i === highlightedIndex
                    ? 'bg-blue-50 text-blue-700'
                    : modelId === value
                      ? 'bg-muted text-foreground'
                      : 'text-foreground hover:bg-muted'
                }`}
              >
                <div className="flex flex-col items-start">
                  <span className="font-medium">{modelId.split('/').pop() || modelId}</span>
                  <span className="text-xs text-muted-foreground">{modelId}</span>
                </div>
              </Button>
            ))}
          </div>
        </PopoverContent>
      </Popover>
      {value && (
        <Button
          variant="ghost"
          size="icon"
          onClick={onClear}
          className="text-muted-foreground hover:text-red-500 hover:bg-red-50"
          title="Clear"
        >
          <XIcon className="h-4 w-4" />
        </Button>
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
      createIssue({
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
    <Dialog open={open} onOpenChange={(v) => !v && resetAndClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Create Issue</DialogTitle>
        </DialogHeader>
        <div className="space-y-3">
          <div>
            <label className="block text-xs font-medium text-foreground mb-1">Title *</label>
            <Input
              type="text"
              value={title}
              onChange={(e) => setTitle(e.target.value)}
              placeholder="Issue title"
              autoFocus
            />
          </div>

          <div>
            <label className="block text-xs font-medium text-foreground mb-1">Description</label>
            <Textarea
              value={body}
              onChange={(e) => setBody(e.target.value)}
              placeholder="Optional description"
              rows={3}
              className="resize-none"
            />
          </div>

          {allLabels && allLabels.length > 0 && (
            <div>
              <label className="block text-xs font-medium text-foreground mb-1">Labels</label>
              <div className="flex flex-wrap gap-1.5">
                {allLabels.map((label) => (
                  <Button
                    key={label}
                    type="button"
                    variant="ghost"
                    size="xs"
                    onClick={() => toggleLabel(label)}
                    className={`rounded-full ${
                      labels.includes(label)
                        ? 'bg-blue-100 text-blue-700 ring-1 ring-blue-300'
                        : 'bg-muted text-muted-foreground hover:bg-muted/80'
                    }`}
                  >
                    {label}
                  </Button>
                ))}
              </div>
            </div>
          )}

          <div>
            <label className="block text-xs font-medium text-foreground mb-1">Coder Model</label>
            <ModelPresetSelect
              value={model}
              onChange={setModel}
              onClear={() => setModel(null)}
            />
          </div>

          <div>
            <label className="block text-xs font-medium text-foreground mb-1">Priority</label>
            <div className="flex gap-1.5">
              {PRIORITIES.map((p) => {
                const style = getPriorityStyle(p)
                return (
                  <Button
                    key={p}
                    type="button"
                    variant="ghost"
                    size="xs"
                    onClick={() => setPriority(p)}
                    className={`rounded-full ${
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
                  </Button>
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
            <Button
              variant="outline"
              onClick={resetAndClose}
            >
              Cancel
            </Button>
            <Button
              onClick={() => mutation.mutate()}
              disabled={!title.trim() || mutation.isPending}
              className="min-h-[44px]"
            >
              {mutation.isPending ? 'Creating...' : 'Create'}
            </Button>
          </div>
        </div>
      </DialogContent>
    </Dialog>
  )
}
