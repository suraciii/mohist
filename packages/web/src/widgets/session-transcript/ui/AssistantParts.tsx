import React, { useState } from 'react'
import Markdown from 'react-markdown'
import { Button } from '@/shared/ui/components/button'
import type { DisplayAssistantPart, DisplayChangedFile } from '../model/session-transcript-display'
import { getToolRegistryEntry, getToolDisplayType } from './tool-registry'
import { parseJsonSafely, getFallbackSubtitle, parsePatchOperations, parseEditInput } from '../model/transcript-tool-utils'
import { parseDiff, isLargeDiff, type FileBlock } from '../../issue-changed-files/model/diffModel'

function formatTime(iso: string): string {
  return new Date(iso).toLocaleTimeString()
}

interface AssistantTextPartViewProps {
  text: string
  completedAt: string | null | undefined
  isStreaming?: boolean
}

export function AssistantTextPartView({ text, completedAt, isStreaming }: AssistantTextPartViewProps) {
  const [copied, setCopied] = useState(false)
  const isIncomplete = completedAt === null || completedAt === undefined

  const handleCopy = () => {
    navigator.clipboard.writeText(text).then(() => {
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
          {text}
        </Markdown>
      </div>
      <div className="mt-1 flex items-center gap-2">
        {(isIncomplete || isStreaming) && (
          <span className="inline-block h-1.5 w-1.5 rounded-full bg-blue-400 animate-pulse" />
        )}
        <Button
          variant="link"
          onClick={handleCopy}
          className="h-auto p-0 text-xs text-gray-400 hover:text-gray-600 transition-colors"
        >
          {copied ? 'Copied!' : 'Copy'}
        </Button>
      </div>
    </div>
  )
}

interface ReasoningPartViewProps {
  text: string
  startedAt: string
}

export function ReasoningPartView({ text, startedAt }: ReasoningPartViewProps) {
  const sizeKB = (text.length / 1024).toFixed(1)

  return (
    <details className="max-w-[90%]">
      <summary className="text-xs text-gray-400 cursor-pointer hover:text-gray-600 select-none">
        Thinking... {sizeKB}KB · {formatTime(startedAt)}
      </summary>
      <pre data-scrollable="" className="mt-1 text-xs text-gray-500 whitespace-pre-wrap break-all max-h-48 overflow-auto bg-gray-50 rounded p-2">
        {text}
      </pre>
    </details>
  )
}

interface ErrorPartViewProps {
  message: string
  kind: 'timeout' | 'failed' | 'cancelled' | 'recovery'
  at: string
}

export function ErrorPartView({ message, kind, at }: ErrorPartViewProps) {
  const messages: Record<string, string> = {
    timeout: '⏱️ Execution timed out',
    failed: '✗ Execution failed',
    cancelled: '⊘ Execution cancelled',
    recovery: '↻ Recovery in progress',
  }

  return (
    <div className="flex items-center gap-1.5 text-xs text-amber-600">
      <svg className="h-3 w-3 shrink-0" viewBox="0 0 20 20" fill="currentColor">
        <path fillRule="evenodd" d="M8.485 2.495c.673-1.167 2.357-1.167 3.03 0l6.28 10.875c.673 1.167-.17 2.625-1.516 2.625H3.72c-1.347 0-2.189-1.458-1.515-2.625L8.485 2.495zM10 5a.75.75 0 01.75.75v3.5a.75.75 0 01-1.5 0v-3.5A.75.75 0 0110 5zm0 9a1 1 0 100-2 1 1 0 000 2z" clipRule="evenodd" />
      </svg>
      <span>
        {messages[kind] ?? kind}
        {message && message !== (messages[kind] ?? kind) ? `: ${message}` : ''}
        {' · '}
        {formatTime(at)}
      </span>
    </div>
  )
}

interface DividerPartViewProps {
  label: string
}

export function DividerPartView({ label }: DividerPartViewProps) {
  return (
    <div className="flex items-center gap-2 py-2">
      <div className="flex-1 border-t border-gray-200" />
      <span className="text-xs text-gray-400">{label}</span>
      <div className="flex-1 border-t border-gray-200" />
    </div>
  )
}

interface AssistantPartsProps {
  parts: DisplayAssistantPart[]
}

export function AssistantParts({ parts }: AssistantPartsProps) {
  return (
    <div className="space-y-2">
      {parts.map((part) => {
        switch (part.partType) {
          case 'text':
            return <AssistantTextPartView key={part.id} text={part.text} completedAt={part.completedAt} isStreaming={part.isStreaming} />
          case 'reasoning':
            return <ReasoningPartView key={part.id} text={part.text} startedAt={part.startedAt} />
          case 'tool':
            return <ToolRowView key={part.id} part={part} />
          case 'context-group':
            return <ContextGroupView key={part.id} title={part.title} tools={part.tools} hasError={part.hasError} />
          case 'error':
            return <ErrorPartView key={part.id} message={part.message} kind={part.kind} at={part.at} />
          case 'divider':
            return <DividerPartView key={part.id} label={part.label} />
          default:
            return null
        }
      })}
    </div>
  )
}

function ToolIcon({ normalizedName }: { normalizedName: string }) {
  const entry = getToolRegistryEntry(normalizedName)
  const iconEl = entry.icon as React.ReactElement<{ className?: string }>
  return React.cloneElement(iconEl, { className: 'h-3.5 w-3.5 text-gray-400 shrink-0' })
}

function getToolDisplayLabel(normalizedName: string, displayTitle?: string, displaySubtitle?: string, rawInput?: string): string {
  if (displayTitle) return displayTitle
  if (displaySubtitle) return displaySubtitle
  const entry = getToolRegistryEntry(normalizedName)
  return entry.getTitle(normalizedName, rawInput)
}

function getToolDisplayArgs(normalizedName: string, rawInput?: string): string[] {
  const entry = getToolRegistryEntry(normalizedName)
  return entry.getBadges(normalizedName, rawInput)
}

function getRegistrySubtitle(normalizedName: string, rawInput?: string): string | undefined {
  const entry = getToolRegistryEntry(normalizedName)
  return entry.getSubtitle(normalizedName, rawInput)
}

function RunningIndicator() {
  return (
    <span className="relative flex h-2.5 w-2.5 shrink-0">
      <span className="animate-ping absolute inline-flex h-full w-full rounded-full bg-blue-400 opacity-75"></span>
      <span className="relative inline-flex rounded-full h-2.5 w-2.5 bg-blue-500"></span>
    </span>
  )
}

function truncateOutput(output: string, maxLines: number = 5): string {
  const lines = output.split('\n')
  if (lines.length <= maxLines) return output
  return lines.slice(0, maxLines).join('\n') + '\n...'
}

interface BashContentViewProps {
  input?: string
  output?: string
  details?: Record<string, unknown>
}

function BashContentView({ input, output, details }: BashContentViewProps) {
  const parsed = input ? parseJsonSafely(input) : null
  const command = parsed
    ? (parsed.command ?? parsed.script ?? parsed.cmd ?? '') as string
    : input ?? ''
  const cwd = typeof details?.cwd === 'string' ? details.cwd : undefined
  const exitCode = typeof details?.exitCode === 'number' ? details.exitCode : undefined
  const outputPreview = typeof details?.outputPreview === 'string' && details.outputPreview
    ? details.outputPreview
    : undefined
  const displayOutput = outputPreview ?? output

  return (
    <div className="border-t border-gray-100">
      <div className="px-3 pt-2">
        <div className="flex items-center gap-2 mb-1">
          <span className="text-xs font-medium text-gray-500">Command</span>
          {cwd && (
            <span className="text-xs px-1 rounded bg-gray-100 text-gray-600 font-mono">
              {cwd}
            </span>
          )}
          {exitCode !== undefined && (
            <span className={`text-xs px-1 rounded ${exitCode === 0 ? 'bg-green-100 text-green-700' : 'bg-red-100 text-red-700'}`}>
              {exitCode === 0 ? 'success' : `exit ${exitCode}`}
            </span>
          )}
        </div>
        <pre className="whitespace-pre-wrap break-all text-xs text-gray-800 bg-gray-900 text-gray-100 rounded p-2 font-mono overflow-auto max-h-24">
          {command}
        </pre>
      </div>
      {displayOutput && (
        <div className="px-3 pb-2">
          <div className="font-medium text-xs text-gray-500 mb-1">Output</div>
          <pre className="whitespace-pre-wrap break-all text-xs text-gray-700 bg-gray-50 rounded p-2 font-mono overflow-auto max-h-32">
            {truncateOutput(displayOutput)}
          </pre>
        </div>
      )}
    </div>
  )
}

interface ReadContentViewProps {
  input?: string
  output?: string
}

function ReadContentView({ input, output }: ReadContentViewProps) {
  const parsed = input ? parseJsonSafely(input) : null
  const filePath = parsed
    ? (parsed.filePath ?? parsed.file_path ?? parsed.path ?? '') as string
    : input ?? ''

  const fileName = filePath.split('/').pop() ?? filePath

  return (
    <div className="border-t border-gray-100">
      <div className="px-3 pt-2">
        <div className="flex items-center gap-2 mb-1">
          <span className="text-xs font-medium text-gray-500">Reading</span>
          <span className="text-xs text-gray-700 font-mono">{fileName}</span>
        </div>
        {output && (
          <pre className="whitespace-pre-wrap break-all text-xs text-gray-700 bg-gray-50 rounded p-2 font-mono overflow-auto max-h-40">
            {truncateOutput(output, 8)}
          </pre>
        )}
      </div>
    </div>
  )
}

interface SearchContentViewProps {
  input?: string
  output?: string
}

function SearchContentView({ input, output }: SearchContentViewProps) {
  const parsed = input ? parseJsonSafely(input) : null
  const pattern = parsed
    ? (parsed.pattern ?? parsed.query ?? '') as string
    : ''
  const searchType = parsed ? (parsed.type ?? '') as string : ''

  let results: string[] = []
  let wasTruncated = false
  if (output) {
    try {
      const parsedOutput = JSON.parse(output)
      if (Array.isArray(parsedOutput)) {
        const total = parsedOutput.length
        results = parsedOutput.slice(0, 5).map((r: any) => {
          if (typeof r === 'string') return r
          if (r.file || r.path) return `${r.file ?? r.path}:${r.line ?? ''}`
          return JSON.stringify(r).slice(0, 80)
        })
        wasTruncated = total > 5
      } else if (typeof parsedOutput === 'object') {
        results = [JSON.stringify(parsedOutput).slice(0, 200)]
      } else {
        results = [String(parsedOutput).slice(0, 200)]
      }
    } catch {
      results = [output.slice(0, 200)]
    }
  }

  return (
    <div className="border-t border-gray-100">
      <div className="px-3 pt-2">
        <div className="flex items-center gap-2 mb-1 flex-wrap">
          <span className="text-xs font-medium text-gray-500">Searching</span>
          {pattern && (
            <span className="text-xs text-gray-700 font-mono bg-gray-100 px-1 rounded">
              {pattern}
            </span>
          )}
          {searchType && (
            <span className="text-xs text-gray-500">({searchType})</span>
          )}
        </div>
        {results.length > 0 && (
          <div className="space-y-0.5">
            {results.map((line, i) => (
              <pre key={i} className="whitespace-pre-wrap break-all text-xs text-gray-700 font-mono bg-gray-50 rounded p-1.5 overflow-auto">
                {line}
              </pre>
            ))}
            {wasTruncated && (
              <span className="text-xs text-gray-400">...</span>
            )}
          </div>
        )}
      </div>
    </div>
  )
}

interface TodoContentViewProps {
  input?: string
}

function TodoContentView({ input }: TodoContentViewProps) {
  const parsed = input ? parseJsonSafely(input) : null
  if (!parsed) return null
  const todos = parsed.todos
  if (!Array.isArray(todos) || todos.length === 0) return null

  const completed = todos.filter((t: any) => t.status === 'completed').length
  const pending = todos.filter((t: any) => t.status === 'pending').length
  const inProgress = todos.filter((t: any) => t.status === 'in_progress').length

  return (
    <div className="border-t border-gray-100 px-3 py-2">
      <div className="flex items-center gap-2 mb-1.5">
        <span className="text-xs font-medium text-gray-500">
          {completed}/{todos.length} completed
        </span>
        {inProgress > 0 && (
          <span className="text-xs text-blue-600">{inProgress} in progress</span>
        )}
        {pending > 0 && (
          <span className="text-xs text-gray-400">{pending} pending</span>
        )}
      </div>
      <div className="space-y-0.5">
        {todos.slice(0, 8).map((todo: any, i: number) => {
          const statusIcon = todo.status === 'completed' ? 'done' : todo.status === 'in_progress' ? 'doing' : 'todo'
          return (
            <div key={i} className="flex items-center gap-1.5 text-xs">
              <span className={`shrink-0 w-3 text-center ${todo.status === 'completed' ? 'text-green-500' : todo.status === 'in_progress' ? 'text-blue-500' : 'text-gray-300'}`}>
                {statusIcon === 'done' ? 'done' : statusIcon === 'doing' ? '>' : 'o'}
              </span>
              <span className={`truncate ${todo.status === 'completed' ? 'text-gray-400 line-through' : 'text-gray-700'}`}>
                {todo.content ?? todo.title ?? `Task ${i + 1}`}
              </span>
            </div>
          )
        })}
        {todos.length > 8 && (
          <span className="text-xs text-gray-400">...and {todos.length - 8} more</span>
        )}
      </div>
    </div>
  )
}

interface DelegationContentViewProps {
  input?: string
  details: Record<string, unknown>
}

function DelegationContentView({ input, details }: DelegationContentViewProps) {
  const parsed = input ? parseJsonSafely(input) : null
  const description = typeof details.description === 'string'
    ? details.description
    : parsed && typeof parsed.description === 'string'
      ? parsed.description
      : undefined
  const subagentType = typeof details.subagentType === 'string' ? details.subagentType : undefined
  const childSessionId = typeof details.childSessionId === 'string' ? details.childSessionId : undefined

  if (!description && !subagentType && !childSessionId) return null

  return (
    <div className="border-t border-gray-100 px-3 py-2">
      <div className="flex flex-wrap items-center gap-2">
        <span className="text-xs font-medium text-gray-500">Delegation</span>
        {subagentType && (
          <span className="text-xs px-1 rounded bg-blue-50 text-blue-700">{subagentType}</span>
        )}
        {childSessionId && (
          <span className="text-xs px-1 rounded bg-gray-100 text-gray-600 font-mono">{childSessionId}</span>
        )}
      </div>
      {description && (
        <div className="mt-1 text-xs text-gray-700 break-words">{description}</div>
      )}
    </div>
  )
}

interface ToolStatusDotProps {
  status: string
}

function ToolStatusDot({ status }: ToolStatusDotProps) {
  switch (status) {
    case 'running':
      return <RunningIndicator />
    case 'completed':
      return <span className="h-2 w-2 rounded-full bg-green-500 shrink-0" />
    case 'failed':
      return <span className="h-2 w-2 rounded-full bg-red-500 shrink-0" />
    case 'cancelled':
      return <span className="h-2 w-2 rounded-full bg-gray-400 shrink-0" />
    case 'pending':
    default:
      return <span className="h-2 w-2 rounded-full bg-gray-300 shrink-0" />
  }
}

interface PatchDiffViewProps {
  changedFiles: DisplayChangedFile[]
}

function PatchDiffView({ changedFiles }: PatchDiffViewProps) {
  const [expanded, setExpanded] = useState(false)

  if (changedFiles.length === 0) return null

  const hasRawDetail = changedFiles.some(f => f.rawDetail)

  let diffBlocks: FileBlock[] = []
  if (hasRawDetail) {
    const rawDetail = changedFiles.find(f => f.rawDetail)?.rawDetail
    if (rawDetail && typeof rawDetail === 'string') {
      if (rawDetail.includes('---')) {
        diffBlocks = parseDiff(rawDetail)
      } else if (rawDetail.includes('*** ')) {
        diffBlocks = buildDiffFromPatchText(rawDetail)
      }
    }
  }

  return (
    <div className="border-t border-gray-100">
      <Button
        variant="ghost"
        size="sm"
        onClick={() => setExpanded(!expanded)}
        className="flex h-auto items-center justify-start gap-2 w-full text-left px-3 py-1.5 text-xs text-blue-600 hover:text-blue-800 hover:bg-gray-50 transition-colors rounded-none"
      >
        <svg className={`h-3 w-3 shrink-0 transition-transform ${expanded ? 'rotate-90' : ''}`} viewBox="0 0 20 20" fill="currentColor">
          <path fillRule="evenodd" d="M7.21 14.77a.75.75 0 01.02-1.06L11.168 10 7.23 6.29a.75.75 0 111.04-1.08l4.5 4.25a.75.75 0 010 1.08l-4.5 4.25a.75.75 0 01-1.06-.02z" clipRule="evenodd" />
        </svg>
        {expanded ? 'Hide' : 'Show'} diff {hasRawDetail && '(expanded view)'}
      </Button>
      {expanded && (
        <div className="px-3 pb-2 space-y-2">
          {changedFiles.map((change, i) => {
            const opBadge: Record<string, string> = { created: '+', modified: '~', deleted: '-', moved: '>' }
            return (
              <div key={i} className="space-y-1">
                <div className="flex items-center gap-2 py-0.5">
                  <span className="text-xs font-mono text-gray-500 w-3">{opBadge[change.operation] ?? '?'}</span>
                  <span className="text-xs font-mono text-gray-700 truncate flex-1">{change.path}</span>
                  {change.additions !== undefined && change.additions > 0 && (
                    <span className="text-xs text-green-600">+{change.additions}</span>
                  )}
                  {change.deletions !== undefined && change.deletions > 0 && (
                    <span className="text-xs text-red-600">-{change.deletions}</span>
                  )}
                </div>
                {change.rawDetail && diffBlocks.length > 0 && (
                  <div className="pl-4">
                    {diffBlocks.slice(0, 3).map((block, j) => (
                      <DiffBlockView key={j} block={block} />
                    ))}
                  </div>
                )}
                {change.rawDetail && diffBlocks.length === 0 && (
                  <pre className="text-xs font-mono text-gray-600 bg-gray-50 rounded p-2 whitespace-pre-wrap break-all max-h-32 overflow-auto">
                    {change.rawDetail}
                  </pre>
                )}
              </div>
            )
          })}
        </div>
      )}
    </div>
  )
}

interface DiffContentViewProps {
  changedFiles?: DisplayChangedFile[]
  rawInput?: string
  rawOutput?: string
  details?: Record<string, unknown>
  normalizedName: string
}

function DiffContentView({ changedFiles, rawInput, rawOutput, details, normalizedName }: DiffContentViewProps) {
  const [showRaw, setShowRaw] = useState(false)

  let diffBlocks: FileBlock[] = []
  let diffText: string | undefined
  const metadataDiff = details?.family === 'mutation' && Array.isArray(details.files)
    ? details.files.find((file) => file && typeof file === 'object' && typeof (file as Record<string, unknown>).diff === 'string')
    : undefined
  const metadataDiffText = metadataDiff && typeof metadataDiff === 'object' ? (metadataDiff as Record<string, unknown>).diff : undefined

  if (typeof metadataDiffText === 'string' && metadataDiffText) {
    diffText = metadataDiffText
  } else if (rawOutput && typeof rawOutput === 'string' && rawOutput.includes('---')) {
    diffText = rawOutput
  } else if (rawInput && typeof rawInput === 'string' && rawInput.includes('---')) {
    diffText = rawInput
  }

  if (diffText) {
    diffBlocks = parseDiff(diffText)
  } else if ((normalizedName === 'edit' || normalizedName === 'write') && rawInput) {
    const editInput = parseEditInput(rawInput)
    if (editInput && editInput.oldString && editInput.newString && editInput.filePath) {
      const fileName = editInput.filePath.split('/').pop() ?? editInput.filePath
      diffBlocks = buildDiffFromEdit(fileName, editInput.oldString, editInput.newString)
    }
  } else if (normalizedName === 'apply_patch' && rawInput) {
    const parsed = parseJsonSafely(rawInput)
    if (parsed) {
      const patchText = parsed.patchText ?? parsed.patch
      if (typeof patchText === 'string' && patchText.includes('*** ')) {
        diffBlocks = buildDiffFromPatchText(patchText)
      }
    }
  }

  const hasDiff = diffBlocks.length > 0
  const displayFiles = changedFiles && changedFiles.length > 0
    ? changedFiles
    : diffBlocks.length > 0
      ? diffBlocks.map(b => ({
          path: b.newPath || b.oldPath,
          operation: (b.status === 'added' ? 'created' : b.status === 'deleted' ? 'deleted' : b.status === 'renamed' ? 'moved' : 'modified') as DisplayChangedFile['operation'],
          additions: b.additions,
          deletions: b.deletions,
        }))
      : []

  return (
    <div className="border-t border-gray-100">
      {displayFiles.length > 0 && (
        <div className="px-3 pt-2">
          <div className="flex items-center justify-between mb-1.5">
            <span className="text-xs font-medium text-gray-500">
              Changed files ({displayFiles.length})
            </span>
            {hasDiff && (
              <Button
                variant="link"
                onClick={() => setShowRaw(!showRaw)}
                className="h-auto p-0 text-xs text-gray-400 hover:text-gray-600 transition-colors"
              >
                {showRaw ? 'Show diff' : 'Show raw'}
              </Button>
            )}
          </div>
          <div className="space-y-1">
            {displayFiles.slice(0, 5).map((file, i) => {
              const opBadge: Record<string, string> = { created: '+', modified: '~', deleted: '-', moved: '>' }
              return (
                <div key={i} className="flex items-center gap-2 py-0.5 px-1.5 bg-gray-50 rounded">
                  <span className="text-xs font-mono text-gray-500 w-3">{opBadge[file.operation] ?? '?'}</span>
                  <span className="text-xs font-mono text-gray-700 truncate flex-1">{file.path}</span>
                  {file.additions !== undefined && file.additions > 0 && (
                    <span className="text-xs text-green-600">+{file.additions}</span>
                  )}
                  {file.deletions !== undefined && file.deletions > 0 && (
                    <span className="text-xs text-red-600">-{file.deletions}</span>
                  )}
                </div>
              )
            })}
            {displayFiles.length > 5 && (
              <div className="text-xs text-gray-400 px-1.5">...and {displayFiles.length - 5} more</div>
            )}
          </div>
        </div>
      )}

      {hasDiff && !showRaw && (
        <div className="px-3 pb-2">
          <div className="mt-2 space-y-2">
            {diffBlocks.map((block, i) => (
              <DiffBlockView key={i} block={block} />
            ))}
          </div>
        </div>
      )}

      {showRaw && (
        <div className="px-3 pb-2">
          <div className="font-medium text-xs text-gray-500 mb-1">Raw output</div>
          <pre data-scrollable="" className="whitespace-pre-wrap break-all text-xs text-gray-700 bg-gray-50 rounded p-2 max-h-32 overflow-auto">
            {diffText}
          </pre>
        </div>
      )}

      {!hasDiff && !showRaw && (
        <div className="px-3 pb-2">
          {rawInput && !rawInput.includes('---') && (
            <div className="mb-2">
              <div className="font-medium text-xs text-gray-500 mb-1">Input</div>
              <pre data-scrollable="" className="whitespace-pre-wrap break-all text-xs text-gray-700 bg-gray-50 rounded p-2 max-h-24 overflow-auto">
                {rawInput}
              </pre>
            </div>
          )}
          {rawOutput && (
            <div>
              <div className="font-medium text-xs text-gray-500 mb-1">Output</div>
              <pre data-scrollable="" className="whitespace-pre-wrap break-all text-xs text-gray-700 bg-gray-50 rounded p-2 max-h-24 overflow-auto">
                {rawOutput}
              </pre>
            </div>
          )}
        </div>
      )}
    </div>
  )
}

function buildDiffFromEdit(filePath: string, oldStr: string, newStr: string): FileBlock[] {
  const oldLines = oldStr.split('\n')
  const newLines = newStr.split('\n')

  const additions = newLines.filter(l => l.trim() !== '').length
  const deletions = oldLines.filter(l => l.trim() !== '').length

  const diffLines: import('../../issue-changed-files/model/diffModel').DiffLine[] = []

  diffLines.push({ type: 'hunk', content: `--- a/${filePath}`, oldLine: undefined, newLine: undefined })
  diffLines.push({ type: 'hunk', content: `+++ b/${filePath}`, oldLine: undefined, newLine: undefined })
  diffLines.push({ type: 'hunk', content: `@@ -1,${oldLines.length} +1,${newLines.length} @@`, oldLine: 1, newLine: 1 })

  const maxLines = Math.max(oldLines.length, newLines.length)
  const contextBefore: string[] = []
  const contextAfter: string[] = []
  const addLines: string[] = []
  const delLines: string[] = []

  for (let i = 0; i < maxLines; i++) {
    const oldLine = oldLines[i]
    const newLine = newLines[i]

    if (oldLine !== undefined && newLine !== undefined && oldLine !== newLine) {
      if (contextBefore.length > 0) {
        for (const ctx of contextBefore) {
          diffLines.push({ type: 'context', content: ` ${ctx}`, oldLine: undefined, newLine: undefined })
        }
        contextBefore.length = 0
      }
      if (delLines.length > 0) {
        for (const dl of delLines) {
          diffLines.push({ type: 'del', content: `-${dl}`, oldLine: undefined, newLine: undefined })
        }
        delLines.length = 0
      }
      if (addLines.length > 0) {
        for (const al of addLines) {
          diffLines.push({ type: 'add', content: `+${al}`, oldLine: undefined, newLine: undefined })
        }
        addLines.length = 0
      }
      if (oldLine.trim() !== '') {
        diffLines.push({ type: 'del', content: `-${oldLine}`, oldLine: undefined, newLine: undefined })
      }
      if (newLine.trim() !== '') {
        diffLines.push({ type: 'add', content: `+${newLine}`, oldLine: undefined, newLine: undefined })
      }
    } else if (oldLine !== undefined && oldLine !== newLine) {
      if (contextBefore.length > 0 && delLines.length === 0 && addLines.length === 0) {
        contextBefore.push(oldLine)
      } else if (delLines.length > 0 || addLines.length > 0) {
        if (oldLine.trim() !== '') {
          delLines.push(oldLine)
        }
      } else {
        contextBefore.push(oldLine)
      }
      if (newLine !== undefined && newLine !== oldLine) {
        if (contextBefore.length > 0) {
          for (const ctx of contextBefore) {
            diffLines.push({ type: 'context', content: ` ${ctx}`, oldLine: undefined, newLine: undefined })
          }
          contextBefore.length = 0
        }
        if (newLine.trim() !== '') {
          addLines.push(newLine)
        }
      }
    } else if (oldLine !== undefined) {
      if (delLines.length > 0) {
        for (const dl of delLines) {
          diffLines.push({ type: 'del', content: `-${dl}`, oldLine: undefined, newLine: undefined })
        }
        delLines.length = 0
      }
      if (addLines.length > 0) {
        for (const al of addLines) {
          diffLines.push({ type: 'add', content: `+${al}`, oldLine: undefined, newLine: undefined })
        }
        addLines.length = 0
      }
      contextAfter.push(oldLine)
      if (contextAfter.length > 3) {
        const removed = contextAfter.shift()!
        diffLines.push({ type: 'context', content: ` ${removed}`, oldLine: undefined, newLine: undefined })
      }
    }
  }

  if (delLines.length > 0) {
    for (const dl of delLines) {
      diffLines.push({ type: 'del', content: `-${dl}`, oldLine: undefined, newLine: undefined })
    }
  }
  if (addLines.length > 0) {
    for (const al of addLines) {
      diffLines.push({ type: 'add', content: `+${al}`, oldLine: undefined, newLine: undefined })
    }
  }
  for (const ctx of contextBefore) {
    diffLines.push({ type: 'context', content: ` ${ctx}`, oldLine: undefined, newLine: undefined })
  }
  for (const ctx of contextAfter) {
    diffLines.push({ type: 'context', content: ` ${ctx}`, oldLine: undefined, newLine: undefined })
  }

  return [{
    oldPath: filePath,
    newPath: filePath,
    status: 'modified',
    isBinary: false,
    additions,
    deletions,
    hunks: [],
    lines: diffLines,
    changedLineCount: diffLines.length,
    hunkCount: 1,
  }]
}

function buildDiffFromPatchText(patchText: string): FileBlock[] {
  const changes = parsePatchOperations(patchText)
  const blocks: FileBlock[] = []

  for (const change of changes) {
    const diffLines: import('../../issue-changed-files/model/diffModel').DiffLine[] = []
    diffLines.push({ type: 'hunk', content: `--- a/${change.path}`, oldLine: undefined, newLine: undefined })
    diffLines.push({ type: 'hunk', content: `+++ b/${change.path}`, oldLine: undefined, newLine: undefined })

    const patchForFile = extractPatchForFile(patchText, change.path)
    if (patchForFile) {
      diffLines.push({ type: 'hunk', content: `@@ -1,${change.deletions ?? 0} +1,${change.additions ?? 0} @@`, oldLine: 1, newLine: 1 })

      const lines = patchForFile.split('\n')
      for (const line of lines) {
        if (line.startsWith('+') && !line.startsWith('+++')) {
          diffLines.push({ type: 'add', content: line, oldLine: undefined, newLine: undefined })
        } else if (line.startsWith('-') && !line.startsWith('---')) {
          diffLines.push({ type: 'del', content: line, oldLine: undefined, newLine: undefined })
        } else if (!line.startsWith('@@')) {
          diffLines.push({ type: 'context', content: ` ${line}`, oldLine: undefined, newLine: undefined })
        }
      }
    }

    const status = change.operation === 'created' ? 'added'
      : change.operation === 'deleted' ? 'deleted'
      : change.operation === 'moved' ? 'renamed'
      : 'modified'

    blocks.push({
      oldPath: change.oldPath ?? change.path,
      newPath: change.path,
      status,
      isBinary: false,
      additions: change.additions ?? 0,
      deletions: change.deletions ?? 0,
      hunks: [],
      lines: diffLines,
      changedLineCount: diffLines.length,
      hunkCount: 1,
    })
  }

  return blocks
}

function extractPatchForFile(patchText: string, filePath: string): string | undefined {
  const lines = patchText.split('\n')
  let inFile = false
  const fileLines: string[] = []

  const escapedPath = filePath.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
  const addRegex = new RegExp(`^\\*\\*\\* Add File:\\s*${escapedPath}$`)
  const updateRegex = new RegExp(`^\\*\\*\\* Update File:\\s*${escapedPath}$`)
  const deleteRegex = new RegExp(`^\\*\\*\\* Delete File:\\s*${escapedPath}$`)

  for (const line of lines) {
    const addMatch = line.match(addRegex)
    const updateMatch = line.match(updateRegex)
    const deleteMatch = line.match(deleteRegex)

    if (addMatch || updateMatch || deleteMatch) {
      inFile = true
      fileLines.length = 0
      continue
    }

    if (inFile) {
      if (line.startsWith('*** ') || line.startsWith('diff ') || line.startsWith('--- a/')) {
        if (fileLines.length > 0) {
          break
        }
      }
      if (line.match(/^\*\*\* (Add File|Update File|Delete File|Move to|OldPath):/)) {
        break
      }
      fileLines.push(line)
    }
  }

  return fileLines.length > 0 ? fileLines.join('\n') : undefined
}

function DiffBlockView({ block }: { block: FileBlock }) {
  const [expanded, setExpanded] = useState(false)
  const large = isLargeDiff(block, 200)

  return (
    <div className="rounded border border-gray-200 overflow-hidden">
      <Button
        variant="ghost"
        size="sm"
        onClick={() => setExpanded(!expanded)}
        className="flex h-auto items-center justify-start gap-2 w-full text-left px-2 py-1 hover:bg-gray-50 transition-colors text-xs rounded-none"
      >
        <svg className={`h-3 w-3 shrink-0 transition-transform ${expanded ? 'rotate-90' : ''}`} viewBox="0 0 20 20" fill="currentColor">
          <path fillRule="evenodd" d="M7.21 14.77a.75.75 0 01.02-1.06L11.168 10 7.23 6.29a.75.75 0 111.04-1.08l4.5 4.25a.75.75 0 010 1.08l-4.5 4.25a.75.75 0 01-1.06-.02z" clipRule="evenodd" />
        </svg>
        <span className="font-mono text-gray-700 truncate flex-1">{block.newPath || block.oldPath}</span>
        <span className="text-green-600">+{block.additions}</span>
        <span className="text-red-500">-{block.deletions}</span>
      </Button>
      {expanded && (
        <div className="border-t border-gray-100">
          {large ? (
            <div className="px-3 py-2 text-xs text-gray-400 text-center">
              Large diff ({block.changedLineCount} lines) — truncated for display
            </div>
          ) : (
            <table className="w-full text-xs font-mono">
              <tbody>
                {block.lines.slice(0, 100).map((line, i) => {
                  let bg = ''
                  let textColor = 'text-gray-700'
                  if (line.type === 'add') {
                    bg = 'bg-green-50'
                    textColor = 'text-green-800'
                  } else if (line.type === 'del') {
                    bg = 'bg-red-50'
                    textColor = 'text-red-800'
                  } else if (line.type === 'hunk') {
                    bg = 'bg-blue-50/50'
                    textColor = 'text-blue-600'
                  }
                  return (
                    <tr key={i} className={bg}>
                      <td className="w-[1%] whitespace-nowrap select-none text-right px-2 py-0 text-gray-300 border-r border-gray-100">
                        {line.oldLine?.toString() ?? ''}
                      </td>
                      <td className="w-[1%] whitespace-nowrap select-none text-right px-2 py-0 text-gray-300 border-r border-gray-100">
                        {line.newLine?.toString() ?? ''}
                      </td>
                      <td className={`${textColor} px-3 py-0 whitespace-pre`}>
                        {line.content}
                      </td>
                    </tr>
                  )
                })}
                {block.lines.length > 100 && (
                  <tr>
                    <td colSpan={3} className="px-3 py-1 text-xs text-gray-400 text-center">
                      ... {block.lines.length - 100} more lines
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          )}
        </div>
      )}
    </div>
  )
}

interface ToolRowViewProps {
  part: Extract<DisplayAssistantPart, { partType: 'tool' }>
}

function ToolRowView({ part }: ToolRowViewProps) {
  const [expanded, setExpanded] = useState(false)
  const isRunning = part.status === 'running' || part.status === 'pending'
  const toolLabel = getToolDisplayLabel(part.normalizedName, part.displayTitle, part.displaySubtitle, part.input)
  const toolArgs = getToolDisplayArgs(part.normalizedName, part.input)
  const registrySubtitleCandidate = !part.displayTitle && !part.displaySubtitle ? getRegistrySubtitle(part.normalizedName, part.input) : undefined
  const registrySubtitle = registrySubtitleCandidate && registrySubtitleCandidate !== toolLabel ? registrySubtitleCandidate : undefined
  const fallbackSubtitleCandidate = !registrySubtitle && !part.displayTitle && !part.displaySubtitle ? getFallbackSubtitle(part.input) : undefined
  const fallbackSubtitle = fallbackSubtitleCandidate && fallbackSubtitleCandidate !== toolLabel ? fallbackSubtitleCandidate : undefined
  const hasChangedFiles = part.changedFiles && part.changedFiles.length > 0
  const displayType = getToolDisplayType(part.normalizedName)
  const displayChangedFilesInline = hasChangedFiles && !(displayType === 'diff' && registrySubtitle)

  const showExpandableDetails = !isRunning && (part.input || part.output || part.error || hasChangedFiles)

  const renderSemanticContent = () => {
    if (part.error) {
      return (
        <div className="px-3 text-xs text-red-600 bg-red-50">
          {part.error}
        </div>
      )
    }

    if (displayType === 'terminal' && (part.input || part.output)) {
      return <BashContentView input={part.input} output={part.output} details={part.details} />
    }

    if ((part.normalizedName === 'read' || part.normalizedName === 'read_file') && (part.input || part.output)) {
      return (
        <>
          <ReadContentView input={part.input} output={part.output} />
          {part.input && (
            <div className="px-3 pb-2">
              <div className="font-medium text-xs text-gray-500 mb-1">Input</div>
              <pre data-scrollable="" className="whitespace-pre-wrap break-all text-xs text-gray-700 bg-gray-50 rounded p-2 max-h-24 overflow-auto">
                {part.input}
              </pre>
            </div>
          )}
        </>
      )
    }

    if ((part.normalizedName === 'grep' || part.normalizedName === 'search' || part.normalizedName === 'search_files') && (part.input || part.output)) {
      return <SearchContentView input={part.input} output={part.output} />
    }

    if ((part.normalizedName === 'todowrite' || part.normalizedName === 'todo') && part.input) {
      return <TodoContentView input={part.input} />
    }

    if (part.normalizedName === 'task' && part.details) {
      return <DelegationContentView input={part.input} details={part.details} />
    }

    if (displayType === 'diff') {
      return (
        <DiffContentView
          changedFiles={part.changedFiles}
          rawInput={part.rawInput}
          rawOutput={part.rawOutput}
          details={part.details}
          normalizedName={part.normalizedName}
        />
      )
    }

    return (
      <>
        {part.input && (
          <div className="px-3 pt-2">
            <div className="font-medium text-xs text-gray-500 mb-1">Input</div>
            <pre data-scrollable="" className="whitespace-pre-wrap break-all text-xs text-gray-700 bg-gray-50 rounded p-2 max-h-32 overflow-auto">
              {part.input}
            </pre>
          </div>
        )}
        {part.output && (
          <div className="px-3">
            <div className="font-medium text-xs text-gray-500 mb-1">Output</div>
            <pre data-scrollable="" className="whitespace-pre-wrap break-all text-xs text-gray-700 bg-gray-50 rounded p-2 max-h-32 overflow-auto">
              {part.output}
            </pre>
          </div>
        )}
      </>
    )
  }

  return (
    <div className={`rounded-md border overflow-hidden ${part.hasError ? 'border-red-200' : 'border-gray-200'}`}>
      <Button
        variant="ghost"
        size="sm"
        onClick={showExpandableDetails ? () => setExpanded(!expanded) : undefined}
        className={`flex h-auto items-center justify-start gap-2 w-full text-left px-3 py-1.5 rounded-none transition-colors ${showExpandableDetails ? 'hover:bg-gray-50 cursor-pointer' : 'cursor-default'}`}
      >
        <ToolStatusDot status={part.status} />
        <ToolIcon normalizedName={part.normalizedName} />
        <span className="text-xs font-medium text-gray-700">{toolLabel}</span>
        {toolArgs.length > 0 && !part.displayTitle && !part.displaySubtitle && (
          <span className="flex gap-1 shrink-0">
            {toolArgs.slice(0, 2).map((arg, i) => (
              <span key={i} className="inline-flex items-center px-1 py-0.5 rounded bg-gray-100 text-xs text-gray-500 font-mono">
                {arg}
              </span>
            ))}
          </span>
        )}
        {registrySubtitle && !part.displayTitle && !part.displaySubtitle && (
          <span className="text-xs text-gray-400 truncate max-w-[150px]">{registrySubtitle}</span>
        )}
        {fallbackSubtitle && !part.displayTitle && !part.displaySubtitle && !registrySubtitle && (
          <span className="text-xs text-gray-400 truncate max-w-[150px]">{fallbackSubtitle}</span>
        )}
        {part.hasError && (
          <span className="text-xs text-red-500">failed</span>
        )}
        {displayChangedFilesInline && (
          <span className="text-xs text-green-600">
            {part.changedFiles!.length === 1
              ? part.changedFiles![0].path.split('/').pop()
              : `${part.changedFiles!.length} files`}
          </span>
        )}
        {showExpandableDetails && (
          <svg className={`h-3 w-3 text-gray-400 shrink-0 ml-auto transition-transform ${expanded ? 'rotate-90' : ''}`} viewBox="0 0 20 20" fill="currentColor">
            <path fillRule="evenodd" d="M7.21 14.77a.75.75 0 01.02-1.06L11.168 10 7.23 6.29a.75.75 0 111.04-1.08l4.5 4.25a.75.75 0 010 1.08l-4.5 4.25a.75.75 0 01-1.06-.02z" clipRule="evenodd" />
          </svg>
        )}
      </Button>
      {expanded && showExpandableDetails && (
        <div className="border-t border-gray-100">
          {renderSemanticContent()}
          {displayChangedFilesInline && (
            <PatchDiffView changedFiles={part.changedFiles!} />
          )}
        </div>
      )}
    </div>
  )
}

interface ContextGroupViewProps {
  title: string
  tools: Extract<DisplayAssistantPart, { partType: 'tool' }>[]
  hasError: boolean
}

function ContextGroupView({ title, tools, hasError }: ContextGroupViewProps) {
  const [expanded, setExpanded] = useState(false)
  const [titlePrefix, titleDetail] = title.split(' · ', 2)
  const singleContextTool = tools.length === 1 ? tools[0] : undefined
  const canExpandSingleContextTool = singleContextTool && singleContextTool.status !== 'running' && singleContextTool.status !== 'pending'
  const singleContextToolLabel = singleContextTool ? getToolDisplayLabel(singleContextTool.normalizedName, singleContextTool.displayTitle, singleContextTool.displaySubtitle, singleContextTool.input) : undefined
  const singleContextToolArgs = singleContextTool ? getToolDisplayArgs(singleContextTool.normalizedName, singleContextTool.input) : []

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
        <span className="text-xs font-medium text-gray-700">{titlePrefix}</span>
        {titleDetail && (
          <span className="text-xs text-gray-500 truncate max-w-[240px]">{titleDetail}</span>
        )}
        {hasError && (
          <span className="text-xs text-red-500">failed</span>
        )}
        <svg className={`h-3 w-3 text-gray-400 shrink-0 ml-auto transition-transform ${expanded ? 'rotate-90' : ''}`} viewBox="0 0 20 20" fill="currentColor">
          <path fillRule="evenodd" d="M7.21 14.77a.75.75 0 01.02-1.06L11.168 10 7.23 6.29a.75.75 0 111.04-1.08l4.5 4.25a.75.75 0 010 1.08l-4.5 4.25a.75.75 0 01-1.06-.02z" clipRule="evenodd" />
        </svg>
      </Button>
      {expanded && (
        <div className="px-3 pb-2 border-t border-gray-100 space-y-1.5">
          {singleContextTool && canExpandSingleContextTool ? (
            <div className="px-3 py-2 text-xs text-gray-600">
              <div className="font-medium text-xs text-gray-500 mb-1">
                {singleContextTool.normalizedName === 'read' || singleContextTool.normalizedName === 'read_file' ? 'Reading' : singleContextToolLabel}
              </div>
              {singleContextToolArgs.length > 0 && (
                <div className="flex flex-wrap gap-1 mb-2">
                  {singleContextToolArgs.map((arg) => (
                    <span key={arg} className="rounded bg-gray-100 px-1 py-0.5 font-mono text-gray-500">{arg}</span>
                  ))}
                </div>
              )}
              {singleContextTool.output && (
                <pre data-scrollable="" className="whitespace-pre-wrap break-all text-xs text-gray-700 bg-gray-50 rounded p-2 max-h-24 overflow-auto">
                  {singleContextTool.output}
                </pre>
              )}
              {singleContextTool.input && (
                <div className="mt-2">
                  <div className="font-medium text-xs text-gray-500 mb-1">Input</div>
                  <pre data-scrollable="" className="whitespace-pre-wrap break-all text-xs text-gray-700 bg-gray-50 rounded p-2 max-h-24 overflow-auto">
                    {singleContextTool.input}
                  </pre>
                </div>
              )}
            </div>
          ) : (
            tools.map((tool) => (
              <ToolRowView key={tool.id} part={tool} />
            ))
          )}
        </div>
      )}
    </div>
  )
}
