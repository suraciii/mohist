import { useEffect, useMemo } from 'react'
import { useNavigate } from 'react-router-dom'
import {
  ReactFlow,
  Background,
  Controls,
  type Edge,
  type Node,
  type NodeMouseHandler,
  type NodeTypes,
} from '@xyflow/react'
import '@xyflow/react/dist/style.css'
import type { LinkedIssue } from '../../../entities/epic'
import { buildGraphEdges, detectCycle } from '../model/graph'
import { buildLayout, type GraphNodeData, isMemberNodeId, parseMemberNodeNumber } from '../model/layout'
import { MemberFlowNode } from './MemberFlowNode'
import { GhostFlowNode } from './GhostFlowNode'

const nodeTypes: NodeTypes = {
  epicDepMember: MemberFlowNode,
  epicDepGhost: GhostFlowNode,
}

export type Renderability = 'renderable' | 'cyclic' | 'empty'

export interface DependencyGraphCanvasProps {
  linkedIssues: LinkedIssue[]
  navigatePathFor: (issueNumber: number) => string | null
  onRenderabilityChange?: (state: { renderable: boolean; reason: Renderability | null }) => void
}

interface PreparedLayout {
  nodes: Node<GraphNodeData>[]
  edges: Edge[]
  renderability: Renderability
}

function prepareLayout(
  linkedIssues: LinkedIssue[],
  navigatePathFor: (issueNumber: number) => string | null,
): PreparedLayout {
  if (linkedIssues.length < 2) {
    return { nodes: [], edges: [], renderability: 'empty' }
  }
  const { edges: graphEdges, externalGhosts } = buildGraphEdges(linkedIssues)
  if (detectCycle(graphEdges)) {
    return { nodes: [], edges: [], renderability: 'cyclic' }
  }
  const layout = buildLayout({
    linkedIssues,
    edges: graphEdges,
    externalGhosts,
    navigatePathFor,
  })
  return { nodes: layout.nodes, edges: layout.edges, renderability: 'renderable' }
}

export function DependencyGraphCanvas({
  linkedIssues,
  navigatePathFor,
  onRenderabilityChange,
}: DependencyGraphCanvasProps) {
  const navigate = useNavigate()
  const prepared = useMemo(
    () => prepareLayout(linkedIssues, navigatePathFor),
    [linkedIssues, navigatePathFor],
  )

  useRenderabilityEffect(onRenderabilityChange, prepared.renderability)

  const handleNodeClick: NodeMouseHandler = (_event, node) => {
    if (!isMemberNodeId(node.id)) return
    const number = parseMemberNodeNumber(node.id)
    if (number == null) return
    const target = navigatePathFor(number)
    if (target) {
      navigate(target)
    }
  }

  if (prepared.renderability !== 'renderable') {
    return null
  }

  return (
    <div
      data-testid="epic-dep-graph-canvas"
      className="h-[560px] w-full min-w-[640px] rounded-lg border bg-background"
    >
      <ReactFlow
        nodes={prepared.nodes}
        edges={prepared.edges}
        nodeTypes={nodeTypes}
        onNodeClick={handleNodeClick}
        nodesDraggable={false}
        nodesConnectable={false}
        edgesFocusable={false}
        elementsSelectable
        fitView
        fitViewOptions={{ padding: 0.2 }}
        proOptions={{ hideAttribution: true }}
        minZoom={0.25}
        maxZoom={2}
      >
        <Background gap={16} size={1} />
        <Controls showInteractive={false} />
      </ReactFlow>
    </div>
  )
}

function useRenderabilityEffect(
  onRenderabilityChange: DependencyGraphCanvasProps['onRenderabilityChange'],
  renderability: Renderability,
) {
  useEffect(() => {
    if (!onRenderabilityChange) return
    onRenderabilityChange({
      renderable: renderability === 'renderable',
      reason: renderability === 'renderable' ? null : renderability,
    })
  }, [onRenderabilityChange, renderability])
}
