import React from 'react'
import { Button } from '@/shared/ui/components/button'
import type { DisplayTurn, DisplayChangedFile } from '../model/session-transcript-display'
import { formatElapsed } from '../model/format-duration'
import type { TurnRefsMap } from '../model/turn-refs'
import { promptKindLabel } from '../model/prompt-kind-labels'
import { PromptBlock } from './PromptBlock'
import { AssistantParts } from './AssistantParts'

function formatTime(iso: string): string {
  return new Date(iso).toLocaleTimeString()
}

interface TurnListProps {
  turns: DisplayTurn[]
  turnRefs?: TurnRefsMap
  isRunning?: boolean
  now?: number
}

export function TurnList({ turns, turnRefs, isRunning, now }: TurnListProps) {
  return (
    <div
      role="log"
      className="space-y-6 min-w-0"
    >
      {turns.map((turn, index) => (
        <TurnItem
          key={turn.id}
          turn={turn}
          index={index + 1}
          registerRef={turnRefs ? (el) => {
            if (el) {
              turnRefs.set(index + 1, el)
            } else {
              turnRefs.delete(index + 1)
            }
          } : undefined}
          isRunning={isRunning}
          now={now}
        />
      ))}
    </div>
  )
}

interface TurnItemProps {
  turn: DisplayTurn
  index: number
  registerRef?: (el: HTMLDivElement | null) => void
  isRunning?: boolean
  now?: number
}

export function TurnItem({ turn, index, registerRef, isRunning, now }: TurnItemProps) {
  return (
    <div
      ref={registerRef}
      data-turn-id={turn.id}
      data-turn-ref=""
      className="space-y-3 min-w-0"
      style={{ contentVisibility: 'auto', containIntrinsicSize: '3rem' }}
    >
      <TurnDivider
        index={index}
        kind={turn.prompt.kind}
        startedAt={turn.startedAt}
        completedAt={turn.completedAt}
      />
      <div className="min-w-0">
        <PromptBlock prompt={turn.prompt} />
      </div>

      {turn.assistantParts.length > 0 && (
        <div className="min-w-0">
          <AssistantParts parts={turn.assistantParts} isRunning={isRunning} now={now} />
        </div>
      )}

      {turn.changedFiles.length > 0 && (
        <TurnDiffs files={turn.changedFiles} />
      )}
    </div>
  )
}

interface TurnDividerProps {
  index: number
  kind: DisplayTurn['prompt']['kind']
  startedAt: string
  completedAt: string | null
}

function TurnDivider({ index, kind, startedAt, completedAt }: TurnDividerProps) {
  const duration = formatElapsed(startedAt, completedAt)
  return (
    <div
      data-turn-divider=""
      data-turn-index={index}
      className="border-t border-border pt-2 mt-2 first:border-t-0 first:pt-0 first:mt-0"
    >
      <div className="flex flex-wrap items-center gap-x-1.5 gap-y-0.5 text-xs text-muted-foreground">
        <span data-turn-index-label="" className="font-medium text-foreground/80">
          Turn {index}
        </span>
        <span aria-hidden="true">·</span>
        <span data-turn-kind-label="" className="text-foreground/70">
          {promptKindLabel(kind)}
        </span>
        <span aria-hidden="true">·</span>
        <time dateTime={startedAt} data-turn-timestamp="" title={startedAt}>
          {formatTime(startedAt)}
        </time>
        {duration && (
          <>
            <span aria-hidden="true">·</span>
            <span data-turn-duration="" className="text-muted-foreground/70">
              {duration}
            </span>
          </>
        )}
      </div>
    </div>
  )
}

interface TurnDiffsProps {
  files: DisplayChangedFile[]
}

export function TurnDiffs({ files }: TurnDiffsProps) {
  const [expanded, setExpanded] = React.useState(false)
  const count = files.length

  return (
    <div className="min-w-0 rounded-md border border-green-200 bg-green-50/50 overflow-hidden">
      <Button
        variant="ghost"
        size="sm"
        onClick={() => setExpanded(!expanded)}
        aria-expanded={expanded}
        className="flex h-auto items-center justify-start gap-2 w-full text-left px-3 py-1.5 rounded-none hover:bg-green-100/50 transition-colors"
      >
        <svg aria-hidden="true" className="h-3.5 w-3.5 text-green-600 shrink-0" viewBox="0 0 20 20" fill="currentColor">
          <path fillRule="evenodd" d="M4 4a2 2 0 012-2h4.586A2 2 0 0112 2.586L15.414 6A2 2 0 0116 7.414V16a2 2 0 01-2 2H6a2 2 0 01-2-2V4zm2 6a1 1 0 011-1h6a1 1 0 110 2H7a1 1 0 01-1-1zm1 3a1 1 0 100 2h6a1 1 0 100-2H7z" clipRule="evenodd" />
        </svg>
        <span className="text-xs font-medium text-green-700">
          {count === 1 ? '1 file changed' : `${count} files changed`}
        </span>
        {count <= 3 && (
          <span className="text-xs text-green-600/70 truncate">
            {files.map(c => c.path.split('/').pop()).join(', ')}
          </span>
        )}
        <svg aria-hidden="true" className={`h-3 w-3 text-green-400 shrink-0 ml-auto transition-transform ${expanded ? 'rotate-90' : ''}`} viewBox="0 0 20 20" fill="currentColor">
          <path fillRule="evenodd" d="M7.21 14.77a.75.75 0 01.02-1.06L11.168 10 7.23 6.29a.75.75 0 111.04-1.08l4.5 4.25a.75.75 0 010 1.08l-4.5 4.25a.75.75 0 01-1.06-.02z" clipRule="evenodd" />
        </svg>
      </Button>
      {expanded && (
        <div className="border-t border-green-200/50 px-3 py-2 space-y-1">
          {files.map((change, i) => {
            const opBadge: Record<string, string> = { created: 'A', modified: 'M', deleted: 'D', moved: 'R' }
            const opColor: Record<string, string> = { created: 'bg-green-100 text-green-700', modified: 'bg-blue-100 text-blue-700', deleted: 'bg-red-100 text-red-700', moved: 'bg-purple-100 text-purple-700' }
            return (
              <div key={i} className="flex items-center gap-2 py-0.5">
                <span className={`inline-flex items-center px-1 py-0.5 rounded text-xs font-medium ${opColor[change.operation] ?? ''}`}>
                  {opBadge[change.operation] ?? '?'}
                </span>
                <span className="text-xs font-mono text-gray-700 truncate flex-1">{change.path}</span>
                {change.operation === 'moved' && change.oldPath && (
                  <span className="text-xs text-gray-400">← {change.oldPath}</span>
                )}
                {change.additions !== undefined && change.additions > 0 && (
                  <span className="text-xs text-green-600">+{change.additions}</span>
                )}
                {change.deletions !== undefined && change.deletions > 0 && (
                  <span className="text-xs text-red-600">-{change.deletions}</span>
                )}
              </div>
            )
          })}
        </div>
      )}
    </div>
  )
}