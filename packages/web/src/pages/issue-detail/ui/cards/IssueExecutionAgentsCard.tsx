import { useWorkflowProfile } from '../../../../entities/settings'
import { WorkflowAgentsBlock } from '../../../../widgets/workflow-agents'

/**
 * Read-only list of the named Agents the Issue's effective Profile runs
 * through `mohist/agent` tasks. Configuration lives on the Agents page.
 */
export function IssueExecutionAgentsCard({ workflowProfileId }: { workflowProfileId: string | null | undefined }) {
  const { data: profile, isLoading } = useWorkflowProfile(workflowProfileId ?? null)
  return (
    <WorkflowAgentsBlock
      profile={profile}
      isLoading={isLoading}
      emptyMessage="This Issue's Profile does not reference a named Agent."
    />
  )
}
