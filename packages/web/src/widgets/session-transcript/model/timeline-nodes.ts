import type { DisplayToolPart, DisplayTurn } from './session-transcript-display'
import { deriveVerbFamily } from '../ui/tool-views/shared'
import { CONTEXT_TOOL_NAMES } from './session-transcript-display'

export type TimelineNodeKind = 'turn' | 'failed' | 'file-change' | 'read-explore'

export interface TimelineNode {
  kind: TimelineNodeKind
  turnId: string
  turnIndex: number
  toolCallId?: string
  tool?: DisplayToolPart
}

function isCompleted(tool: DisplayToolPart): boolean {
  return tool.status === 'completed'
}

function isReadExplore(tool: DisplayToolPart): boolean {
  if (!isCompleted(tool)) return false
  return CONTEXT_TOOL_NAMES.has(tool.normalizedName.toLowerCase())
}

function isFileChange(tool: DisplayToolPart): boolean {
  if (tool.status === 'failed') return false
  if (tool.changedFiles && tool.changedFiles.length > 0) return true
  return deriveVerbFamily(tool.normalizedName) === 'edit'
}

function emitToolNodes(turn: DisplayTurn, turnIndex: number, nodes: TimelineNode[]) {
  for (const part of turn.assistantParts) {
    if (part.partType === 'tool') {
      pushToolNode(part, turn, turnIndex, nodes)
      continue
    }
    if (part.partType === 'context-group') {
      for (const tool of part.tools) {
        pushToolNode(tool, turn, turnIndex, nodes)
      }
    }
  }
}

function pushToolNode(tool: DisplayToolPart, turn: DisplayTurn, turnIndex: number, nodes: TimelineNode[]) {
  if (tool.status === 'failed') {
    nodes.push({ kind: 'failed', turnId: turn.id, turnIndex, toolCallId: tool.toolCallId, tool })
    return
  }
  if (isFileChange(tool)) {
    nodes.push({ kind: 'file-change', turnId: turn.id, turnIndex, toolCallId: tool.toolCallId, tool })
    return
  }
  if (isReadExplore(tool)) {
    nodes.push({ kind: 'read-explore', turnId: turn.id, turnIndex, toolCallId: tool.toolCallId, tool })
  }
}

export function projectSessionToTimelineNodes(turns: DisplayTurn[]): TimelineNode[] {
  const nodes: TimelineNode[] = []
  turns.forEach((turn, index) => {
    const turnIndex = index + 1
    nodes.push({ kind: 'turn', turnId: turn.id, turnIndex })
    emitToolNodes(turn, turnIndex, nodes)
  })
  return nodes
}