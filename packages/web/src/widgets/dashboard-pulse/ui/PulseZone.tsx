import { useMemo } from 'react'
import { Link } from 'react-router-dom'
import { useActivityCards, type SessionCard } from '@/entities/agent-ops'
import { useProject, useProjectPath } from '@/entities/project'
import { classifyIssueAttention, isRunningIssue, useIssues, type Issue } from '@/entities/issue'
import { useAgentStatus, type ActiveAgentInfo, type AgentStatus } from '@/entities/agent'
import { CompactSessionCard, IssueRow, stageColorFor } from './CompactSessionCard'

const MAX_VISIBLE_ROWS = 4

function stageLabel(stage: string | null | undefined): string | null {
  if (!stage) return null
  if (stage.length === 0) return null
  return stage.charAt(0).toUpperCase() + stage.slice(1)
}

export interface PulseZoneProps {
  /**
   * Test/dev override: lets spec tests inject an in-memory issue list
   * without going through `useIssues`. Production callers should rely
   * on the default `useIssues()` pull.
   */
  issuesOverride?: Issue[]
  agentStatusOverride?: AgentStatus
  activityCardsHook?: typeof useActivityCards
}

type ActiveRow =
  | { kind: 'issue'; issue: Issue }
  | { kind: 'session'; card: SessionCard }
  | { kind: 'agent'; issueNumber: number | null; stage: string | null; key: string }

export function PulseZone({
  issuesOverride,
  agentStatusOverride,
  activityCardsHook = useActivityCards,
}: PulseZoneProps = {}) {
  const { projectId } = useProject()
  const { data: fetchedIssues } = useIssues(projectId ? { projectId } : undefined)
  const { data: fetchedAgentStatus } = useAgentStatus()
  const { activeCards, activeCardByIssueNumber } = activityCardsHook()
  const toProjectPath = useProjectPath()
  const agentStatus = agentStatusOverride ?? fetchedAgentStatus

  const activeRows = useMemo(() => {
    const issues = issuesOverride ?? fetchedIssues ?? []
    const runningIssues = issues
      .filter(isRunningIssue)
      .slice()
      .sort((a, b) => a.number - b.number)
    const runningIssueNumbers = new Set(runningIssues.map((issue) => issue.number))
    const sessionOnlyRows = activeCards
      .filter((card) => {
        const issueNumber = Number(card.issueNumber)
        return !Number.isFinite(issueNumber) || !runningIssueNumbers.has(issueNumber)
      })
      .slice()
      .sort(compareSessionCards)
      .map((card) => ({ kind: 'session' as const, card }))
    const activeCardIssueNumbers = new Set(
      activeCards
        .map((card) => normalizeIssueNumber(card.issueNumber))
        .filter((issueNumber): issueNumber is number => issueNumber !== null),
    )
    const coveredIssueNumbers = new Set([...runningIssueNumbers, ...activeCardIssueNumbers])
    const agentRows = deriveAgentStatusRows(agentStatus, coveredIssueNumbers)

    return [
      ...runningIssues.map((issue) => ({ kind: 'issue' as const, issue })),
      ...sessionOnlyRows,
      ...agentRows,
    ]
  }, [issuesOverride, fetchedIssues, activeCards, agentStatus])

  const visible = activeRows.slice(0, MAX_VISIBLE_ROWS)
  const overflow = activeRows.length - visible.length

  return (
    <div data-testid="pulse-zone" className="flex flex-col gap-3">
      {activeRows.length === 0 ? (
        <div
          data-testid="pulse-empty-state"
          className="rounded-md border border-dashed border-gray-200 bg-gray-50 px-3 py-6 text-center"
        >
          <p className="text-xs text-gray-400">No active production</p>
        </div>
      ) : (
        <>
          <div className="flex flex-col gap-2" data-testid="pulse-card-list">
            {visible.map((row) => {
              if (row.kind === 'session') {
                return (
                  <CompactSessionCard
                    key={`session-${row.card.sessionId}`}
                    card={row.card}
                  />
                )
              }

              if (row.kind === 'agent') {
                return (
                  <AgentStatusRow
                    key={row.key}
                    issueNumber={row.issueNumber}
                    stage={row.stage}
                  />
                )
              }

              const issue = row.issue
              const ownerActionItem = classifyIssueAttention(issue)
              const card = activeCardByIssueNumber.get(issue.number)
              if (card) {
                return (
                  <CompactSessionCard
                    key={`issue-${issue.number}-session`}
                    card={card}
                    issueNumber={issue.number}
                    issueTitle={issue.title}
                    workflowStage={stageLabel(issue.workflowStage ?? null)}
                    ownerActionItem={ownerActionItem}
                  />
                )
              }
              return (
                <IssueRow
                  key={`issue-${issue.number}-row`}
                  issueNumber={issue.number}
                  issueTitle={issue.title}
                  workflowStage={stageLabel(issue.workflowStage ?? null)}
                  ownerActionItem={ownerActionItem}
                />
              )
            })}
          </div>
          {overflow > 0 && (
            <Link
              to={toProjectPath('/issues')}
              data-testid="pulse-overflow-link"
              className="text-xs text-blue-600 hover:text-blue-800 hover:underline self-start"
            >
              +{overflow} more active items
            </Link>
          )}
        </>
      )}
    </div>
  )
}

function compareSessionCards(a: SessionCard, b: SessionCard): number {
  return a.issueNumber - b.issueNumber
}

function deriveAgentStatusRows(
  agentStatus: AgentStatus | undefined,
  coveredIssueNumbers: Set<number>,
): Extract<ActiveRow, { kind: 'agent' }>[] {
  if (!agentStatus) return []

  const rows: Extract<ActiveRow, { kind: 'agent' }>[] = []
  for (const activeAgent of agentStatus.activeAgents ?? []) {
    const issueNumber = normalizeIssueNumber(activeAgent.issueNumber)
    if (issueNumber === null || coveredIssueNumbers.has(issueNumber)) continue
    rows.push(agentStatusRowFromActiveAgent(activeAgent, issueNumber))
    coveredIssueNumbers.add(issueNumber)
  }

  if (rows.length === 0 && agentStatus.running) {
    const issueNumber = normalizeIssueNumber(agentStatus.issueNumber)
    if (issueNumber === null || !coveredIssueNumbers.has(issueNumber)) {
      rows.push({
        kind: 'agent',
        issueNumber,
        stage: null,
        key: issueNumber === null ? 'agent-running' : `agent-${issueNumber}`,
      })
    }
  }

  return rows.sort(compareAgentStatusRows)
}

function agentStatusRowFromActiveAgent(
  activeAgent: ActiveAgentInfo,
  issueNumber: number,
): Extract<ActiveRow, { kind: 'agent' }> {
  return {
    kind: 'agent',
    issueNumber,
    stage: stageLabel(activeAgent.progress?.stage ?? null),
    key: `agent-${issueNumber}`,
  }
}

function normalizeIssueNumber(issueNumber: string | number | null | undefined): number | null {
  if (issueNumber == null) return null
  const value = Number(issueNumber)
  return Number.isFinite(value) ? value : null
}

function compareAgentStatusRows(
  a: Extract<ActiveRow, { kind: 'agent' }>,
  b: Extract<ActiveRow, { kind: 'agent' }>,
): number {
  if (a.issueNumber === null && b.issueNumber === null) return 0
  if (a.issueNumber === null) return 1
  if (b.issueNumber === null) return -1
  return a.issueNumber - b.issueNumber
}

function AgentStatusRow({
  issueNumber,
  stage,
}: {
  issueNumber: number | null
  stage: string | null
}) {
  const toProjectPath = useProjectPath()
  const linkTarget = issueNumber === null ? '/activity' : `/issues/${issueNumber}`
  const stageText = stage ?? 'Active'

  return (
    <Link
      to={toProjectPath(linkTarget)}
      data-testid="pulse-agent-status-card"
      data-issue-number={issueNumber === null ? 'unknown' : String(issueNumber)}
      className="block rounded-lg border border-border bg-card shadow-sm hover:border-muted-foreground/40 hover:shadow-md transition-colors"
    >
      <div className="p-3">
        <div className="flex items-center gap-2 mb-1.5">
          <span className="inline-block h-2 w-2 rounded-full bg-info animate-pulse" />
          {issueNumber !== null && (
            <span className="text-xs font-mono text-muted-foreground">#{issueNumber}</span>
          )}
          <span
            className={`inline-flex items-center rounded-full px-1.5 py-0.5 text-[10px] font-semibold ${stageColorFor(stageText)}`}
            data-testid="pulse-agent-status-stage"
          >
            {stageText}
          </span>
        </div>
        <h3 className="text-sm font-medium text-foreground" data-testid="pulse-agent-status-title">
          Agent active
        </h3>
        <p className="mt-1 text-xs text-muted-foreground">
          Runner status reports active work; session telemetry is catching up.
        </p>
      </div>
    </Link>
  )
}
