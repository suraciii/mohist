import { useMemo } from 'react'
import { Link } from 'react-router-dom'
import { useActivityCards, type SessionCard } from '@/widgets/coder-session/model/activity-cards'
import { useProject, useProjectPath } from '@/entities/project'
import { isRunningIssue, issueNeedsOwnerAction, useIssues, type Issue } from '@/entities/issue'
import { CompactSessionCard, IssueRow } from './CompactSessionCard'

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
}

export function PulseZone({ issuesOverride }: PulseZoneProps = {}) {
  const { projectId } = useProject()
  const { data: fetchedIssues } = useIssues(projectId ? { projectId } : undefined)
  const { activeCards, activeCardByIssueNumber } = useActivityCards()
  const toProjectPath = useProjectPath()

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

    return [
      ...runningIssues.map((issue) => ({ kind: 'issue' as const, issue })),
      ...sessionOnlyRows,
    ]
  }, [issuesOverride, fetchedIssues, activeCards])

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

              const issue = row.issue
              const needsAction = issueNeedsOwnerAction(issue)
              const card = activeCardByIssueNumber.get(issue.number)
              if (card) {
                return (
                  <CompactSessionCard
                    key={`issue-${issue.number}-session`}
                    card={card}
                    issueNumber={issue.number}
                    issueTitle={issue.title}
                    workflowStage={stageLabel(issue.workflowStage ?? null)}
                    needsOwnerAction={needsAction}
                  />
                )
              }
              return (
                <IssueRow
                  key={`issue-${issue.number}-row`}
                  issueNumber={issue.number}
                  issueTitle={issue.title}
                  workflowStage={stageLabel(issue.workflowStage ?? null)}
                  needsOwnerAction={needsAction}
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
  const aIssueNumber = Number(a.issueNumber)
  const bIssueNumber = Number(b.issueNumber)
  if (Number.isFinite(aIssueNumber) && Number.isFinite(bIssueNumber)) {
    return aIssueNumber - bIssueNumber
  }
  return a.issueNumber.localeCompare(b.issueNumber)
}
