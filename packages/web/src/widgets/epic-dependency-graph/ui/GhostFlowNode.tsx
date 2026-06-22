import { memo } from 'react'
import { Handle, Position, type NodeProps } from '@xyflow/react'
import type { GhostNodeData } from '../model/layout'

export interface GhostFlowNodeProps extends NodeProps {
  data: GhostNodeData
}

function GhostFlowNodeImpl({ data, selected }: GhostFlowNodeProps) {
  if (data.kind !== 'ghost') return null
  const { number, title, status, resolved, referencedBy } = data
  return (
    <div
      data-testid="epic-dep-ghost-node"
      data-node-kind="ghost"
      data-resolved={resolved ? 'true' : 'false'}
      data-issue-number={number}
      data-selected={selected ? 'true' : undefined}
      className="rounded-lg border-2 border-dashed px-3 py-2 text-xs flex flex-col gap-1"
      style={{
        background: '#fafafa',
        borderColor: resolved ? '#94a3b8' : '#f59e0b',
        color: '#475569',
        minWidth: 180,
        maxWidth: 220,
      }}
    >
      <Handle type="target" position={Position.Top} style={{ background: '#94a3b8' }} />
      <div className="flex items-center justify-between gap-2">
        <span className="font-mono font-semibold tabular-nums">#{number}</span>
        <span
          data-testid="epic-dep-ghost-tag"
          className="inline-flex items-center gap-1 rounded-full px-1.5 py-0.5 text-[10px] font-semibold uppercase tracking-wide"
          style={{
            backgroundColor: resolved ? '#94a3b8' : '#f59e0b',
            color: 'white',
          }}
        >
          {resolved ? 'External' : 'Unresolved'}
        </span>
      </div>
      <div className="truncate font-medium" title={title || (resolved ? status : `Issue #${number} (unresolved)`)}>
        {resolved ? (title || `#${number} (external)`) : `#${number} (unresolved)`}
      </div>
      {resolved && status && (
        <div className="text-[10px] text-muted-foreground" data-testid="epic-dep-ghost-status">
          {status}
        </div>
      )}
      {referencedBy.length > 0 && (
        <div className="text-[10px] text-muted-foreground/80" data-testid="epic-dep-ghost-refs">
          referenced by {referencedBy.map(n => `#${n}`).join(', ')}
        </div>
      )}
      <Handle type="source" position={Position.Bottom} style={{ background: '#94a3b8' }} />
    </div>
  )
}

export const GhostFlowNode = memo(GhostFlowNodeImpl)
