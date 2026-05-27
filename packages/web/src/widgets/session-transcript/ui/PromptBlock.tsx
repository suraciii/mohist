import { useState } from 'react'
import type { DisplayPrompt } from '../model/session-transcript-display'

const KIND_LABELS: Record<string, string> = {
  initial: 'Initial Task',
  task: 'Task',
  retry: 'Retry',
  followup: 'Follow-up',
  recovery: 'Recovery',
  'legacy-missing': 'Missing Prompt',
}

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
      <div className="flex justify-end">
        <div className="max-w-[80%] rounded-2xl rounded-br-sm bg-gray-300 text-white px-4 py-2.5 text-sm">
          <div className="flex items-center gap-2 text-xs text-gray-200 mb-1.5">
            <span className="font-medium">{KIND_LABELS[prompt.kind] ?? prompt.kind}</span>
            <span className="text-gray-300">·</span>
            <span>{formatDateTime(prompt.sentAt)}</span>
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
      <div className="max-w-[80%] rounded-2xl rounded-br-sm bg-gray-200 text-gray-800 px-4 py-2.5 text-sm">
        <div className="flex items-center gap-2 text-xs text-gray-500 mb-1.5">
          <span className="font-medium">{KIND_LABELS[prompt.kind] ?? prompt.kind}</span>
          <span className="text-gray-400">·</span>
          <span>{formatDateTime(prompt.sentAt)}</span>
        </div>

        <div className="mb-2 space-y-1">
          <p className="text-sm font-medium leading-relaxed">{prompt.title || 'Task prompt'}</p>
          {prompt.subtitle && <p className="text-xs text-gray-500">{prompt.subtitle}</p>}
          {prompt.outputPath && prompt.outputPath !== prompt.subtitle && !prompt.subtitle?.endsWith(prompt.outputPath) && (
            <p className="text-xs text-gray-400">Output: {prompt.outputPath}</p>
          )}
          {prompt.contextFiles && prompt.contextFiles.length > 0 && (
            <p className="text-xs text-gray-400">
              Context: {prompt.contextFiles.join(', ')}
            </p>
          )}
        </div>

        {expanded && (
          <pre className="whitespace-pre-wrap break-all text-sm leading-relaxed mt-2 border-t border-gray-300 pt-2 font-mono text-xs">{prompt.text}</pre>
        )}

        <div className="flex items-center gap-2 mt-2">
          {!expanded && prompt.text && (
            <button
              onClick={() => setExpanded(true)}
              className="text-xs text-gray-500 hover:text-gray-700 transition-colors"
            >
              Show full prompt
            </button>
          )}
          {expanded && (
            <button
              onClick={() => setExpanded(false)}
              className="text-xs text-gray-500 hover:text-gray-700 transition-colors"
            >
              Show less
            </button>
          )}
          <button
            onClick={handleCopy}
            className="text-xs text-gray-500 hover:text-gray-700 transition-colors"
          >
            {copied ? 'Copied!' : 'Copy'}
          </button>
        </div>
      </div>
    </div>
  )
}