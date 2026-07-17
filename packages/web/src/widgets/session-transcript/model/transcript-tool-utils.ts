import type { FileChangeSummary } from '../../../entities/coder-session'

export interface EditInput {
  filePath: string
  oldString: string
  newString: string
  patch?: string
}

export function parseJsonSafely(input: string | undefined): Record<string, unknown> | null {
  if (!input) return null
  try {
    const parsed = JSON.parse(input)
    if (typeof parsed !== 'object' || parsed === null) return null
    return parsed as Record<string, unknown>
  } catch {
    return null
  }
}

export function getToolLabel(toolName: string, rawInput: string | undefined): string | undefined {
  const parsed = parseJsonSafely(rawInput)
  if (!parsed) return undefined

  switch (toolName) {
    case 'webfetch': {
      const url = parsed.url ?? parsed.uri
      if (typeof url === 'string') return url
      const target = parsed.target ?? parsed.site
      if (typeof target === 'string') return target
      break
    }
    case 'task': {
      const desc = parsed.description ?? parsed.task ?? parsed.summary
      if (typeof desc === 'string') return desc
      break
    }
    case 'skill': {
      const name = parsed.name ?? parsed.skill ?? parsed.id
      if (typeof name === 'string') return name
      break
    }
    case 'search':
    case 'grep': {
      const query = parsed.query ?? parsed.pattern ?? parsed.search
      if (typeof query === 'string') return query
      break
    }
    case 'memread':
    case 'membrowse':
    case 'memsearch': {
      const uri = parsed.uri ?? parsed.path ?? parsed.resource
      if (typeof uri === 'string') return uri
      break
    }
    case 'read':
    case 'glob': {
      const fp = parsed.filePath ?? parsed.file_path ?? parsed.path
      if (typeof fp === 'string') return fp
      break
    }
    case 'todowrite': {
      const todos = parsed.todos
      if (Array.isArray(todos)) return `${todos.length} items`
      break
    }
    case 'bash': {
      const cmd = parsed.command ?? parsed.script ?? parsed.cmd
      if (typeof cmd === 'string') return cmd
      break
    }
    case 'edit':
    case 'write': {
      const fp = parsed.filePath ?? parsed.file_path ?? parsed.path
      if (typeof fp === 'string') return fp.split('/').pop() ?? fp
      break
    }
    case 'question': {
      const q = parsed.question ?? parsed.query ?? parsed.text
      if (typeof q === 'string') return q.length > 60 ? q.slice(0, 60) + '...' : q
      break
    }
    default: {
      const url = parsed.url
      if (typeof url === 'string') return url
      const fp = parsed.filePath ?? parsed.file_path ?? parsed.path
      if (typeof fp === 'string') return fp
      const desc = parsed.description ?? parsed.name
      if (typeof desc === 'string') return desc
      const query = parsed.query
      if (typeof query === 'string') return query
    }
  }
  return undefined
}

export function getToolArgs(toolName: string, rawInput: string | undefined): string[] {
  const parsed = parseJsonSafely(rawInput)
  if (!parsed) return []

  const args: string[] = []

  switch (toolName) {
    case 'webfetch':
      if (typeof parsed.method === 'string') args.push(parsed.method)
      if (typeof parsed.format === 'string') args.push(parsed.format)
      break
    case 'search':
    case 'grep':
      if (typeof parsed.type === 'string') args.push(parsed.type)
      if (typeof parsed.scope === 'string') args.push(parsed.scope)
      break
    case 'read':
    case 'glob':
      if (parsed.recursive) args.push('recursive')
      if (typeof parsed.include === 'string') args.push(parsed.include)
      break
    case 'bash':
      if (parsed.timeout) args.push(`timeout:${parsed.timeout}`)
      if (typeof parsed.cwd === 'string') args.push(parsed.cwd.split('/').pop() ?? '')
      break
    case 'edit':
    case 'write':
      if (parsed.oldString || parsed.old_string) args.push('edit')
      break
    case 'task':
      if (parsed.priority) args.push(String(parsed.priority))
      break
    case 'memsearch':
      if (parsed.limit) args.push(`limit:${parsed.limit}`)
      if (parsed.score_threshold) args.push('threshold')
      break
    default: {
      if (parsed.format) args.push(String(parsed.format))
      if (parsed.language) args.push(String(parsed.language))
      if (parsed.mode) args.push(String(parsed.mode))
      if (parsed.level) args.push(String(parsed.level))
    }
  }

  return args
}

export type ToolDisplayType = 'diff' | 'terminal' | 'summary' | 'generic'

export const TOOL_DISPLAY_TYPE: Record<string, ToolDisplayType> = {
  edit: 'diff',
  write: 'diff',
  apply_patch: 'diff',
  bash: 'terminal',
  read: 'summary',
  glob: 'summary',
  grep: 'summary',
  todowrite: 'summary',
  webfetch: 'summary',
  memread: 'summary',
  membrowse: 'summary',
  memsearch: 'summary',
}

export function getDisplayType(toolName: string): ToolDisplayType {
  return TOOL_DISPLAY_TYPE[toolName] ?? 'generic'
}

export function parsePatchOperations(patchText: string): FileChangeSummary[] {
  const unescaped = patchText.replace(/\\n/g, '\n')
  const changes: FileChangeSummary[] = []
  const addRegex = /^\*\*\* Add File:\s*(.+)/
  const updateRegex = /^\*\*\* Update File:\s*(.+)/
  const deleteRegex = /^\*\*\* Delete File:\s*(.+)/
  const moveRegex = /^\*\*\* Move to:\s*(.+)/
  const oldPathRegex = /^OldPath:\s*(.+)/

  const lines = unescaped.split('\n')
  let currentOp: 'created' | 'modified' | 'deleted' | 'moved' | null = null
  let currentPath: string | null = null
  let oldPath: string | null = null
  let additions = 0
  let deletions = 0

  for (const line of lines) {
    const addMatch = line.match(addRegex)
    const updateMatch = line.match(updateRegex)
    const deleteMatch = line.match(deleteRegex)
    const moveMatch = line.match(moveRegex)
    const oldPathMatch = line.match(oldPathRegex)

    if (addMatch) {
      if (currentPath) {
        changes.push({ path: currentPath, operation: currentOp!, additions, deletions, oldPath: oldPath ?? undefined })
      }
      currentOp = 'created'
      currentPath = addMatch[1].trim()
      additions = 0
      deletions = 0
      oldPath = null
    } else if (updateMatch) {
      if (currentPath) {
        changes.push({ path: currentPath, operation: currentOp!, additions, deletions, oldPath: oldPath ?? undefined })
      }
      currentOp = 'modified'
      currentPath = updateMatch[1].trim()
      additions = 0
      deletions = 0
      oldPath = null
    } else if (deleteMatch) {
      if (currentPath) {
        changes.push({ path: currentPath, operation: currentOp!, additions, deletions, oldPath: oldPath ?? undefined })
      }
      currentOp = 'deleted'
      currentPath = deleteMatch[1].trim()
      additions = 0
      deletions = 0
      oldPath = null
    } else if (moveMatch) {
      if (currentPath) {
        changes.push({ path: currentPath, operation: currentOp!, additions, deletions, oldPath: oldPath ?? undefined })
      }
      currentOp = 'moved'
      currentPath = moveMatch[1].trim()
      additions = 0
      deletions = 0
    } else if (oldPathMatch) {
      oldPath = oldPathMatch[1].trim()
    } else if (line.startsWith('+') && !line.startsWith('+++')) {
      additions++
    } else if (line.startsWith('-') && !line.startsWith('---')) {
      deletions++
    }
  }

  if (currentPath && currentOp) {
    changes.push({ path: currentPath, operation: currentOp, additions, deletions, oldPath: oldPath ?? undefined })
  }

  return changes
}

export function parseEditInput(rawInput: string | undefined): EditInput | null {
  if (!rawInput) return null
  try {
    const parsed = JSON.parse(rawInput)
    if (typeof parsed !== 'object' || parsed === null) return null
    const filePath = parsed.file_path ?? parsed.filePath ?? parsed.path ?? ''
    const oldString = parsed.old_string ?? parsed.oldString ?? ''
    const newString = parsed.new_string ?? parsed.newString ?? parsed.content ?? ''
    if (typeof parsed.patchText === 'string' && parsed.patchText.includes('*** ')) {
      return { filePath: extractPatchTarget(parsed.patchText) || filePath, oldString, newString, patch: parsed.patchText }
    }
    const patch = typeof parsed.patchText === 'string' ? parsed.patchText
      : typeof parsed.patch === 'string' ? parsed.patch
        : undefined
    return { filePath, oldString, newString, patch }
  } catch {
    const patchMatch = rawInput.match(/"patchText"\s*:\s*"([^"]+)"/)
    if (patchMatch) {
      const potentialPatch = patchMatch[1]
      if (potentialPatch.includes('*** ')) {
        return { filePath: extractPatchTarget(potentialPatch) || 'patch', oldString: '', newString: '', patch: potentialPatch }
      }
    }
    if (rawInput.includes('*** Begin Patch') || rawInput.includes('*** Update File:') || rawInput.includes('*** Add File:') || rawInput.includes('*** Delete File:') || rawInput.includes('*** Move to:')) {
      return { filePath: extractPatchTarget(rawInput), oldString: '', newString: '', patch: rawInput }
    }
    return null
  }
}

function extractPatchTarget(patch: string): string {
  const match = patch.match(/^\*\*\* (?:Update|Add|Delete) File: (.+)$/m)
  return match?.[1]?.trim() ?? 'patch'
}

export function parseEditWriteChanges(parsed: EditInput): FileChangeSummary[] {
  if (!parsed || !parsed.filePath) return []
  const fileName = parsed.filePath.split('/').pop() ?? parsed.filePath
  const isNewFile = !parsed.oldString
  const operation: 'created' | 'modified' = isNewFile ? 'created' : 'modified'

  let additions = 0
  let deletions = 0
  if (parsed.oldString && parsed.newString) {
    const oldLines = parsed.oldString.split('\n').length
    const newLines = parsed.newString.split('\n').length
    additions = newLines
    deletions = oldLines
  }

  return [{
    path: fileName,
    operation,
    additions: additions || undefined,
    deletions: deletions || undefined,
  }]
}

export function getFallbackSubtitle(rawInput?: string): string | undefined {
  if (!rawInput) return undefined
  try {
    const parsed = JSON.parse(rawInput)
    const desc = parsed.description ?? parsed.name
    if (typeof desc === 'string') return desc
    const query = parsed.query
    if (typeof query === 'string') return query
    const url = parsed.url
    if (typeof url === 'string') return url
    const fp = parsed.filePath ?? parsed.file_path ?? parsed.path
    if (typeof fp === 'string') return fp
    return undefined
  } catch {
    return undefined
  }
}

export function getFilePathFromInput(input: string | undefined): string | null {
  const parsed = parseJsonSafely(input)
  if (!parsed) return null
  const fp = parsed.filePath ?? parsed.file_path ?? parsed.path
  if (typeof fp === 'string') return fp
  return null
}

export function stringifyPayload(payload: unknown): string | undefined {
  if (payload === undefined || payload === null) return undefined
  return typeof payload === 'string' ? payload : JSON.stringify(payload)
}

function inferTitleToolFamily(titleLower: string): string | undefined {
  if (titleLower.includes('apply_patch')) return 'apply_patch'
  if (titleLower.includes('search_files')) return 'search_files'
  if (titleLower.includes('webfetch')) return 'webfetch'
  if (titleLower.includes('websearch')) return 'websearch'
  if (titleLower.includes('todowrite')) return 'todowrite'
  if (titleLower === 'todo' || titleLower.startsWith('todo:') || titleLower.includes(' todo ')) return 'todo'
  if (titleLower.includes('bash')) return 'bash'
  if (titleLower.includes('shell')) return 'shell'
  if (titleLower.includes('grep')) return 'grep'
  if (titleLower.includes('glob')) return 'glob'
  if (titleLower.includes('read')) return 'read'
  if (titleLower.includes('write')) return 'write'
  if (titleLower.includes('edit')) return 'edit'
  if (titleLower.includes('list')) return 'list'
  if (titleLower.includes('question')) return 'question'
  if (titleLower.includes('search')) return 'search'
  return undefined
}

function inferSemanticToolName(
  obj: Record<string, unknown>,
  toolName?: string,
  name?: string,
  title?: string,
): string | undefined {
  const candidateTitle = typeof obj.title === 'string' ? obj.title : title
  if (candidateTitle) {
    const titleLower = candidateTitle.toLowerCase()
    if (titleLower.startsWith('loaded skill:') || titleLower === 'skill' || titleLower.startsWith('skill:')) return 'skill'
    if (titleLower.includes('subagent') || titleLower.includes('delegate') || titleLower.startsWith('task:')) return 'task'
    const inferredFamily = inferTitleToolFamily(titleLower)
    if (inferredFamily) return inferredFamily
  }

  const skillName = obj.skillName ?? obj.skill ?? obj.name
  if (typeof skillName === 'string' && skillName && skillName !== toolName && skillName !== name) return 'skill'
  if (obj.subagent_type !== undefined || obj.subagentType !== undefined || obj.task_id !== undefined || obj.taskId !== undefined || obj.childSessionId !== undefined || obj.child_session_id !== undefined) return 'task'
  if (obj.patchText !== undefined) return 'apply_patch'
  if (obj.command !== undefined || obj.script !== undefined || obj.cmd !== undefined) return 'bash'
  if (obj.url !== undefined || obj.uri !== undefined) {
    if (obj.search_query !== undefined || obj.query !== undefined) return 'websearch'
    return 'webfetch'
  }
  if (obj.pattern !== undefined || obj.query !== undefined || obj.search !== undefined) {
    if (obj.file_path !== undefined || obj.filePath !== undefined || obj.include !== undefined) return 'grep'
    return 'search'
  }
  if (obj.file_path !== undefined || obj.filePath !== undefined || obj.path !== undefined) return 'read'
  if (obj.todos !== undefined) return 'todowrite'
  if (obj.question !== undefined) return 'question'
  return undefined
}

export function inferToolName(toolName: string | undefined, title?: string, rawInput?: unknown, rawOutput?: unknown): string {
  if (toolName && toolName !== 'unknown') return toolName

  if (title) {
    const inferred = inferSemanticToolName({}, toolName, undefined, title)
    if (inferred) return inferred
  }

  const input = typeof rawInput === 'string' ? rawInput : rawInput && typeof rawInput === 'object' ? rawInput as Record<string, unknown> : null
  if (input && typeof input === 'object') {
    if (typeof input.toolName === 'string') return input.toolName
    if (typeof input.name === 'string') return input.name
    const inferred = inferSemanticToolName(input, toolName, typeof input.name === 'string' ? input.name : undefined, title)
    if (inferred) return inferred
  }
  if (typeof input === 'string') {
    try {
      return inferToolName(undefined, title, JSON.parse(input), rawOutput)
    } catch {
      if (input.includes('*** Begin Patch') || input.includes('*** Add File:') || input.includes('*** Update File:') || input.includes('*** Delete File:')) return 'apply_patch'
    }
  }

  const output = rawOutput && typeof rawOutput === 'object' ? rawOutput as Record<string, unknown> : null
  const metadata = output?.metadata && typeof output.metadata === 'object' ? output.metadata as Record<string, unknown> : null
  if (typeof metadata?.toolName === 'string') return metadata.toolName
  if (typeof metadata?.name === 'string') return metadata.name
  if (metadata) {
    const inferred = inferSemanticToolName(metadata, toolName, typeof metadata.name === 'string' ? metadata.name : undefined, title)
    if (inferred) return inferred
  }

  return toolName ?? 'unknown'
}

export function normalizeToolName(toolName: string | undefined, title?: string, rawInput?: unknown, rawOutput?: unknown): string {
  const inferred = inferToolName(toolName, title, rawInput, rawOutput)
  if (!inferred || inferred === 'unknown') return 'unknown'
  return inferred.toLowerCase().replace(/[^a-z0-9]/g, '_')
}

export const GENERIC_TOOL_LABEL = 'Tool call'

const DISPLAY_TITLES: Record<string, string> = {
  apply_patch: 'Patch',
  read: 'Read',
  write: 'Write',
  edit: 'Edit',
  glob: 'Glob',
  grep: 'Search',
  list: 'List',
  todowrite: 'Update todo list',
  membrowse: 'Browse',
  memread: 'Read memory',
  memsearch: 'Search memory',
}

export function inferDisplayTitle(toolName: string, title?: string): { displayTitle?: string; displaySubtitle?: string } {
  if (title) {
    return { displayTitle: title }
  }
  const normalized = normalizeToolName(toolName)
  if (normalized === 'unknown') {
    return { displayTitle: GENERIC_TOOL_LABEL }
  }
  const mapped = DISPLAY_TITLES[normalized]
  if (mapped) return { displayTitle: mapped }
  if (toolName && toolName !== 'unknown') return { displayTitle: toolName }
  return { displayTitle: GENERIC_TOOL_LABEL }
}

export function getCorrelationKey(toolName: string, title?: string, target?: string): string {
  const normalized = normalizeToolName(toolName, title)
  const keyParts = [normalized]
  if (target) {
    keyParts.push(target)
  } else if (title) {
    keyParts.push(title)
  }
  return keyParts.join('|')
}
