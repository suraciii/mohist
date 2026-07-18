import { type KeyboardEvent, useCallback, useMemo } from 'react'
import { Button } from '@/shared/ui/components/button'
import type { DisplayTurn } from '../model/session-transcript-display'
import { projectSessionToTimelineNodes, type TimelineNode, type TimelineNodeKind } from '../model/timeline-nodes'
import type { TranscriptLocateTarget } from '../model/use-transcript-locate'

interface MiniTimelineProps {
  turns: DisplayTurn[]
  locate: (target: TranscriptLocateTarget) => void
  groupIdsByToolCallId?: Map<string, string>
}

const NODE_KIND_LABEL: Record<TimelineNodeKind, string> = {
  'turn': 'Turn',
  'failed': 'Failed tool call',
  'file-change': 'File change',
  'read-explore': 'Exploratory read',
}

const NODE_TONE: Record<TimelineNodeKind, string> = {
  'turn': 'neutral',
  'failed': 'danger',
  'file-change': 'success',
  'read-explore': 'neutral',
}

const NODE_DOT_CLASS: Record<TimelineNodeKind, string> = {
  'turn': 'bg-muted-foreground/60',
  'failed': 'bg-danger',
  'file-change': 'bg-success',
  'read-explore': 'bg-muted-foreground/40',
}

const NODE_DOT_SHAPE: Record<TimelineNodeKind, string> = {
  'turn': 'h-1 w-3 rounded-sm',
  'failed': 'h-2 w-2 rounded-full',
  'file-change': 'h-2 w-2 rounded-full',
  'read-explore': 'h-2 w-2 rounded-full',
}

function nodeAriaLabel(node: TimelineNode): string {
  if (node.kind === 'turn') return `Jump to turn ${node.turnIndex}`
  const prefix = NODE_KIND_LABEL[node.kind]
  return `${prefix} · turn ${node.turnIndex}`
}

function buildLocateTarget(node: TimelineNode, groupIdsByToolCallId?: Map<string, string>): TranscriptLocateTarget {
  if (node.kind === 'turn') {
    return { turnId: node.turnId }
  }
  if (!node.toolCallId) return {}
  const groupId = groupIdsByToolCallId?.get(node.toolCallId)
  return { toolCallId: node.toolCallId, groupId }
}

export function MiniTimeline({ turns, locate, groupIdsByToolCallId }: MiniTimelineProps) {
  const nodes = useMemo(() => projectSessionToTimelineNodes(turns), [turns])

  const handleActivate = useCallback((node: TimelineNode) => {
    locate(buildLocateTarget(node, groupIdsByToolCallId))
  }, [locate, groupIdsByToolCallId])

  if (nodes.length === 0) return null

  return (
    <aside
      data-testid="transcript-mini-timeline"
      data-mini-timeline=""
      aria-label="Session mini timeline"
      className="hidden xl:flex sticky top-16 self-start flex-col items-center w-12 shrink-0"
    >
      <div
        data-testid="transcript-mini-timeline-track"
        className="flex flex-col items-center gap-1 py-2"
      >
        {nodes.map((node, index) => (
          <MiniTimelineNodeButton
            key={`${node.kind}:${node.toolCallId ?? node.turnId}:${index}`}
            node={node}
            onActivate={handleActivate}
          />
        ))}
      </div>
    </aside>
  )
}

interface MiniTimelineNodeButtonProps {
  node: TimelineNode
  onActivate: (node: TimelineNode) => void
}

function MiniTimelineNodeButton({ node, onActivate }: MiniTimelineNodeButtonProps) {
  const handleKeyDown = useCallback((event: KeyboardEvent<HTMLButtonElement>) => {
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault()
      onActivate(node)
    }
  }, [node, onActivate])

  const handleClick = useCallback(() => {
    onActivate(node)
  }, [node, onActivate])

  return (
    <Button
      type="button"
      variant="ghost"
      size="icon-xs"
      onClick={handleClick}
      onKeyDown={handleKeyDown}
      aria-label={nodeAriaLabel(node)}
      title={nodeAriaLabel(node)}
      data-testid="transcript-mini-timeline-node"
      data-mini-timeline-node-kind={node.kind}
      data-mini-timeline-node-tone={NODE_TONE[node.kind]}
      data-mini-timeline-turn-index={node.turnIndex}
      data-mini-timeline-tool-call-id={node.toolCallId ?? undefined}
      data-mini-timeline-turn-id={node.turnId}
      className="inline-flex items-center justify-center p-0"
    >
      <span
        aria-hidden="true"
        className={`block ${NODE_DOT_SHAPE[node.kind]} ${NODE_DOT_CLASS[node.kind]}`}
      />
    </Button>
  )
}