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
