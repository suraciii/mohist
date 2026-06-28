import type {
  DisplayTurn,
  DisplayAssistantPart,
  DisplayToolPart,
  DisplayContextGroupPart,
  DisplayChangedFile,
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

function changedFileLine(file: DisplayChangedFile): string {
  const counts: string[] = []
  if (file.additions !== undefined) counts.push(`+${file.additions}`)
  if (file.deletions !== undefined) counts.push(`-${file.deletions}`)
  const countSuffix = counts.length > 0 ? ` (${counts.join(' ')})` : ''
  const moveSuffix = file.operation === 'moved' && file.oldPath ? ` from ${file.oldPath}` : ''
  return `  [changed-file] ${file.operation} ${file.path}${moveSuffix}${countSuffix}`
}

function labeledBlock(label: string, text: string | undefined): string[] {
  if (!text) return []
  return [`  ${label}:`, text]
}

function jsonBlock(label: string, value: unknown): string[] {
  if (value === undefined || value === null) return []
  return [`  ${label}:`, JSON.stringify(value, null, 2)]
}

function toolLines(tool: DisplayToolPart): string[] {
  const lines = [toolSummaryLine(tool)]
  lines.push(...labeledBlock('input', tool.input ?? tool.rawInput))
  if (tool.rawInput && tool.rawInput !== tool.input) {
    lines.push(...labeledBlock('raw input', tool.rawInput))
  }
  lines.push(...labeledBlock('output', tool.output ?? tool.rawOutput))
  if (tool.rawOutput && tool.rawOutput !== tool.output) {
    lines.push(...labeledBlock('raw output', tool.rawOutput))
  }
  lines.push(...jsonBlock('details', tool.details))
  if (tool.error) lines.push(`  [tool-error] ${tool.error}`)
  if (tool.changedFiles && tool.changedFiles.length > 0) {
    lines.push('  changed files:')
    lines.push(...tool.changedFiles.map(changedFileLine))
  }
  return lines
}

function contextGroupLines(group: DisplayContextGroupPart): string[] {
  const lines = [`[context-group] ${group.title}`]
  for (const tool of group.tools) {
    lines.push(...toolLines(tool).map((line) => `  ${line}`))
  }
  return lines
}

function assistantPartLines(part: DisplayAssistantPart): string[] {
  switch (part.partType) {
    case 'text':
      return [part.text]
    case 'reasoning':
      return [`[reasoning omitted, ${formatReasoningSizeKb(part.text)} KB]`]
    case 'tool':
      return toolLines(part)
    case 'context-group':
      return contextGroupLines(part)
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
