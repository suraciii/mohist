import { memo } from 'react'
import { Handle, Position, type NodeProps } from '@xyflow/react'
import { statusColors, readinessLabel, readinessTone } from '../model/readiness'
import type { MemberNodeData } from '../model/layout'

export interface MemberFlowNodeProps extends NodeProps {
  data: MemberNodeData
}

function MemberFlowNodeImpl({ data, selected }: MemberFlowNodeProps) {
  if (data.kind !== 'member') return null
  const { issue, readiness, waitingForIssueNumber } = data
  const tone = statusColors(issue.status)
  const ring = readinessTone(readiness)
  const isWaiting = readiness === 'waiting'
  return (
    <div
      data-testid="epic-dep-member-node"
      data-node-kind="member"
      data-readiness={readiness}
      data-status={issue.status}
      data-issue-number={issue.number}
      data-selected={selected ? 'true' : undefined}
      className="rounded-lg border-2 shadow-sm px-3 py-2 text-xs flex flex-col gap-1"
      style={{
        background: tone.background,
        borderColor: selected ? ring : tone.border,
        color: tone.text,
        minWidth: 180,
        maxWidth: 220,
        cursor: data.navigateTo ? 'pointer' : 'default',
      }}
    >
      <Handle type="target" position={Position.Top} style={{ background: tone.border }} />
      <div className="flex items-center justify-between gap-2">
        <span className="font-mono font-semibold tabular-nums">#{issue.number}</span>
        <span
          data-testid="epic-dep-readiness-marker"
          className="inline-flex items-center gap-1 rounded-full px-1.5 py-0.5 text-[10px] font-semibold uppercase tracking-wide"
          style={{ backgroundColor: ring, color: 'white' }}
        >
          <span className="inline-block h-1.5 w-1.5 rounded-full bg-white" />
          {readinessLabel(readiness)}
        </span>
      </div>
      <div className="truncate font-medium" title={issue.title}>{issue.title}</div>
      {isWaiting && waitingForIssueNumber != null && (
        <div
          data-testid="epic-dep-waiting-for"
          className="text-[10px] font-medium"
          style={{ color: ring }}
        >
          Waiting for #{waitingForIssueNumber}
        </div>
      )}
      <Handle type="source" position={Position.Bottom} style={{ background: tone.border }} />
    </div>
  )
}

export const MemberFlowNode = memo(MemberFlowNodeImpl)
