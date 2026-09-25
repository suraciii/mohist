import { useMemo, useState } from 'react'
import { useProjects, useProject } from '../../../entities/project'
import { useAgentStatus, type AgentStatus } from '../../../entities/agent'
import { useRunnerSummary } from '../../../entities/runner'
import { CreateProjectDialog } from '../../../features/create-project'
import { DashboardDigestWidget } from '../../../widgets/dashboard-digest'
import { DashboardCapacityZone } from '../../../widgets/dashboard-capacity'
import { PulseZone } from '../../../widgets/dashboard-pulse'
import { useActivityCards } from '../../../entities/agent-ops'
import { FactoryStatusHeadline } from '../../../widgets/factory-status'
import { AttentionHero } from '../../../widgets/attention-hero'
import { Button } from '../../../shared/ui/components/button'
import { useDocumentTitle } from '../../../shared/lib/useDocumentTitle'
import { isRunningIssue, useIssues, useRecentDigest } from '../../../entities/issue'
import { deriveAttentionItems } from '../../../entities/agent-ops'
import { CheckCircle2Icon } from 'lucide-react'
import { DashboardZone } from './DashboardZone'

const defaultAgentStatus: AgentStatus = {
  running: false,
  issueNumber: null,
  activeAgents: [],
  capacity: { active: 0, max: 0 },
}

export type ActivityCardsHook = typeof useActivityCards
export type RunnerSummaryHook = typeof useRunnerSummary

export function DashboardPage({
  activityCardsHook = useActivityCards,
  runnerSummaryHook = useRunnerSummary,
}: {
  activityCardsHook?: ActivityCardsHook
  runnerSummaryHook?: RunnerSummaryHook
} = {}) {
  const { data: projects, isLoading: projectsLoading } = useProjects()
  const { currentProject, projectId } = useProject()
  const { data: agentStatus, isLoading: agentStatusLoading, isError: agentStatusError } = useAgentStatus()
  const runnerSummary = runnerSummaryHook()
  const {
    data: fetchedIssues,
    isLoading: issuesLoading,
    isError: issuesError,
  } = useIssues(projectId ? { projectId } : undefined)
  const {
    activeCards,
    needsVerificationCards = [],
    isLoading: activityLoading,
    isError: activityError,
  } = activityCardsHook()
  const { completed, failed, archived } = useRecentDigest()
  const [showCreateProject, setShowCreateProject] = useState(false)

  useDocumentTitle('Dashboard — Mohist', agentStatus?.running ?? false)

  const attentionItems = useMemo(
    () => deriveAttentionItems(fetchedIssues ?? [], agentStatus ?? defaultAgentStatus, runnerSummary),
    [fetchedIssues, agentStatus, runnerSummary],
  )

  const runningIssues = useMemo(() => (fetchedIssues ?? []).filter(isRunningIssue), [fetchedIssues])

  const hasAttention = attentionItems.length > 0
  const hasAgentStatusActiveWork = agentStatus?.running === true || (agentStatus?.activeAgents?.length ?? 0) > 0
  const hasNeedsVerification = needsVerificationCards.length > 0
  const hasActiveWork = runningIssues.length > 0 || activeCards.length > 0 || hasAgentStatusActiveWork
  const hasDigestItems = completed.length > 0 || failed.length > 0 || archived.length > 0
  const hasCapacityData = runnerSummary.rows.length > 0
  const issuesResolved = fetchedIssues !== undefined || (!issuesLoading && !issuesError)
  const activityResolved = !activityLoading && !activityError
  const agentStatusResolved = agentStatus !== undefined || (!agentStatusLoading && !agentStatusError)
  const runnerStatusResolved = runnerSummary.isLoading !== true && runnerSummary.isError !== true
  const runnerReady = runnerSummary.hasAdmissibleCapacity

  const showAttentionHero = hasAttention
  const showReadyState =
    issuesResolved &&
    activityResolved &&
    agentStatusResolved &&
    runnerStatusResolved &&
    runnerReady &&
    runnerSummary.activeWorkCount === 0 &&
    !hasAttention &&
    !hasActiveWork &&
    !hasNeedsVerification

  if (projectsLoading) {
    return null
  }

  if (!projects || projects.length === 0) {
    return (
      <>
        <div data-testid="dashboard-empty-state" className="flex items-center justify-center flex-1">
          <div className="text-center">
            <div className="text-muted-foreground text-lg mb-4">No projects yet</div>
            <Button onClick={() => setShowCreateProject(true)} data-testid="dashboard-create-project">
              Create Project
            </Button>
          </div>
        </div>
        <CreateProjectDialog open={showCreateProject} onClose={() => setShowCreateProject(false)} />
      </>
    )
  }

  return (
    <div
      data-testid="dashboard-page"
      data-project={currentProject?.name ?? ''}
      data-state={
        showAttentionHero
          ? 'has-attention'
          : hasNeedsVerification
            ? 'needs-verification'
            : hasActiveWork
              ? 'active-only'
              : runnerSummary.fleet.state === 'admission-blocked'
                ? 'runner-blocked'
                : runnerSummary.activeWorkCount > 0
                  ? 'runner-active-work'
                  : 'ready'
      }
      className="flex-1 overflow-y-auto p-4 md:p-6"
    >
      <div className="flex flex-col gap-4 md:gap-6">
        <div data-testid="dashboard-headline">
          <FactoryStatusHeadline runnerSummary={runnerSummary} />
        </div>
        {showAttentionHero && (
          <div data-testid="dashboard-hero">
            <AttentionHero
              issues={fetchedIssues ?? []}
              agentStatus={agentStatus ?? defaultAgentStatus}
              runnerSummary={runnerSummary}
            />
          </div>
        )}
        {showReadyState && <ReadyState />}
        {hasActiveWork && (
          <DashboardZone id="pulse" name="Active production">
            <PulseZone
              issuesOverride={fetchedIssues ?? []}
              agentStatusOverride={agentStatus ?? defaultAgentStatus}
              activityCardsHook={activityCardsHook}
              includeNeedsVerification={false}
            />
          </DashboardZone>
        )}
        {hasNeedsVerification && (
          <DashboardZone id="needs-verification" name="Needs verification">
            <PulseZone
              issuesOverride={[]}
              agentStatusOverride={defaultAgentStatus}
              activityCardsHook={activityCardsHook}
              needsVerificationOnly
            />
          </DashboardZone>
        )}
        {hasCapacityData && <DashboardCapacityZone runnerSummaryOverride={runnerSummary} />}
        {hasDigestItems && (
          <DashboardZone id="digest" name="Recent history">
            <DashboardDigestWidget />
          </DashboardZone>
        )}
      </div>
    </div>
  )
}

function ReadyState() {
  return (
    <section
      data-testid="dashboard-ready-state"
      aria-label="Ready"
      className="rounded-lg border border-success-border bg-success-subtle p-4"
    >
      <div className="flex items-center gap-2">
        <span className="inline-flex items-center justify-center size-6 rounded-full bg-success text-white">
          <CheckCircle2Icon className="size-3.5" />
        </span>
        <span className="text-sm font-semibold uppercase tracking-wide text-success">All clear</span>
      </div>
      <p className="mt-2 text-sm text-foreground">
        Nothing needs your attention right now. New activity will surface here.
      </p>
    </section>
  )
}
