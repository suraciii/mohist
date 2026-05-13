import type {
  CoderSessionDetail,
  SessionTurn,
  TextPart,
  ReasoningPart,
  ToolPart,
  ErrorPart,
} from './types'

export type PromptKind = 'initial' | 'task' | 'retry' | 'followup' | 'recovery' | 'legacy-missing'

export type DisplayTurnState = 'idle' | 'streaming' | 'finalizing' | 'error'

export interface DisplayPrompt {
  role: 'mohist'
  text: string
  kind: PromptKind
  sentAt: string
  title?: string
  subtitle?: string
  outputPath?: string
  contextFiles?: string[]
}

export interface DisplayChangedFile {
  path: string
  operation: 'created' | 'modified' | 'deleted' | 'moved'
  additions?: number
  deletions?: number
  oldPath?: string
  rawDetail?: string
}

export type DisplayAssistantPart =
  | DisplayTextPart
  | DisplayReasoningPart
  | DisplayToolPart
  | DisplayContextGroupPart
  | DisplayErrorPart
  | DisplayDividerPart

export interface DisplayTextPart {
  id: string
  partType: 'text'
  text: string
  startedAt: string
  completedAt: string | null
  isStreaming?: boolean
}

export interface DisplayReasoningPart {
  id: string
  partType: 'reasoning'
  text: string
  startedAt: string
  completedAt: string | null
}

export interface DisplayToolPart {
  id: string
  partType: 'tool'
  toolCallId: string
  normalizedName: string
  displayTitle?: string
  displaySubtitle?: string
  toolName: string
  status: 'pending' | 'running' | 'completed' | 'failed' | 'cancelled'
  title?: string
  target?: string
  input?: string
  output?: string
  error?: string
  startedAt: string
  completedAt?: string | null
  rawInput?: string
  rawOutput?: string
  changedFiles?: DisplayChangedFile[]
  hasError: boolean
  isContextTool: boolean
}

export interface DisplayContextGroupPart {
  id: string
  partType: 'context-group'
  title: string
  tools: DisplayToolPart[]
  hasError: boolean
}

export interface DisplayErrorPart {
  id: string
  partType: 'error'
  message: string
  kind: 'timeout' | 'failed' | 'cancelled' | 'recovery'
  at: string
}

export interface DisplayDividerPart {
  id: string
  partType: 'divider'
  label: string
  at: string
}

export interface DisplayTurn {
  id: string
  startedAt: string
  completedAt: string | null
  prompt: DisplayPrompt
  assistantParts: DisplayAssistantPart[]
  changedFiles: DisplayChangedFile[]
  state: DisplayTurnState
}

const CONTEXT_TOOL_NAMES = new Set([
  'read', 'read_file', 'glob', 'grep', 'search', 'list',
  'membrowse', 'memread', 'memsearch', 'search_files',
])

const INTERNAL_TOOL_NAMES = new Set(['todowrite', 'todo'])

function isContextTool(normalizedName: string): boolean {
  return CONTEXT_TOOL_NAMES.has(normalizedName.toLowerCase())
}

function isInternalTool(normalizedName: string): boolean {
  return INTERNAL_TOOL_NAMES.has(normalizedName.toLowerCase())
}

function buildDisplayPrompt(turn: SessionTurn): DisplayPrompt {
  const { user } = turn
  return {
    role: 'mohist',
    text: user.text,
    kind: user.kind,
    sentAt: user.sentAt,
    title: user.summary?.title,
    subtitle: user.summary?.subtitle,
    outputPath: user.summary?.outputPath,
    contextFiles: user.summary?.contextFiles,
  }
}

function buildDisplayTextPart(part: TextPart): DisplayTextPart {
  return {
    id: part.id,
    partType: 'text',
    text: part.text,
    startedAt: part.startedAt,
    completedAt: part.completedAt,
  }
}

function buildDisplayReasoningPart(part: ReasoningPart): DisplayReasoningPart {
  return {
    id: part.id,
    partType: 'reasoning',
    text: part.text,
    startedAt: part.startedAt,
    completedAt: part.completedAt,
  }
}

function buildDisplayToolPart(part: ToolPart): DisplayToolPart {
  const { tool } = part
  return {
    id: part.id,
    partType: 'tool',
    toolCallId: tool.toolCallId,
    normalizedName: tool.normalizedName ?? tool.toolName,
    displayTitle: tool.displayTitle,
    displaySubtitle: tool.displaySubtitle,
    toolName: tool.toolName,
    status: tool.status,
    title: tool.title,
    target: tool.target,
    input: tool.input,
    output: tool.output,
    error: tool.error,
    startedAt: tool.startedAt,
    completedAt: tool.completedAt,
    rawInput: tool.rawInput,
    rawOutput: tool.rawOutput,
    changedFiles: tool.changedFiles?.map(cf => ({
      path: cf.path,
      operation: cf.operation,
      additions: cf.additions,
      deletions: cf.deletions,
      oldPath: cf.oldPath,
    })),
    hasError: tool.status === 'failed' || tool.status === 'cancelled' || !!tool.error,
    isContextTool: isContextTool(tool.normalizedName ?? tool.toolName),
  }
}

function buildDisplayErrorPart(part: ErrorPart): DisplayErrorPart {
  return {
    id: part.id,
    partType: 'error',
    message: part.message,
    kind: part.kind,
    at: part.at,
  }
}

function collectChangedFilesFromTools(tools: DisplayToolPart[]): DisplayChangedFile[] {
  const files: DisplayChangedFile[] = []
  for (const tool of tools) {
    if (tool.changedFiles) {
      for (const cf of tool.changedFiles) {
        files.push({ ...cf })
      }
    }
  }
  return files
}

export function projectSessionToDisplayTurns(detail: CoderSessionDetail): DisplayTurn[] {
  return detail.turns.map(turn => projectTurn(turn))
}

function sameSecond(ts1: string, ts2: string): boolean {
  return ts1.slice(0, 19) === ts2.slice(0, 19)
}

function applyReasoningReorder(parts: SessionTurn['assistant']): SessionTurn['assistant'] {
  const result: SessionTurn['assistant'] = []
  let i = 0
  while (i < parts.length) {
    const part = parts[i]
    if (part.type !== 'reasoning') {
      result.push(part)
      i++
      continue
    }
    const reasoningBlockStart = i
    while (i < parts.length && parts[i].type === 'reasoning') {
      i++
    }
    const reasoningBlockEnd = i
    if (i >= parts.length) {
      for (let j = reasoningBlockStart; j < reasoningBlockEnd; j++) {
        result.push(parts[j])
      }
      break
    }
    const followingPart = parts[i]
    if (followingPart.type === 'text') {
      const lastReasoning = parts[reasoningBlockEnd - 1] as ReasoningPart
      if (sameSecond(lastReasoning.startedAt, followingPart.startedAt)) {
        result.push(followingPart)
        for (let j = reasoningBlockStart; j < reasoningBlockEnd; j++) {
          result.push(parts[j])
        }
      } else {
        for (let j = reasoningBlockStart; j < reasoningBlockEnd; j++) {
          result.push(parts[j])
        }
        result.push(followingPart)
      }
    } else {
      for (let j = reasoningBlockStart; j < reasoningBlockEnd; j++) {
        result.push(parts[j])
      }
      result.push(followingPart)
    }
    i++
  }
  return result
}

export function projectTurn(turn: SessionTurn): DisplayTurn {
  const prompt = buildDisplayPrompt(turn)
  const rawParts = turn.assistant

  const displayParts: DisplayAssistantPart[] = []
  const toolStack: DisplayToolPart[] = []

  const reorderedParts = applyReasoningReorder(rawParts)

  const flushContextGroup = () => {
    if (toolStack.length === 0) return
    const groupTools = toolStack.splice(0)
    const hasError = groupTools.some(t => t.hasError)
    const contextToolCount = groupTools.filter(t => t.isContextTool).length
    let title: string
    if (contextToolCount > 0 && groupTools.length > 1) {
      const reads = groupTools.filter(t => t.normalizedName === 'read' || t.normalizedName === 'read_file').length
      const searches = groupTools.filter(t => t.normalizedName === 'grep' || t.normalizedName === 'search' || t.normalizedName === 'search_files').length
      const globs = groupTools.filter(t => t.normalizedName === 'glob').length
      const parts: string[] = []
      if (reads > 0) parts.push(`${reads} read${reads > 1 ? 's' : ''}`)
      if (searches > 0) parts.push(`${searches} search${searches > 1 ? 'es' : ''}`)
      if (globs > 0) parts.push(`${globs} glob${globs > 1 ? 's' : ''}`)
      title = `Gathering context · ${parts.join(' · ')}`
    } else if (groupTools.length === 1) {
      title = groupTools[0].displayTitle ?? groupTools[0].normalizedName
    } else {
      title = 'Gathering context'
    }
    displayParts.push({
      id: `ctx-${groupTools[0].id}`,
      partType: 'context-group',
      title,
      tools: groupTools,
      hasError,
    } as DisplayContextGroupPart)
  }

  for (let i = 0; i < reorderedParts.length; i++) {
    const part = reorderedParts[i]

    if (part.type === 'text') {
      if (toolStack.length > 0) {
        const nextPart = reorderedParts[i + 1]
        const nextIsContextTool = nextPart?.type === 'tool' && isContextTool(nextPart.tool.normalizedName ?? nextPart.tool.toolName)
        if (!nextIsContextTool) {
          flushContextGroup()
        }
      }
      displayParts.push(buildDisplayTextPart(part))
      continue
    }

    if (part.type === 'reasoning') {
      if (toolStack.length > 0) {
        flushContextGroup()
      }
      displayParts.push(buildDisplayReasoningPart(part))
      continue
    }

    if (part.type === 'tool') {
      const normalizedName = part.tool.normalizedName ?? part.tool.toolName
      if (isInternalTool(normalizedName)) {
        continue
      }
      if (part.hidden) {
        continue
      }

      const displayTool = buildDisplayToolPart(part)

      if (toolStack.length > 0) {
        const top = toolStack[toolStack.length - 1]
        const topNorm = top.normalizedName
        const currNorm = displayTool.normalizedName
        const prevIsContext = isContextTool(topNorm)
        const currIsContext = isContextTool(currNorm)

        if (prevIsContext && currIsContext) {
          toolStack.push(displayTool)
          continue
        } else if (!prevIsContext && !currIsContext && topNorm === currNorm) {
          const topIsPending = top.status === 'pending' || top.status === 'running'
          if (topIsPending) {
            toolStack.pop()
            displayParts.push(displayTool)
            continue
          }
        }
        flushContextGroup()
      }

      if (isContextTool(normalizedName)) {
        toolStack.push(displayTool)
      } else {
        displayParts.push(displayTool)
      }
      continue
    }

    if (part.type === 'error') {
      if (toolStack.length > 0) {
        flushContextGroup()
      }
      displayParts.push(buildDisplayErrorPart(part))
      continue
    }
  }

  if (toolStack.length > 0) {
    flushContextGroup()
  }

  const state: DisplayTurnState = turn.completedAt
    ? 'idle'
    : turn.incomplete
      ? 'finalizing'
      : 'streaming'

  const changedFiles = collectChangedFilesFromTools(
    displayParts.filter((p): p is DisplayToolPart => p.partType === 'tool'),
  )

  return {
    id: turn.id,
    startedAt: turn.startedAt,
    completedAt: turn.completedAt,
    prompt,
    assistantParts: displayParts,
    changedFiles,
    state,
  }
}

export function extractTurnChangedFiles(turn: DisplayTurn): DisplayChangedFile[] {
  return turn.changedFiles
}