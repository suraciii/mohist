import type { Issue, AgentStatus } from '../lib/types'
import { IssueCard } from './IssueCard'

interface Props {
  label: string
  issues: Issue[]
  agentStatus: AgentStatus
}

export function StageColumn({ label, issues, agentStatus }: Props) {
  return (
    <div className="flex flex-col min-w-[280px] max-w-[320px] flex-1">
      <div className="mb-3 flex items-center gap-2 px-1">
        <span className="inline-block h-2.5 w-2.5 rounded-full bg-gray-400" />
        <h2 className="text-sm font-semibold text-gray-700 uppercase tracking-wide">{label}</h2>
        <span className="ml-auto text-xs text-gray-400">{issues.length}</span>
      </div>

      <div className="flex-1 space-y-2 overflow-y-auto rounded-lg bg-gray-100/60 p-2 min-h-[120px]">
        {issues.length === 0 && (
          <div className="flex items-center justify-center py-8 text-xs text-gray-400">
            No issues
          </div>
        )}
        {issues.map((issue) => (
          <IssueCard key={issue.id} issue={issue} agentStatus={agentStatus} />
        ))}
      </div>
    </div>
  )
}
