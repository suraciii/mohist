import { parseJsonSafely } from '../../model/transcript-tool-utils'

interface SearchContentViewProps {
  input?: string
  output?: string
}

export function SearchContentView({ input, output }: SearchContentViewProps) {
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
