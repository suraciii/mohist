import { parseJsonSafely } from '../../model/transcript-tool-utils'
import { truncateOutput } from './shared'

interface ReadContentViewProps {
  input?: string
  output?: string
}

export function ReadContentView({ input, output }: ReadContentViewProps) {
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
