import { useMemo, useRef, useState } from 'react'
import {
  Popover,
  PopoverContent,
  PopoverTrigger,
} from '@/shared/ui/components/popover'
import { Input } from '@/shared/ui/components/input'
import { Button } from '@/shared/ui/components/button'
import { statusBadge, statusLabel } from '../../../entities/issue/lib/status-badge'
import { IssueStatus, IssueHealth, type Issue, type IssueStartBlocker } from '../../../entities/issue'
import { useIssues } from '../../../entities/issue'

export interface IssuePrerequisitePickerProps {
  projectId: string
  excludeNumbers: number[]
  selected: number[]
  mode: 'buffer' | 'live'
  onAdd: (number: number) => Promise<void> | void
  onRemove: (number: number) => Promise<void> | void
  renderChips?: boolean
  canStart?: boolean
  blocker?: IssueStartBlocker | null
  disabled?: boolean
  errorMessage?: string | null
}

function isCompleted(issue: Issue): boolean {
  return issue.status === IssueStatus.Done
}

function statusBadgeForStatus(status: IssueStatus | IssueHealth | string): string {
  if (Object.values(IssueHealth).includes(status as IssueHealth)) {
    return statusBadge(status as IssueHealth)
  }
  switch (status as IssueStatus) {
    case IssueStatus.Done:
      return statusBadge(IssueHealth.Done)
    case IssueStatus.Cancelled:
      return statusBadge(IssueHealth.Cancelled)
    case IssueStatus.InProgress:
      return 'text-blue-700 bg-blue-50'
    case IssueStatus.Backlog:
    default:
      return statusBadge(IssueHealth.Paused)
  }
}

function statusLabelForStatus(status: IssueStatus | IssueHealth | string): string {
  if (Object.values(IssueHealth).includes(status as IssueHealth)) {
    return statusLabel(status as IssueHealth)
  }
  switch (status as IssueStatus) {
    case IssueStatus.Done:
      return statusLabel(IssueHealth.Done)
    case IssueStatus.Cancelled:
      return statusLabel(IssueHealth.Cancelled)
    case IssueStatus.InProgress:
      return 'In Progress'
    case IssueStatus.Backlog:
    default:
      return 'Backlog'
  }
}

function blockerDescription(blocker: IssueStartBlocker | null | undefined): string | null {
  if (!blocker) return null
  if (blocker.kind === 'draft') return 'Cannot start: draft issue'
  if (blocker.kind === 'waiting-for') return `Cannot start: waiting on #${blocker.issue.number}`
  return null
}

export function IssuePrerequisitePicker({
  projectId,
  excludeNumbers,
  selected,
  mode,
  onAdd,
  onRemove,
  renderChips = true,
  canStart,
  blocker,
  disabled = false,
  errorMessage,
}: IssuePrerequisitePickerProps) {
  const issuesQuery = useIssues({ projectId })
  const allIssues: Issue[] = issuesQuery.data ?? []

  const excludeSet = useMemo(() => new Set(excludeNumbers), [excludeNumbers])
  const selectedSet = useMemo(() => new Set(selected), [selected])

  const issuesByNumber = useMemo(() => {
    const map = new Map<number, Issue>()
    for (const issue of allIssues) {
      map.set(issue.number, issue)
    }
    return map
  }, [allIssues])

  const [open, setOpen] = useState(false)
  const [search, setSearch] = useState('')
  const searchRef = useRef<HTMLInputElement | null>(null)
  const [pendingError, setPendingError] = useState<string | null>(null)

  const candidatePool = useMemo(() => {
    return allIssues.filter(issue => !excludeSet.has(issue.number))
  }, [allIssues, excludeSet])

  const filteredCandidates = useMemo(() => {
    const term = search.trim().toLowerCase()
    if (!term) return candidatePool
    return candidatePool.filter(issue => {
      if (issue.title.toLowerCase().includes(term)) return true
      return String(issue.number).includes(term)
    })
  }, [candidatePool, search])

  async function handleSelect(issue: Issue) {
    if (selectedSet.has(issue.number)) return
    setPendingError(null)
    try {
      await onAdd(issue.number)
      setSearch('')
    } catch (err) {
      setPendingError((err as Error).message ?? 'Failed to add prerequisite')
    }
  }

  async function handleRemoveChip(number: number) {
    setPendingError(null)
    try {
      await onRemove(number)
    } catch (err) {
      setPendingError((err as Error).message ?? 'Failed to remove prerequisite')
    }
  }

  const triggerLabel = open
    ? 'Search by number or title…'
    : selected.length > 0
      ? `${selected.length} prerequisite${selected.length === 1 ? '' : 's'} selected`
      : 'Add prerequisites'

  const showReadiness = typeof canStart === 'boolean' || blocker !== undefined
  const readinessText = canStart === false
    ? (blockerDescription(blocker) ?? 'Cannot start: prerequisites incomplete')
    : canStart === true
      ? 'Ready to start'
      : null

  return (
    <div className="space-y-2" data-testid="issue-prerequisite-picker">
      <Popover open={open} onOpenChange={(next) => { setOpen(next); if (!next) setPendingError(null) }}>
        <PopoverTrigger
          render={
            <Button
              type="button"
              variant="outline"
              data-testid="prerequisite-picker-trigger"
              role="combobox"
              aria-haspopup="listbox"
              aria-expanded={open}
              disabled={disabled}
              className="w-full justify-between gap-1.5 min-h-[36px]"
            />
          }
        >
          <span className="truncate text-muted-foreground">{triggerLabel}</span>
          <svg className="h-4 w-4 shrink-0 text-muted-foreground" viewBox="0 0 20 20" fill="currentColor">
            <path fillRule="evenodd" d="M5.23 7.21a.75.75 0 011.06.02L10 11.168l3.71-3.938a.75.75 0 111.08 1.04l-4.25 4.5a.75.75 0 01-1.08 0l-4.25-4.5a.75.75 0 01.02-1.06z" clipRule="evenodd" />
          </svg>
        </PopoverTrigger>
        <PopoverContent className="w-[var(--radix-popover-trigger-width)] p-0" align="start">
          <div className="p-2">
            <div className="relative">
              <div className="absolute left-3 top-1/2 -translate-y-1/2">
                <svg className="h-4 w-4 text-muted-foreground" viewBox="0 0 20 20" fill="currentColor">
                  <path fillRule="evenodd" d="M9 3.5a5.5 5.5 0 100 11 5.5 5.5 0 000-11zM2 9a7 7 0 1112.452 4.391l3.328 3.329a.75.75 0 11-1.06 1.06l-3.329-3.328A7 7 0 012 9z" clipRule="evenodd" />
                </svg>
              </div>
              <Input
                ref={searchRef}
                type="text"
                value={search}
                onChange={(e) => setSearch(e.target.value)}
                placeholder="Search by number or title…"
                data-testid="prerequisite-picker-search"
                className="pl-9"
              />
            </div>
          </div>
          <div
            className="max-h-64 overflow-y-auto border-t"
            role="listbox"
            data-testid="prerequisite-picker-listbox"
          >
            {issuesQuery.isLoading ? (
              <div className="px-3 py-6 text-center text-sm text-muted-foreground">
                Loading issues…
              </div>
            ) : candidatePool.length === 0 ? (
              <div className="px-3 py-6 text-center text-sm text-muted-foreground">
                No issues available
              </div>
            ) : filteredCandidates.length === 0 ? (
              <div className="px-3 py-6 text-center text-sm text-muted-foreground">
                No issues match &ldquo;{search}&rdquo;
              </div>
            ) : (
              filteredCandidates.map(issue => (
                <button
                  key={issue.number}
                  type="button"
                  role="option"
                  aria-selected={selectedSet.has(issue.number)}
                  data-testid="prerequisite-picker-option"
                  data-issue-number={issue.number}
                  onClick={() => handleSelect(issue)}
                  className="flex w-full items-start justify-between gap-2 px-3 py-2 text-left text-sm transition-colors hover:bg-muted"
                >
                  <span className="min-w-0 flex-1 truncate">
                    <span className="font-mono">#{issue.number}</span>
                    <span className="mx-1 text-muted-foreground">·</span>
                    <span>{issue.title}</span>
                    <span className="mx-1 text-muted-foreground">·</span>
                    <span className="text-xs text-muted-foreground">{statusLabelForStatus(issue.status)}</span>
                  </span>
                  <span
                    data-testid="prerequisite-picker-option-badge"
                    className={`shrink-0 rounded px-1.5 py-0.5 text-[11px] font-medium ${statusBadgeForStatus(issue.health)}`}
                  >
                    {statusLabelForStatus(issue.health)}
                  </span>
                </button>
              ))
            )}
          </div>
        </PopoverContent>
      </Popover>

      {renderChips && selected.length > 0 && (
        <div
          className="flex flex-wrap gap-1.5"
          data-testid="prerequisite-picker-chips"
          data-mode={mode}
        >
          {selected.map((number) => {
            const issue = issuesByNumber.get(number)
            const incomplete = issue ? !isCompleted(issue) : false
            const label = issue ? `#${number} · ${issue.title}` : `#${number}`
            return (
              <span
                key={number}
                data-testid="prerequisite-picker-chip"
                data-issue-number={number}
                data-incomplete={incomplete ? 'true' : 'false'}
                className={`inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-xs ${
                  incomplete
                    ? 'border-warning-border bg-warning-subtle text-warning'
                    : 'border-success-border bg-success-subtle text-success'
                }`}
              >
                <span className="truncate max-w-[12rem]">{label}</span>
                {incomplete && (
                  <span
                    data-testid="prerequisite-picker-chip-incomplete-indicator"
                    aria-label="incomplete prerequisite"
                    className="ml-1 inline-flex h-1.5 w-1.5 rounded-full bg-warning"
                  />
                )}
                <Button
                  type="button"
                  variant="ghost"
                  size="icon-xs"
                  aria-label={`Remove prerequisite #${number}`}
                  data-testid="prerequisite-picker-chip-remove"
                  onClick={() => handleRemoveChip(number)}
                  className="-mr-1 h-4 w-4 p-0 text-current hover:bg-transparent"
                >
                  ×
                </Button>
              </span>
            )
          })}
        </div>
      )}

      {showReadiness && readinessText && (
        <p
          data-testid="prerequisite-picker-readiness"
          data-can-start={canStart === true ? 'true' : canStart === false ? 'false' : undefined}
          className={`text-xs ${
            canStart ? 'text-success' : 'text-warning'
          }`}
        >
          {readinessText}
        </p>
      )}

      {(pendingError ?? errorMessage) && (
        <p
          data-testid="prerequisite-picker-error"
          className="text-xs text-danger"
          role="alert"
        >
          {pendingError ?? errorMessage}
        </p>
      )}
    </div>
  )
}