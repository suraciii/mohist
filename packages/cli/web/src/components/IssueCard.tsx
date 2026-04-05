import type { Issue, AgentStatus } from '../lib/types'
import { Stage, IssueStatus } from '../lib/types'

const APPROVAL_STAGES = new Set<string>([Stage.Build])

interface Props {
  issue: Issue
  agentStatus: AgentStatus
}

export function IssueCard({ issue, agentStatus }: Props) {
  const isAgentRunning = agentStatus.running && agentStatus.issueId === issue.id
  const isApprovalGate = APPROVAL_STAGES.has(issue.stage) && issue.status === IssueStatus.Active

  return (
    <a
      href={`/issue/${issue.number}`}
      className="block rounded-lg border border-gray-200 bg-white p-3 shadow-sm hover:border-gray-300 hover:shadow-md transition-colors"
    >
      <div className="flex items-center gap-2 mb-1">
        <span className="text-xs font-mono text-gray-400">#{issue.number}</span>
        {isAgentRunning && (
          <span className="inline-flex items-center gap-1 text-xs text-blue-600">
            <span className="inline-block h-2 w-2 rounded-full bg-blue-500 animate-pulse" />
            Running
          </span>
        )}
        {isApprovalGate && !isAgentRunning && (
          <span className="inline-flex items-center gap-1 text-xs text-amber-600 bg-amber-50 px-1.5 py-0.5 rounded">
            <svg className="h-3 w-3" viewBox="0 0 20 20" fill="currentColor">
              <path fillRule="evenodd" d="M8.485 2.495c.673-1.167 2.357-1.167 3.03 0l6.28 10.875c.673 1.167-.17 2.625-1.516 2.625H3.72c-1.347 0-2.189-1.458-1.515-2.625L8.485 2.495zM10 6a.75.75 0 01.75.75v3.5a.75.75 0 01-1.5 0v-3.5A.75.75 0 0110 6zm0 9a1 1 0 100-2 1 1 0 000 2z" clipRule="evenodd" />
            </svg>
            Waiting for approval
          </span>
        )}
      </div>

      <h3 className="text-sm font-medium text-gray-900 truncate" title={issue.title}>
        {issue.title}
      </h3>

      {issue.labels.length > 0 && (
        <div className="mt-2 flex flex-wrap gap-1">
          {issue.labels.map((label) => (
            <span
              key={label}
              className="inline-block rounded-full bg-gray-100 px-2 py-0.5 text-xs text-gray-600"
            >
              {label}
            </span>
          ))}
        </div>
      )}

      {issue.status === IssueStatus.Blocked && (
        <div className="mt-2 text-xs text-red-500">Closed</div>
      )}
      {issue.status === IssueStatus.Paused && (
        <div className="mt-2 text-xs text-gray-400">Paused</div>
      )}
    </a>
  )
}
