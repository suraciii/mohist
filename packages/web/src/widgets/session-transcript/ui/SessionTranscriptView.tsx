import React, { useState } from 'react'
import Markdown from 'react-markdown'
import { Button } from '@/shared/ui/components/button'
import type { SessionTurn, TextPart, ReasoningPart, ErrorPart, ToolPart, PromptSummary, FileChangeSummary } from '../../../entities/coder-session'
import { ToolCallCard, getToolLabel, getToolArgs } from './ToolCallCard'
import type { ToolCallEntry } from '../../../entities/coder-session'

const CONTEXT_TOOL_NAMES = new Set(['read', 'glob', 'grep', 'list', 'membrowse', 'memread', 'memsearch'])

function isContextTool(toolName: string): boolean {
  return CONTEXT_TOOL_NAMES.has(toolName.toLowerCase())
}

function isTodowriteTool(toolName: string): boolean {
  return toolName.toLowerCase() === 'todowrite'
}

function getToolIdentity(part: ToolPart): string {
  const name = part.tool.normalizedName ?? part.tool.toolName
  if (name && name !== 'unknown') return name
  const title = part.tool.title
  if (title && /^[a-zA-Z_][a-zA-Z0-9_-]*$/.test(title)) return title
  return name ?? 'unknown'
}

function shouldGroupContextTool(parts: Array<TextPart | ReasoningPart | ErrorPart | ToolPart>, index: number): boolean {
  return parts[index]?.type === 'tool'
}

function formatTime(iso: string): string {
  return new Date(iso).toLocaleTimeString()
}

function formatDateTime(iso: string): string {
  return new Date(iso).toLocaleString()
}

const KIND_LABELS: Record<string, string> = {
  initial: 'Initial Task',
  task: 'Task',
  retry: 'Retry',
  followup: 'Follow-up',
  recovery: 'Recovery',
  'legacy-missing': 'Missing Prompt',
}

function PromptSummaryCard({
  summary,
  kind,
  sentAt,
  rawText,
}: {
  summary: PromptSummary | undefined
  kind: string
  sentAt: string
  rawText: string
}) {
  const [expanded, setExpanded] = useState(false)
  const [copied, setCopied] = useState(false)

  const title = summary?.title ?? ''
  const subtitle = summary?.subtitle ?? summary?.outputPath ?? ''
  const isLegacy = kind === 'legacy-missing'

  const handleCopy = () => {
    navigator.clipboard.writeText(rawText).then(() => {
      setCopied(true)
      setTimeout(() => setCopied(false), 2000)
    })
  }

  if (isLegacy) {
    return (
      <div className="flex justify-end">
        <div className="max-w-[80%] rounded-2xl rounded-br-sm bg-gray-400 text-white px-4 py-2.5 text-sm">
          <div className="flex items-center gap-2 text-xs text-gray-200 mb-1.5">
            <span className="font-medium">{KIND_LABELS[kind] ?? kind}</span>
            <span className="text-gray-300">·</span>
            <span>{formatDateTime(sentAt)}</span>
          </div>
          <p className="text-sm italic text-gray-100">
            Prompt was not recorded for this historical session
          </p>
        </div>
      </div>
    )
  }

  return (
    <div className="flex justify-end">
      <div className="max-w-[80%] rounded-2xl rounded-br-sm bg-blue-600 text-white px-4 py-2.5 text-sm">
        <div className="flex items-center gap-2 text-xs text-blue-200 mb-1.5">
          <span className="font-medium">{KIND_LABELS[kind] ?? kind}</span>
          <span className="text-blue-300">·</span>
          <span>{formatDateTime(sentAt)}</span>
        </div>

        <div className="mb-2 space-y-1">
          <p className="text-sm font-medium leading-relaxed">{title || 'Task prompt'}</p>
          {subtitle && <p className="text-xs text-blue-200">{subtitle}</p>}
          {summary?.outputPath && summary.outputPath !== subtitle && (
            <p className="text-xs text-blue-200">Output: {summary.outputPath}</p>
          )}
          {summary?.contextFiles && summary.contextFiles.length > 0 && (
            <p className="text-xs text-blue-100">
              Context: {summary.contextFiles.join(', ')}
            </p>
          )}
        </div>

        {expanded && (
          <pre className="whitespace-pre-wrap break-all text-sm leading-relaxed mt-2 border-t border-blue-500/40 pt-2">{rawText}</pre>
        )}

        <div className="flex items-center gap-2 mt-2">
          {!expanded && rawText && (
            <Button
              variant="link"
              onClick={() => setExpanded(true)}
              className="h-auto p-0 text-xs text-blue-200 hover:text-white transition-colors"
            >
              Show full prompt
            </Button>
          )}
          {expanded && (
            <Button
              variant="link"
              onClick={() => setExpanded(false)}
              className="h-auto p-0 text-xs text-blue-200 hover:text-white transition-colors"
            >
              Show less
            </Button>
          )}
          <Button
            variant="link"
            onClick={handleCopy}
            className="h-auto p-0 text-xs text-blue-200 hover:text-white transition-colors"
          >
            {copied ? 'Copied!' : 'Copy'}
          </Button>
        </div>
      </div>
    </div>
  )
}

function AssistantTextPartView({ part }: { part: TextPart }) {
  const [copied, setCopied] = useState(false)
  const isStreaming = part.completedAt === null
  const hasText = part.text.trim().length > 0

  const handleCopy = () => {
    navigator.clipboard.writeText(part.text).then(() => {
      setCopied(true)
      setTimeout(() => setCopied(false), 2000)
    })
  }

  return (
    <div className="max-w-[90%]">
      <div className="text-sm text-gray-800 leading-relaxed">
        <Markdown
          components={{
            code({ children, className }) {
              const match = /language-(\w+)/.exec(className ?? '')
              const isInline = !match && !className
              if (isInline) {
                return <code className="px-1 py-0.5 bg-gray-100 rounded text-gray-800 text-xs font-mono">{children}</code>
              }
              return (
                <code className={`${className ?? ''} block overflow-x-auto rounded bg-gray-50 p-3 text-xs font-mono`}>
                  {children}
                </code>
              )
            },
            pre({ children }) {
              return <pre className="overflow-x-auto rounded bg-gray-50 p-3 text-xs font-mono">{children}</pre>
            },
          }}
        >
          {part.text}
        </Markdown>
        {isStreaming && (
          <span className="inline-block h-4 w-0.5 bg-gray-800 ml-0.5 animate-pulse align-middle" />
        )}
      </div>
      {hasText && (
        <Button
          variant="link"
          onClick={handleCopy}
          className="mt-1 h-auto p-0 text-xs text-gray-400 hover:text-gray-700 transition-colors"
        >
          {copied ? 'Copied!' : 'Copy'}
        </Button>
      )}
    </div>
  )
}

function ReasoningPartView({ part }: { part: ReasoningPart }) {
  const [expanded, setExpanded] = useState(false)
  const sizeKB = (part.text.length / 1024).toFixed(1)

  return (
    <details className="max-w-[90%]">
      <summary
        onClick={(event) => {
          event.preventDefault()
          setExpanded(!expanded)
        }}
        className="text-xs text-gray-400 cursor-pointer hover:text-gray-600 select-none"
      >
        Thinking... {sizeKB}KB · {formatTime(part.startedAt)}
      </summary>
      {expanded && (
        <pre className="mt-1 text-xs text-gray-500 whitespace-pre-wrap break-all max-h-48 overflow-auto bg-gray-50 rounded p-2">
          {part.text}
        </pre>
      )}
    </details>
  )
}

function TurnFileChangesView({ changes }: { changes: FileChangeSummary[] }) {
  const [expanded, setExpanded] = useState(false)
  const count = changes.length

  return (
    <div className="max-w-[90%] rounded-md border border-green-200 bg-green-50/50 overflow-hidden">
      <Button
        variant="ghost"
        size="sm"
        onClick={() => setExpanded(!expanded)}
        className="flex h-auto items-center justify-start gap-2 w-full text-left px-3 py-1.5 rounded-none hover:bg-green-100/50 transition-colors"
      >
        <svg className="h-3.5 w-3.5 text-green-600 shrink-0" viewBox="0 0 20 20" fill="currentColor">
          <path fillRule="evenodd" d="M4 4a2 2 0 012-2h4.586A2 2 0 0112 2.586L15.414 6A2 2 0 0116 7.414V16a2 2 0 01-2 2H6a2 2 0 01-2-2V4zm2 6a1 1 0 011-1h6a1 1 0 110 2H7a1 1 0 01-1-1zm1 3a1 1 0 100 2h6a1 1 0 100-2H7z" clipRule="evenodd" />
        </svg>
        <span className="text-xs font-medium text-green-700">
          {count === 1 ? '1 file changed' : `${count} files changed`}
        </span>
        {count <= 3 && (
          <span className="text-xs text-green-600/70 truncate">
            {changes.map(c => c.path.split('/').pop()).join(', ')}
          </span>
        )}
        <svg className={`h-3 w-3 text-green-400 shrink-0 ml-auto transition-transform ${expanded ? 'rotate-90' : ''}`} viewBox="0 0 20 20" fill="currentColor">
          <path fillRule="evenodd" d="M7.21 14.77a.75.75 0 01.02-1.06L11.168 10 7.23 6.29a.75.75 0 111.04-1.08l4.5 4.25a.75.75 0 010 1.08l-4.5 4.25a.75.75 0 01-1.06-.02z" clipRule="evenodd" />
        </svg>
      </Button>
      {expanded && (
        <div className="border-t border-green-200/50 px-3 py-2 space-y-1">
          {changes.map((change, i) => {
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

function SessionErrorPartView({ part }: { part: ErrorPart }) {
  const messages: Record<string, string> = {
    timeout: '⏱️ Execution timed out',
    failed: '✗ Execution failed',
    cancelled: '⊘ Execution cancelled',
    recovery: '↻ Recovery in progress',
  }

  return (
    <div
      data-testid="session-error-part"
      data-tone="warning"
      className="flex items-center gap-1.5 text-xs text-warning"
    >
      <svg className="h-3 w-3 shrink-0" viewBox="0 0 20 20" fill="currentColor">
        <path fillRule="evenodd" d="M8.485 2.495c.673-1.167 2.357-1.167 3.03 0l6.28 10.875c.673 1.167-.17 2.625-1.516 2.625H3.72c-1.347 0-2.189-1.458-1.515-2.625L8.485 2.495zM10 5a.75.75 0 01.75.75v3.5a.75.75 0 01-1.5 0v-3.5A.75.75 0 0110 5zm0 9a1 1 0 100-2 1 1 0 000 2z" clipRule="evenodd" />
      </svg>
      <span>
        {messages[part.kind] ?? part.kind}
        {part.message && part.message !== (messages[part.kind] ?? part.kind) ? `: ${part.message}` : ''}
        {' · '}
        {formatTime(part.at)}
      </span>
    </div>
  )
}

function ToolPartView({ part }: { part: ToolPart }) {
  const toolName = getToolIdentity(part)
  const entry: ToolCallEntry = {
    executionId: '',
    toolName,
    state: part.tool.status,
    timestamp: part.tool.startedAt ? new Date(part.tool.startedAt).getTime() : Date.now(),
    toolCallId: part.tool.toolCallId,
    title: part.tool.displayTitle ?? part.tool.title ?? part.tool.target,
    rawInput: part.tool.input,
    rawOutput: part.tool.output,
    error: part.tool.error,
    duration: part.tool.completedAt && part.tool.startedAt
      ? new Date(part.tool.completedAt).getTime() - new Date(part.tool.startedAt).getTime()
      : undefined,
    changedFiles: part.tool.changedFiles,
  }

  return <ToolCallCard entry={entry} />
}

interface ContextGroupCardProps {
  tools: ToolPart[]
}

function ContextGroupCard({ tools }: ContextGroupCardProps) {
  const [expanded, setExpanded] = useState(false)

  const counts: Record<string, number> = {}
  let failedCount = 0

  for (const tool of tools) {
    const name = tool.tool.normalizedName ?? tool.tool.toolName
    if (!counts[name]) counts[name] = 0
    counts[name]++
    if (tool.tool.status === 'failed') failedCount++
  }

  const labelParts: string[] = []
  for (const [name, count] of Object.entries(counts)) {
    labelParts.push(count === 1 ? name : `${name} ${count}`)
  }
  const firstTool = tools[0]
  const firstToolName = firstTool ? getToolIdentity(firstTool) : undefined
  const firstToolLabel = firstToolName && firstTool
    ? firstTool.tool.displaySubtitle ?? firstTool.tool.target ?? getToolLabel(firstToolName, firstTool.tool.input)
    : undefined
  if (tools.length === 1 && firstToolLabel) {
    labelParts.push(firstToolLabel)
  }

  const failedLabel = failedCount > 0 ? ` · ${failedCount} failed` : ''
  const summary = `Gathering context · ${labelParts.join(' · ')}${failedLabel}`
  const expandedLabel = `Context gathered · ${labelParts.join(' · ')}${failedLabel}`

  return (
    <div className="rounded-md border border-gray-200 overflow-hidden">
      <Button
        variant="ghost"
        size="sm"
        onClick={() => setExpanded(!expanded)}
        className="flex h-auto items-center justify-start gap-2 w-full text-left px-3 py-1.5 rounded-none hover:bg-gray-50 transition-colors"
      >
        <svg className="h-3.5 w-3.5 text-gray-400 shrink-0" viewBox="0 0 20 20" fill="currentColor">
          <path d="M10 3a1.5 1.5 0 110 3 1.5 1.5 0 010-3zM7.5 4.5a1.5 1.5 0 110 3 1.5 1.5 0 010-3zm5 0a1.5 1.5 0 110 3 1.5 1.5 0 010-3z" />
        </svg>
        <span className="text-xs font-medium text-gray-700">{expanded ? expandedLabel : summary}</span>
        <span className="sr-only">Context gathered</span>
        {failedCount > 0 && (
          <span
            data-testid="context-group-failed-count"
            data-tone="danger"
            className="text-xs text-danger"
          >
            {failedCount} failed
          </span>
        )}
        <svg className={`h-3 w-3 text-gray-400 shrink-0 ml-auto transition-transform ${expanded ? 'rotate-90' : ''}`} viewBox="0 0 20 20" fill="currentColor">
          <path fillRule="evenodd" d="M7.21 14.77a.75.75 0 01.02-1.06L11.168 10 7.23 6.29a.75.75 0 111.04-1.08l4.5 4.25a.75.75 0 010 1.08l-4.5 4.25a.75.75 0 01-1.06-.02z" clipRule="evenodd" />
        </svg>
      </Button>
      {expanded && (
        <div className="px-3 pb-2 border-t border-gray-100 space-y-1.5">
          {tools.map((tool) => (
            <CompactContextTool key={tool.id} tool={tool} />
          ))}
        </div>
      )}
    </div>
  )
}

function CompactContextTool({ tool }: { tool: ToolPart }) {
  const toolName = getToolIdentity(tool)
  const input = tool.tool.input
  const label = tool.tool.displaySubtitle ?? tool.tool.target ?? getToolLabel(toolName, input)
  const args = getToolArgs(toolName, input)
  const duration = tool.tool.completedAt && tool.tool.startedAt
    ? new Date(tool.tool.completedAt).getTime() - new Date(tool.tool.startedAt).getTime()
    : undefined

  return (
    <div
      data-testid="compact-context-tool"
      data-tone={tool.tool.status === 'failed' ? 'danger' : tool.tool.status === 'completed' ? 'success' : 'neutral'}
      className={`flex items-center gap-2 px-2 py-1 text-xs rounded border border-border bg-muted/50 ${tool.tool.status === 'failed' ? 'border-danger-border' : ''}`}
    >
      <span className={tool.tool.status === 'failed' ? 'text-danger' : 'text-success'}>
        {tool.tool.status === 'failed' ? 'failed' : tool.tool.status === 'completed' ? 'done' : tool.tool.status}
      </span>
      <span className="font-mono text-gray-600">{toolName}</span>
      {label && <span className="text-gray-500 truncate max-w-[220px]">{label}</span>}
      {args.length > 0 && (
        <span className="flex gap-1 shrink-0">
          {args.slice(0, 3).map((arg, i) => (
            <span key={i} className="inline-flex items-center px-1.5 py-0.5 rounded bg-gray-100 text-gray-500 font-mono">
              {arg}
            </span>
          ))}
        </span>
      )}
      {duration != null && <span className="text-gray-400 ml-auto shrink-0">{duration < 1000 ? `${duration}ms` : `${(duration / 1000).toFixed(1)}s`}</span>}
    </div>
  )
}

interface TodoUpdateCardProps {
  part: ToolPart
}

function TodoUpdateCard({ part }: TodoUpdateCardProps) {
  const [expanded, setExpanded] = useState(false)

  let itemCount = 0
  if (part.tool.input) {
    try {
      const parsed = JSON.parse(part.tool.input)
      if (parsed.todos && Array.isArray(parsed.todos)) {
        itemCount = parsed.todos.length
      }
    } catch {}
  }

  const summary = itemCount > 0
    ? `Updated todo list (${itemCount} items)`
    : 'Updated todo list'

  return (
    <div className="rounded-md border border-gray-200 overflow-hidden">
      <Button
        variant="ghost"
        size="sm"
        onClick={() => setExpanded(!expanded)}
        className="flex h-auto items-center justify-start gap-2 w-full text-left px-3 py-1.5 rounded-none hover:bg-gray-50 transition-colors"
      >
        <svg className="h-3.5 w-3.5 text-gray-400 shrink-0" viewBox="0 0 20 20" fill="currentColor">
          <path fillRule="evenodd" d="M16.704 4.153a.75.75 0 01.143 1.052l-8 10.5a.75.75 0 01-1.127.075l-4.5-4.5a.75.75 0 011.06-1.06l3.894 3.893 7.48-9.817a.75.75 0 011.05-.143z" clipRule="evenodd" />
        </svg>
        <span className="text-xs font-medium text-gray-700">{summary}</span>
        {part.tool.status === 'failed' && (
          <span
            data-testid="todo-update-failed"
            data-tone="danger"
            className="text-xs text-danger"
          >
            failed
          </span>
        )}
        <svg className={`h-3 w-3 text-muted-foreground/70 shrink-0 ml-auto transition-transform ${expanded ? 'rotate-90' : ''}`} viewBox="0 0 20 20" fill="currentColor">
          <path fillRule="evenodd" d="M7.21 14.77a.75.75 0 01.02-1.06L11.168 10 7.23 6.29a.75.75 0 111.04-1.08l4.5 4.25a.75.75 0 010 1.08l-4.5 4.25a.75.75 0 01-1.06-.02z" clipRule="evenodd" />
        </svg>
      </Button>
      {expanded && (
        <div className="px-3 pb-2 border-t border-border">
          <div className="rounded-md border border-border overflow-hidden">
            <div className="flex items-center gap-2 px-3 py-1.5 border-b border-border bg-muted">
              <svg className="h-3.5 w-3.5 text-success" viewBox="0 0 20 20" fill="currentColor">
                <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.857-9.809a.75.75 0 00-1.214-.882l-3.483 4.79-1.88-1.88a.75.75 0 10-1.06 1.061l2.5 2.5a.75.75 0 001.137-.089l4-5.5z" clipRule="evenodd" />
              </svg>
              <span className="text-xs font-mono text-muted-foreground">todowrite</span>
              {part.tool.status === 'failed' && (
                <span
                  data-testid="todo-update-failed-detail"
                  data-tone="danger"
                  className="text-xs text-danger"
                >
                  failed
                </span>
              )}
            </div>
            {part.tool.input && (
              <div className="px-3 py-2">
                <div className="font-medium text-xs text-muted-foreground mb-1">Input</div>
                <pre className="whitespace-pre-wrap break-all text-xs text-foreground bg-muted rounded p-2 max-h-48 overflow-auto">
                  {part.tool.input}
                </pre>
              </div>
            )}
            {part.tool.output && (
              <div className="px-3 py-2 border-t border-border">
                <div className="font-medium text-xs text-muted-foreground mb-1">Output</div>
                <pre className="whitespace-pre-wrap break-all text-xs text-foreground bg-muted rounded p-2 max-h-48 overflow-auto">
                  {part.tool.output}
                </pre>
              </div>
            )}
            {part.tool.error && (
              <div
                data-testid="todo-update-error"
                data-tone="danger"
                className="px-3 py-2 text-xs text-danger bg-danger-subtle border-t border-danger-border"
              >
                {part.tool.error}
              </div>
            )}
          </div>
        </div>
      )}
    </div>
  )
}

function SessionTurnView({ turn }: { turn: SessionTurn }) {
    const renderParts = () => {
      const parts: React.ReactNode[] = []
      let i = 0

      while (i < turn.assistant.length) {
        const part = turn.assistant[i]

        if (part.type === 'tool' && isTodowriteTool(getToolIdentity(part))) {
          parts.push(<TodoUpdateCard key={part.id} part={part} />)
          i++
          continue
        }

        if (part.type === 'tool' && isContextTool(getToolIdentity(part)) && shouldGroupContextTool(turn.assistant, i)) {
          const contextGroup: ToolPart[] = []
          while (
            i < turn.assistant.length &&
            turn.assistant[i].type === 'tool' &&
            isContextTool(getToolIdentity(turn.assistant[i] as ToolPart)) &&
            shouldGroupContextTool(turn.assistant, i)
          ) {
            contextGroup.push(turn.assistant[i] as ToolPart)
            i++
          }
          if (contextGroup.length > 0) {
            parts.push(<ContextGroupCard key={`ctx-${contextGroup[0].id}`} tools={contextGroup} />)
          }
          continue
        }

        if (part.type === 'text') parts.push(<AssistantTextPartView key={part.id} part={part} />)
        else if (part.type === 'reasoning') parts.push(<ReasoningPartView key={part.id} part={part} />)
        else if (part.type === 'error') parts.push(<SessionErrorPartView key={part.id} part={part} />)
        else if (part.type === 'tool') parts.push(<ToolPartView key={part.id} part={part} />)

        i++
      }

      return parts
    }

    const changedFileSets: FileChangeSummary[][] = []
    for (const part of turn.assistant) {
      if (part.type === 'tool' && part.tool.changedFiles && part.tool.changedFiles.length > 0) {
        changedFileSets.push(part.tool.changedFiles)
      }
    }
    const allChanges = changedFileSets.flat()
    const uniqueChanges = allChanges.filter((change, idx, arr) =>
      idx === arr.findIndex(c => c.path === change.path)
    )

    return (
      <div className="space-y-3">
        <div className="flex items-center gap-2 text-xs text-gray-400">
          <span className="font-medium text-gray-600">Mohist</span>
          <span className="text-gray-300">·</span>
          <span>{formatDateTime(turn.startedAt)}</span>
          {turn.incomplete && (
            <>
              <span className="text-muted-foreground/60">·</span>
              <span
                data-testid="turn-incomplete-glyph"
                data-tone="warning"
                className="text-warning"
              >
                Incomplete
              </span>
            </>
          )}
        </div>

        <PromptSummaryCard
          summary={turn.user.summary}
          kind={turn.user.kind}
          sentAt={turn.user.sentAt}
          rawText={turn.user.text}
        />

        {turn.assistant.length > 0 && (
          <div className="flex items-center gap-2 text-xs text-gray-400 ml-2">
            <span className="font-medium text-gray-600">Coder</span>
          </div>
        )}

        {renderParts()}

        {uniqueChanges.length > 0 && (
          <TurnFileChangesView changes={uniqueChanges} />
        )}
      </div>
    )
  }

export function SessionTranscriptView({
  turns,
  isRunning,
}: {
  turns: SessionTurn[]
  isRunning: boolean
}) {
  if (turns.length === 0 && !isRunning) {
    return (
      <div
        data-testid="transcript-empty-state"
        data-tone="neutral"
        className="text-center text-muted-foreground/70 text-sm py-12"
      >
        No activity recorded for this session
      </div>
    )
  }

  if (turns.length === 0 && isRunning) {
    return (
      <div
        data-testid="transcript-empty-state"
        data-tone="info"
        className="flex items-center gap-2 text-sm text-info justify-center py-12"
      >
        <span className="inline-block h-2.5 w-2.5 rounded-full bg-info animate-pulse" />
        Waiting for activity...
      </div>
    )
  }

  return (
    <div className="space-y-6">
      {turns.map((turn) => (
        <SessionTurnView key={turn.id} turn={turn} />
      ))}
    </div>
  )
}
