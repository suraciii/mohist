import { useMemo } from 'react'
import type { Issue, AgentStatus } from '../lib/types'
import { Stage } from '../lib/types'
import { StageColumn } from './StageColumn'

const STAGES: { key: Stage; label: string }[] = [
  { key: Stage.Draft, label: 'Draft' },
  { key: Stage.Plan, label: 'Plan' },
  { key: Stage.Build, label: 'Build' },
  { key: Stage.Check, label: 'Check' },
  { key: Stage.Done, label: 'Done' },
]

interface Props {
  issues: Issue[]
  agentStatus: AgentStatus
}

export function KanbanBoard({ issues, agentStatus }: Props) {
  const columns = useMemo(() => {
    const map = new Map<Stage, Issue[]>()
    for (const s of STAGES) map.set(s.key, [])
    for (const issue of issues) {
      const list = map.get(issue.stage)
      if (list) list.push(issue)
    }
    return STAGES.map((s) => ({
      ...s,
      issues: map.get(s.key) ?? [],
    }))
  }, [issues])

  return (
    <div className="flex gap-4 overflow-x-auto p-4 h-[calc(100vh-4rem)]">
      {columns.map((col) => (
        <StageColumn
          key={col.key}
          label={col.label}
          issues={col.issues}
          agentStatus={agentStatus}
        />
      ))}
    </div>
  )
}
