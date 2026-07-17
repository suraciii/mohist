import { useState } from 'react'
import { Button } from '@/shared/ui/components/button'
import type { DisplayAssistantPart } from '../model/session-transcript-display'
import { ToolRowView, ContextGroupView } from './tool-views'
import { TranscriptMarkdown } from './TranscriptMarkdown'

function formatTime(iso: string): string {
  return new Date(iso).toLocaleTimeString()
}

interface AssistantTextPartViewProps {
  text: string
  completedAt: string | null | undefined
  isStreaming?: boolean
  isRunning?: boolean
}

export function AssistantTextPartView({ text, completedAt, isStreaming, isRunning }: AssistantTextPartViewProps) {
  const [copied, setCopied] = useState(false)
  const isIncomplete = completedAt === null || completedAt === undefined
  const showStreamingGlyph = isRunning === true && (isIncomplete || isStreaming === true)

  const handleCopy = () => {
    navigator.clipboard.writeText(text).then(() => {
      setCopied(true)
      setTimeout(() => setCopied(false), 2000)
    })
  }

  return (
    <div className="min-w-0">
      <TranscriptMarkdown content={text} />
      <div className="mt-1 flex items-center gap-2">
        {showStreamingGlyph && (
          <span
            data-testid="assistant-text-streaming-glyph"
            data-tone={isStreaming ? 'info' : 'warning'}
            aria-hidden="true"
            className="inline-block h-1.5 w-1.5 rounded-full bg-info animate-pulse"
          />
        )}
        <Button
          variant="link"
          onClick={handleCopy}
          className="h-auto p-0 text-xs text-muted-foreground/70 hover:text-foreground transition-colors"
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
    <details className="min-w-0">
      <summary className="text-xs text-muted-foreground/70 cursor-pointer hover:text-foreground select-none">
        Thinking... {sizeKB}KB · {formatTime(startedAt)}
      </summary>
      <pre data-scrollable="" className="mt-1 text-xs text-muted-foreground whitespace-pre-wrap break-all max-h-48 overflow-auto bg-muted rounded p-2">
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
    <div
      data-testid="assistant-error-part"
      data-tone="warning"
      className="flex items-center gap-1.5 text-xs text-warning"
    >
      <svg aria-hidden="true" className="h-3 w-3 shrink-0" viewBox="0 0 20 20" fill="currentColor">
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
      <div className="flex-1 border-t border-border" />
      <span className="text-xs text-muted-foreground/70">{label}</span>
      <div className="flex-1 border-t border-border" />
    </div>
  )
}

interface AssistantPartsProps {
  parts: DisplayAssistantPart[]
  isRunning?: boolean
}

export function AssistantParts({ parts, isRunning }: AssistantPartsProps) {
  return (
    <div className="space-y-2">
      {parts.map((part) => {
        switch (part.partType) {
          case 'text':
            return <AssistantTextPartView key={part.id} text={part.text} completedAt={part.completedAt} isStreaming={part.isStreaming} isRunning={isRunning} />
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
