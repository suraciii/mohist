import type { DisplayToolPart, DisplayTurn } from './session-transcript-display'

export function selectFailedToolCalls(turns: DisplayTurn[]): DisplayToolPart[] {
  const failed: DisplayToolPart[] = []
  for (const turn of turns) {
    if (!Array.isArray(turn.assistantParts)) continue
    for (const part of turn.assistantParts) {
      if (part.partType === 'context-group') {
        failed.push(...part.tools.filter((tool) => tool.status === 'failed'))
      } else if (part.partType === 'tool' && part.status === 'failed') {
        failed.push(part)
      }
    }
  }
  return failed
}

export function selectToolCallGroupIds(turns: DisplayTurn[]): Map<string, string> {
  const groupIds = new Map<string, string>()
  for (const turn of turns) {
    if (!Array.isArray(turn.assistantParts)) continue
    for (const part of turn.assistantParts) {
      if (part.partType !== 'context-group') continue
      for (const tool of part.tools) groupIds.set(tool.toolCallId, part.id)
    }
  }
  return groupIds
}
