import { useState } from 'react'
import Markdown from 'react-markdown'
import type { SessionTurn, TextPart, ReasoningPart, ErrorPart, ToolPart, PromptSummary } from '../lib/types'
import { ToolCallCard } from './ToolCallCard'
import type { ToolCallEntry } from '../lib/types'

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
  const isLong = rawText.length > 500

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

        {(title || subtitle) && (
          <div className="mb-2">
            {title && <p className="text-sm font-medium leading-relaxed">{title}</p>}
            {subtitle && <p className="text-xs text-blue-200 mt-0.5">{subtitle}</p>}
          </div>
        )}

        {expanded || !isLong ? (
          <pre className="whitespace-pre-wrap break-all text-sm leading-relaxed">{rawText}</pre>
        ) : (
          <pre className="whitespace-pre-wrap break-all text-sm leading-relaxed max-h-32 overflow-hidden">{rawText}</pre>
        )}

        <div className="flex items-center gap-2 mt-2">
          {isLong && !expanded && (
            <button
              onClick={() => setExpanded(true)}
              className="text-xs text-blue-200 hover:text-white transition-colors"
            >
              Show full prompt
            </button>
          )}
          {expanded && isLong && (
            <button
              onClick={() => setExpanded(false)}
              className="text-xs text-blue-200 hover:text-white transition-colors"
            >
              Show less
            </button>
          )}
          <button
            onClick={handleCopy}
            className="text-xs text-blue-200 hover:text-white transition-colors"
          >
            {copied ? 'Copied!' : 'Copy'}
          </button>
        </div>
      </div>
    </div>
  )
}

function AssistantTextPartView({ part }: { part: TextPart }) {
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
      </div>
    </div>
  )
}

function ReasoningPartView({ part }: { part: ReasoningPart }) {
  const sizeKB = (part.text.length / 1024).toFixed(1)

  return (
    <details className="max-w-[90%]">
      <summary className="text-xs text-gray-400 cursor-pointer hover:text-gray-600 select-none">
        Thinking... {sizeKB}KB · {formatTime(part.startedAt)}
      </summary>
      <pre className="mt-1 text-xs text-gray-500 whitespace-pre-wrap break-all max-h-48 overflow-auto bg-gray-50 rounded p-2">
        {part.text}
      </pre>
    </details>
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
    <div className="flex items-center gap-1.5 text-xs text-amber-600">
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
  const entry: ToolCallEntry = {
    executionId: '',
    toolName: part.tool.toolName,
    state: part.tool.status,
    timestamp: part.tool.startedAt ? new Date(part.tool.startedAt).getTime() : Date.now(),
    toolCallId: part.tool.toolCallId,
    title: part.tool.title,
    rawInput: part.tool.input,
    rawOutput: part.tool.output,
    error: part.tool.error,
    duration: part.tool.completedAt && part.tool.startedAt
      ? new Date(part.tool.completedAt).getTime() - new Date(part.tool.startedAt).getTime()
      : undefined,
  }

  return <ToolCallCard entry={entry} />
}

function SessionTurnView({ turn }: { turn: SessionTurn }) {
  return (
    <div className="space-y-3">
      <div className="flex items-center gap-2 text-xs text-gray-400">
        <span className="font-medium text-gray-600">Mohist</span>
        <span className="text-gray-300">·</span>
        <span>{formatDateTime(turn.startedAt)}</span>
        {turn.incomplete && (
          <>
            <span className="text-gray-300">·</span>
            <span className="text-amber-500">Incomplete</span>
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

      {turn.assistant.map((part) => {
        if (part.type === 'text') return <AssistantTextPartView key={part.id} part={part} />
        if (part.type === 'reasoning') return <ReasoningPartView key={part.id} part={part} />
        if (part.type === 'error') return <SessionErrorPartView key={part.id} part={part} />
        if (part.type === 'tool') return <ToolPartView key={part.id} part={part} />
        return null
      })}
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
      <div className="text-center text-gray-400 text-sm py-12">
        No activity recorded for this session
      </div>
    )
  }

  if (turns.length === 0 && isRunning) {
    return (
      <div className="flex items-center gap-2 text-sm text-blue-500 justify-center py-12">
        <span className="inline-block h-2.5 w-2.5 rounded-full bg-blue-500 animate-pulse" />
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
