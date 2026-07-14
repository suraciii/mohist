import { parseJsonSafely } from '../../model/transcript-tool-utils'
import { truncateOutput } from './shared'

interface BashContentViewProps {
  input?: string
  output?: string
  details?: Record<string, unknown>
}

export function BashContentView({ input, output, details }: BashContentViewProps) {
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
    <div className="border-t border-border">
      <div className="px-3 pt-2">
        <div className="flex items-center gap-2 mb-1">
          <span className="text-xs font-medium text-muted-foreground">Command</span>
          {cwd && (
            <span className="text-xs px-1 rounded bg-muted text-muted-foreground font-mono">
              {cwd}
            </span>
          )}
          {exitCode !== undefined && (
            <span
              data-testid="bash-exit-status"
              data-tone={exitCode === 0 ? 'success' : 'danger'}
              className={`text-xs px-1 rounded ${exitCode === 0 ? 'bg-success-subtle text-success' : 'bg-danger-subtle text-danger'}`}
            >
              {exitCode === 0 ? 'success' : `exit ${exitCode}`}
            </span>
          )}
        </div>
        <pre className="whitespace-pre-wrap break-all text-xs text-muted-foreground bg-foreground/90 text-background rounded p-2 font-mono overflow-auto max-h-24">
          {command}
        </pre>
      </div>
      {displayOutput && (
        <div className="px-3 pb-2">
          <div className="font-medium text-xs text-muted-foreground mb-1">Output</div>
          <pre className="whitespace-pre-wrap break-all text-xs text-muted-foreground bg-muted rounded p-2 font-mono overflow-auto max-h-32">
            {truncateOutput(displayOutput)}
          </pre>
        </div>
      )}
    </div>
  )
}
