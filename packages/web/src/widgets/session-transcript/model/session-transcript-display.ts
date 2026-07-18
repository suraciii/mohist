import type {
  CoderSessionDetail,
  SessionTurn,
  TextPart,
  ReasoningPart,
  ToolPart,
  ErrorPart,
} from '../../../entities/coder-session'

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
  metadata?: Record<string, unknown>
  details?: Record<string, unknown>
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

export const CONTEXT_TOOL_NAMES = new Set([
  'read', 'read_file', 'glob', 'grep', 'search', 'list',
  'membrowse', 'memread', 'memsearch', 'search_files',
])

function isContextTool(normalizedName: string): boolean {
  return CONTEXT_TOOL_NAMES.has(normalizedName.toLowerCase())
}

function buildDisplayPrompt(turn: SessionTurn): DisplayPrompt {
  const { user } = turn
  const subtitle = user.summary?.subtitle
  const outputPath = user.summary?.outputPath
  const subtitleOutput = subtitle?.trim().replace(/^output\s*:\s*/i, '').trim()
  const canonicalSubtitle = subtitleOutput && outputPath && subtitleOutput === outputPath.trim()
    ? undefined
    : subtitle
  return {
    role: 'mohist',
    text: user.text,
    kind: user.kind,
    sentAt: user.sentAt,
    title: user.summary?.title,
    subtitle: canonicalSubtitle,
    outputPath,
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
  const mutationFiles = tool.details?.family === 'mutation' && Array.isArray(tool.details.files)
    ? tool.details.files
      .filter((file): file is Record<string, unknown> => file && typeof file === 'object' && typeof (file as Record<string, unknown>).path === 'string')
      .map(file => ({
        path: String(file.path),
        operation: (file.operation === 'created' || file.operation === 'modified' || file.operation === 'deleted' || file.operation === 'moved' ? file.operation : 'modified') as DisplayChangedFile['operation'],
        additions: typeof file.additions === 'number' ? file.additions : undefined,
        deletions: typeof file.deletions === 'number' ? file.deletions : undefined,
        oldPath: typeof file.oldPath === 'string' ? file.oldPath : undefined,
        rawDetail: typeof file.diff === 'string' ? file.diff : typeof file.content === 'string' ? file.content : undefined,
      }))
    : undefined
  const changedFiles = tool.changedFiles?.map(cf => ({
    path: cf.path,
    operation: cf.operation,
    additions: cf.additions,
    deletions: cf.deletions,
    oldPath: cf.oldPath,
    rawDetail: cf.rawDetail,
  }))
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
    metadata: tool.metadata,
    details: tool.details,
    changedFiles: changedFiles && changedFiles.length > 0 ? changedFiles : mutationFiles,
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

function collectChangedFilesFromTools(parts: DisplayAssistantPart[]): DisplayChangedFile[] {
  const files: DisplayChangedFile[] = []
  for (const part of parts) {
    if (part.partType === 'tool') {
      const includeNestedFiles = part.normalizedName !== 'apply_patch'
      if (includeNestedFiles && part.changedFiles) {
        for (const cf of part.changedFiles) {
          files.push({ ...cf })
        }
      }
    } else if (part.partType === 'context-group') {
      const nestedFiles = collectChangedFilesFromTools(part.tools as DisplayAssistantPart[])
      files.push(...nestedFiles)
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
    if (groupTools.length === 1) {
      displayParts.push(groupTools[0])
      return
    }
    const hasError = groupTools.some(t => t.hasError)
    const reads = groupTools.filter(t => t.normalizedName === 'read' || t.normalizedName === 'read_file').length
    const searches = groupTools.filter(t => t.normalizedName === 'grep' || t.normalizedName === 'search' || t.normalizedName === 'search_files').length
    const globs = groupTools.filter(t => t.normalizedName === 'glob').length
    const parts: string[] = []
    if (reads > 0) parts.push(`${reads} read${reads > 1 ? 's' : ''}`)
    if (searches > 0) parts.push(`${searches} search${searches > 1 ? 'es' : ''}`)
    if (globs > 0) parts.push(`${globs} glob${globs > 1 ? 's' : ''}`)
    const title = parts.length > 0
      ? `Explored · ${parts.join(' · ')}`
      : 'Explored'
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

  const changedFiles = collectChangedFilesFromTools(displayParts)

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
