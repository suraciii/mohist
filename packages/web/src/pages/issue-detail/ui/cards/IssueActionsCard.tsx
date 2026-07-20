import { BotIcon, CircleCheckIcon } from 'lucide-react'
import { Button } from '@/shared/ui/components/button'
import { CardSection } from '@/shared/ui/components/card-section'
import type { AgentStatus } from '../../../../entities/agent'
import type { Issue } from '../../../../entities/issue'
import type { IssueDetailMutations } from '../../model/useIssueDetailMutations'

export interface IssueActionsCardProps {
  issue: Issue
  agentStatus: AgentStatus | null | undefined
  mutations: Pick<
    IssueDetailMutations,
    | 'approveMutation'
    | 'sendBackMutation'
    | 'startMutation'
    | 'markReadyMutation'
    | 'markDoneMutation'
    | 'closeMutation'
    | 'resumeMutation'
    | 'retryMutation'
    | 'rerunMutation'
  >
  onAskAgent: () => void
  unframed?: boolean
}

export function IssueActionsCard({
  issue,
  agentStatus,
  mutations,
  onAskAgent,
  unframed = false,
}: IssueActionsCardProps) {
  const activeAgents = agentStatus?.activeAgents ?? []
  const isAgentRunningOnThis = activeAgents.some((agent) => agent.issueNumber === issue.number)
  const otherAgentsCount = activeAgents.filter((agent) => agent.issueNumber !== issue.number).length
  const showOtherAgents = !isAgentRunningOnThis && otherAgentsCount > 0 && issue.status !== 'backlog'
  const showClose = issue.health === 'active' && !isAgentRunningOnThis
  const hasChildren = issue.childIssuesSummary?.hasChildren === true || (issue.children?.length ?? 0) > 0
  const showMarkDone = issue.status === 'in_progress'
    && !hasChildren
    && !isAgentRunningOnThis
    && (issue.workflowStatus === 'stopped' || issue.workflowStatus === 'completed')

  const content = (
    <div className="space-y-2" data-testid="issue-actions-card-body">
      {issue.archivedAt && (
        <div
          data-testid="archived-actions-note"
          className="rounded-md bg-muted border border-border px-3 py-2 text-xs text-muted-foreground"
        >
          This issue is archived. Start, stop, retry, rerun, resume controls are unavailable because the workflow is no longer running. The execution history is preserved above.
        </div>
      )}

      {issue.isDraft && (
        <div
          data-testid="start-readiness"
          data-blocker="draft"
          className="rounded-md bg-muted border border-border px-3 py-2 text-sm text-muted-foreground"
        >
          <div className="mb-1 text-xs font-semibold uppercase tracking-wide text-foreground">
            Still a draft
          </div>
          <p className="text-xs">
            This issue has not been marked ready yet. Mark it ready to enable Start.
          </p>
          <Button
            data-testid="mark-ready-button"
            onClick={() => mutations.markReadyMutation.mutate()}
            disabled={mutations.markReadyMutation.isPending}
            className="w-full mt-2"
          >
            {mutations.markReadyMutation.isPending ? 'Marking ready...' : 'Mark ready'}
          </Button>
        </div>
      )}

      {showClose && (
        <Button
          variant="outline"
          onClick={() => mutations.closeMutation.mutate()}
          disabled={mutations.closeMutation.isPending}
          className="w-full"
        >
          {mutations.closeMutation.isPending ? 'Closing...' : 'Close'}
        </Button>
      )}

      {showMarkDone && (
        <Button
          onClick={() => mutations.markDoneMutation.mutate()}
          disabled={mutations.markDoneMutation.isPending}
          className="w-full"
          data-testid="mark-issue-done"
        >
          <CircleCheckIcon className="size-4 mr-2" />
          {mutations.markDoneMutation.isPending ? 'Marking done...' : 'Mark as done'}
        </Button>
      )}

      {showOtherAgents && (
        <div className="text-xs text-muted-foreground text-center">
          {otherAgentsCount} agent{otherAgentsCount > 1 ? 's' : ''} running on other issues
        </div>
      )}

      <div className="border-t border-border/60 pt-2">
        <Button
          variant="outline"
          onClick={onAskAgent}
          className="w-full"
          data-testid="ask-agent-issue"
        >
          <BotIcon className="size-4 mr-2" />
          Ask Agent
        </Button>
      </div>
    </div>
  )
  if (unframed) return content
  return <CardSection title="Actions">{content}</CardSection>
}
