import { useState } from 'react'
import { Button } from '@/shared/ui/components/button'
import type { DisplayPrompt } from '../model/session-transcript-display'
import { promptKindLabel } from '../model/prompt-kind-labels'

function formatDateTime(iso: string): string {
  return new Date(iso).toLocaleString()
}

interface PromptBlockProps {
  prompt: DisplayPrompt
}

export function PromptBlock({ prompt }: PromptBlockProps) {
  const [expanded, setExpanded] = useState(false)
  const [copied, setCopied] = useState(false)

  const isLegacy = prompt.kind === 'legacy-missing'

  const handleCopy = () => {
    navigator.clipboard.writeText(prompt.text).then(() => {
      setCopied(true)
      setTimeout(() => setCopied(false), 2000)
    })
  }

  if (isLegacy) {
    return (
      <div
        data-prompt-block=""
        data-prompt-kind={prompt.kind}
        className="min-w-0 border-l-2 border-muted pl-3 py-1 italic text-sm text-muted-foreground"
      >
        <div className="flex flex-wrap items-center gap-x-1.5 gap-y-0.5 text-xs text-muted-foreground/80 mb-1">
          <span className="font-medium">{promptKindLabel(prompt.kind)}</span>
          <span aria-hidden="true">·</span>
          <span>{formatDateTime(prompt.sentAt)}</span>
        </div>
        <p className="text-sm italic text-muted-foreground">
          Prompt was not recorded for this historical session
        </p>
      </div>
    )
  }

  return (
    <div
      data-prompt-block=""
      data-prompt-kind={prompt.kind}
      className="min-w-0 border-l-2 border-muted pl-3 py-1"
    >
      <div className="flex flex-wrap items-center gap-x-1.5 gap-y-0.5 text-xs text-muted-foreground/80 mb-1">
        <span className="font-medium">{promptKindLabel(prompt.kind)}</span>
        <span aria-hidden="true">·</span>
        <span>{formatDateTime(prompt.sentAt)}</span>
      </div>

      <div className="mb-2 space-y-1">
        <p className="text-sm font-medium leading-relaxed">{prompt.title || 'Task prompt'}</p>
        {prompt.subtitle && <p className="text-xs text-muted-foreground">{prompt.subtitle}</p>}
        {prompt.outputPath && prompt.outputPath !== prompt.subtitle && !prompt.subtitle?.endsWith(prompt.outputPath) && (
          <p className="text-xs text-muted-foreground/80">Output: {prompt.outputPath}</p>
        )}
        {prompt.contextFiles && prompt.contextFiles.length > 0 && (
          <p className="text-xs text-muted-foreground/80">
            Context: {prompt.contextFiles.join(', ')}
          </p>
        )}
      </div>

      {expanded && (
        <pre className="whitespace-pre-wrap break-all text-sm leading-relaxed mt-2 border-t border-border pt-2 font-mono text-xs text-muted-foreground">{prompt.text}</pre>
      )}

      <div className="flex items-center gap-2 mt-2">
        {!expanded && prompt.text && (
          <Button
            variant="link"
            onClick={() => setExpanded(true)}
            className="h-auto p-0 text-xs text-muted-foreground hover:text-foreground transition-colors"
          >
            Show full prompt
          </Button>
        )}
        {expanded && (
          <Button
            variant="link"
            onClick={() => setExpanded(false)}
            className="h-auto p-0 text-xs text-muted-foreground hover:text-foreground transition-colors"
          >
            Show less
          </Button>
        )}
        <Button
          variant="link"
          onClick={handleCopy}
          className="h-auto p-0 text-xs text-muted-foreground hover:text-foreground transition-colors"
        >
          {copied ? 'Copied!' : 'Copy'}
        </Button>
      </div>
    </div>
  )
}