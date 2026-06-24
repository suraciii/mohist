import { parseJsonSafely } from '../../model/transcript-tool-utils'

interface DelegationContentViewProps {
  input?: string
  details: Record<string, unknown>
}

export function DelegationContentView({ input, details }: DelegationContentViewProps) {
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
