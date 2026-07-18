import type { DisplayToolPart, DisplayTurn } from './session-transcript-display'

export function selectActiveToolCall(turns: DisplayTurn[]): DisplayToolPart | null {
  let last: DisplayToolPart | null = null
  for (const turn of turns) {
    for (const part of turn.assistantParts) {
      if (part.partType === 'context-group') {
        for (const tool of part.tools) {
          if (tool.status === 'pending' || tool.status === 'running') {
            last = tool
          }
        }
        continue
      }
      if (part.partType === 'tool' && (part.status === 'pending' || part.status === 'running')) {
        last = part
      }
    }
  }
  return last
}