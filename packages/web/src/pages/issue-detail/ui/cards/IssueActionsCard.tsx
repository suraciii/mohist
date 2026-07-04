import { BotIcon } from 'lucide-react'
import { Button } from '@/shared/ui/components/button'
import { CardSection } from '@/shared/ui/components/card-section'
import type { AgentStatus } from '../../../../entities/agent'
import type { Issue } from '../../../../entities/issue'
import type { RuntimeDecision } from '../../../../widgets/issue-workflow/model/derive-runtime-decision'
import type { IssueDetailMutations } from '../../model/useIssueDetailMutations'

export interface IssueActionsCardProps {
  issue: Issue
  decision: RuntimeDecision
  agentStatus: AgentStatus | null | undefined
  mutations: Pick<
    IssueDetailMutations,
    | 'approveMutation'
    | 'sendBackMutation'
    | 'startMutation'
    | 'markReadyMutation'
    | 'closeMutation'
    | 'resumeMutation'
    | 'retryMutation'
    | 'rerunMutation'
  >
  onAskAgent: () => void
}

export function IssueActionsCard({
  issue,
  decision,
  agentStatus,
  mutations,
  onAskAgent,
}: IssueActionsCardProps) {
  const activeAgents = agentStatus?.activeAgents ?? []
  const isAgentRunningOnThis = activeAgents.some((agent) => agent.issueNumber === issue.number)
  const otherAgentsCount = activeAgents.filter((agent) => agent.issueNumber !== issue.number).length
  const showOtherAgents = !isAgentRunningOnThis && otherAgentsCount > 0 && issue.status !== 'backlog'
  const showClose = issue.health === 'active' && !isAgentRunningOnThis
  const actionError = mutations.approveMutation.error
    || mutations.sendBackMutation.error
    || mutations.startMutation.error
    || mutations.markReadyMutation.error
    || mutations.closeMutation.error
    || mutations.resumeMutation.error
    || mutations.retryMutation.error
    || mutations.rerunMutation.error

  return (
    <CardSection title="Actions">
      <div className="space-y-2">
        {issue.archivedAt && (
          <div
            data-testid="archived-actions-note"
            className="rounded-md bg-muted border border-border px-3 py-2 text-xs text-muted-foreground"
          >
            This issue is archived. Active workflow controls are unavailable because the workflow is no longer running. The execution history is preserved above.
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

        {decision.currentTask && (
          <div className="rounded-md border border-border bg-card px-3 py-2 text-xs text-muted-foreground">
            Current: {decision.currentTask.kind} - {decision.currentTask.title}
          </div>
        )}

        {decision.blockedReason && (
          <div className="rounded-md border border-danger-border bg-danger-subtle px-3 py-2 text-sm text-danger">
            {decision.blockedReason}
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

        {actionError && (
          <div className="rounded-md border border-danger-border bg-danger-subtle px-3 py-2 text-xs text-danger">
            {actionError.message}
          </div>
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
    </CardSection>
  )
}
