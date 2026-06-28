import type {
  DisplayTurn,
  DisplayAssistantPart,
  DisplayToolPart,
  DisplayContextGroupPart,
  DisplayPrompt,
  PromptKind,
} from './session-transcript-display'

const KIND_LABELS: Record<PromptKind, string> = {
  initial: 'Initial Task',
  task: 'Task',
  retry: 'Retry',
  followup: 'Follow-up',
  recovery: 'Recovery',
  'legacy-missing': 'Missing Prompt',
}

function promptKindLabel(kind: PromptKind): string {
  return KIND_LABELS[kind] ?? kind
}

function formatTurnTimestamp(iso: string): string {
  return new Date(iso).toLocaleString()
}

function formatReasoningSizeKb(text: string): string {
  const kb = text.length / 1024
  return kb.toFixed(1)
}

function shouldRenderOutputPath(prompt: DisplayPrompt): boolean {
  if (!prompt.outputPath) return false
  if (prompt.outputPath === prompt.subtitle) return false
  if (prompt.subtitle && prompt.subtitle.endsWith(prompt.outputPath)) return false
  return true
}

function promptSectionLines(prompt: DisplayPrompt): string[] {
  const lines: string[] = []
  if (prompt.title) lines.push(prompt.title)
  if (prompt.subtitle) lines.push(prompt.subtitle)
  if (shouldRenderOutputPath(prompt)) {
    lines.push(`Output: ${prompt.outputPath}`)
  }
  if (prompt.contextFiles && prompt.contextFiles.length > 0) {
    lines.push(`Context: ${prompt.contextFiles.join(', ')}`)
  }
  if (prompt.text) lines.push(prompt.text)
  return lines
}

function toolSummaryLine(tool: DisplayToolPart): string {
  const title = tool.displayTitle ?? tool.title
  if (title) return `[tool ${tool.normalizedName}] ${title}`
  if (tool.target) return `[tool ${tool.normalizedName}] ${tool.target}`
  return `[tool ${tool.normalizedName}]`
}

function contextGroupSummary(group: DisplayContextGroupPart): string {
  return `[context-group] ${group.title}`
}

function assistantPartLines(part: DisplayAssistantPart): string[] {
  switch (part.partType) {
    case 'text':
      return [part.text]
    case 'reasoning':
      return [`[reasoning omitted, ${formatReasoningSizeKb(part.text)} KB]`]
    case 'tool':
      return [toolSummaryLine(part)]
    case 'context-group':
      return [contextGroupSummary(part)]
    case 'error':
      return [`[error] ${part.message}`]
    case 'divider':
      return [`[divider] ${part.label}`]
    default:
      return []
  }
}

function turnSection(index: number, turn: DisplayTurn): string[] {
  const header = `== Turn ${index} · ${promptKindLabel(turn.prompt.kind)} · ${formatTurnTimestamp(turn.startedAt)} ==`
  const promptLines = promptSectionLines(turn.prompt)
  const assistantLines = turn.assistantParts.flatMap(assistantPartLines)
  return [header, ...promptLines, ...assistantLines]
}

export function serializeTranscriptPlainText(turns: DisplayTurn[]): string {
  if (turns.length === 0) return ''
  return turns
    .map((turn, i) => turnSection(i + 1, turn).join('\n'))
    .join('\n\n')
    .trimEnd()
    .concat('\n')
}