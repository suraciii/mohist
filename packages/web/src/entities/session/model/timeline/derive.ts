import { detectShellDomainAction, detectToolDomainAction } from './domain-actions'
import type {
  TimelineDetail,
  TimelineFact,
  TimelineFileChange,
  TimelineItem,
  TimelineRenderClass,
  TimelineSalience,
  TimelineToolFact,
} from './types'

const FILE_READ_TOOLS = new Set(['read', 'read_file', 'glob', 'grep', 'search', 'list', 'membrowse', 'memread', 'memsearch', 'search_files'])
const FILE_EDIT_TOOLS = new Set(['edit', 'write', 'write_file', 'apply_patch', 'patch'])
const SHELL_TOOLS = new Set(['bash', 'shell', 'terminal', 'exec'])
const PLAN_TOOLS = new Set(['todo', 'todowrite', 'todo_write', 'plan', 'plan_update'])

function salienceFor(renderClass: TimelineRenderClass): TimelineSalience {
  switch (renderClass) {
    case 'error':
      return 'critical'
    case 'domain-action':
      return 'high'
    case 'input':
    case 'message':
    case 'plan':
    case 'boundary':
      return 'normal'
    case 'unknown':
      return 'normal'
    case 'file-edit':
    case 'shell':
      return 'medium'
    case 'file-read':
    case 'tool':
    case 'reasoning':
      return 'low'
    case 'status':
    case 'suppressed':
      return 'quiet'
  }
}

function toolName(tool: TimelineToolFact): string {
  return (tool.normalizedName ?? tool.name).toLowerCase()
}

function isTerminalTool(tool: TimelineToolFact): boolean {
  return tool.exitCode !== undefined || tool.status === 'completed' || tool.status === 'failed' || tool.status === 'cancelled' || tool.status === 'timeout'
}

function isFailedTool(tool: TimelineToolFact): boolean {
  return tool.status === 'failed' || tool.status === 'cancelled' || tool.status === 'timeout' || (tool.exitCode !== undefined && tool.exitCode !== 0)
}

function stringField(value: unknown, keys: string[]): string | undefined {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return undefined
  const record = value as Record<string, unknown>
  for (const key of keys) {
    const candidate = record[key]
    if (typeof candidate === 'string' && candidate.trim()) return candidate
  }
  return undefined
}

function toolTarget(tool: TimelineToolFact): string | undefined {
  return tool.target
    ?? stringField(tool.input, ['path', 'filePath', 'file', 'query', 'command'])
    ?? tool.title
}

function toolCommand(tool: TimelineToolFact): string | undefined {
  return tool.command ?? stringField(tool.input, ['command', 'cmd', 'script']) ?? (typeof tool.input === 'string' ? tool.input : undefined)
}

function formatFileChanges(changedFiles: TimelineFileChange[] | undefined): string {
  if (!changedFiles || changedFiles.length === 0) return ''
  const additions = changedFiles.reduce((total, file) => total + (file.additions ?? 0), 0)
  const deletions = changedFiles.reduce((total, file) => total + (file.deletions ?? 0), 0)
  if (additions === 0 && deletions === 0) return ''
  return ` (+${additions}/-${deletions})`
}

function makeDetail(fact: TimelineFact): TimelineDetail {
  if (!fact.tool) return { raw: fact.raw, error: fact.error?.message }
  return {
    input: fact.tool.input,
    output: fact.tool.output,
    diff: fact.tool.changedFiles,
    error: fact.error?.message,
    raw: fact.raw,
  }
}

function classifyTool(fact: TimelineFact): Omit<TimelineItem, 'id' | 'sourceIds' | 'occurredAt'> {
  const tool = fact.tool
  if (!tool) {
    return {
      renderClass: 'tool',
      summary: '执行了工具',
      salience: 'low',
      detail: makeDetail(fact),
      isTerminal: false,
    }
  }

  const command = toolCommand(tool)
  const domainAction = command ? detectShellDomainAction(command) : detectToolDomainAction(tool)
  let renderClass: TimelineRenderClass
  let summary: string
  let groupKey: string | undefined
  let reference: TimelineItem['reference']

  if (domainAction) {
    renderClass = 'domain-action'
    summary = `${domainAction.verb} ${domainAction.object}`
    reference = domainAction.reference
  } else if (FILE_READ_TOOLS.has(toolName(tool))) {
    renderClass = 'file-read'
    summary = `读取了 ${toolTarget(tool) ?? '文件'}`
    groupKey = fact.groupKey ?? 'file-read'
  } else if (FILE_EDIT_TOOLS.has(toolName(tool))) {
    renderClass = 'file-edit'
    summary = `编辑了 ${toolTarget(tool) ?? '文件'}${formatFileChanges(tool.changedFiles)}`
  } else if (SHELL_TOOLS.has(toolName(tool))) {
    renderClass = 'shell'
    summary = `运行了 ${command ?? tool.title ?? '命令'}`
    groupKey = fact.groupKey ?? 'shell'
  } else if (PLAN_TOOLS.has(toolName(tool))) {
    renderClass = 'plan'
    summary = '更新了计划'
  } else {
    renderClass = 'tool'
    summary = `执行了 ${tool.title ?? tool.name}`
    groupKey = fact.groupKey ?? `tool:${toolName(tool)}`
  }

  if (isFailedTool(tool)) {
    return {
      renderClass: 'error',
      summary: `${summary} → 失败`,
      salience: 'critical',
      detail: makeDetail(fact),
      reference,
      isTerminal: true,
    }
  }

  if (tool.status === 'completed' || tool.exitCode === 0) summary = `${summary} → 通过`
  return {
    renderClass,
    summary,
    salience: salienceFor(renderClass),
    detail: makeDetail(fact),
    reference,
    groupKey,
    isTerminal: isTerminalTool(tool),
  }
}

function classifyFact(fact: TimelineFact): Omit<TimelineItem, 'id' | 'sourceIds' | 'occurredAt'> {
  switch (fact.kind) {
    case 'input': {
      const outcome = fact.input?.acceptance ? ` → ${fact.input.acceptance}` : ''
      return {
        renderClass: 'input',
        summary: `输入了 ${fact.input?.text ?? fact.text ?? '消息'}${outcome}`,
        salience: 'normal',
        detail: makeDetail(fact),
        isTerminal: true,
      }
    }
    case 'message':
      return {
        renderClass: 'message',
        summary: fact.text ? `回复了 ${fact.text}` : '回复了消息',
        salience: 'normal',
        detail: makeDetail(fact),
        isTerminal: false,
        isStreaming: true,
      }
    case 'reasoning':
      return {
        renderClass: 'reasoning',
        summary: '进行了思考',
        salience: 'low',
        detail: makeDetail(fact),
        isTerminal: false,
        isStreaming: true,
      }
    case 'tool':
      return classifyTool(fact)
    case 'plan':
      return {
        renderClass: 'plan',
        summary: fact.text ?? '更新了计划',
        salience: 'normal',
        detail: makeDetail(fact),
        isTerminal: true,
      }
    case 'status':
      return {
        renderClass: 'status',
        summary: fact.status?.label ?? fact.status?.state ?? fact.text ?? '状态已更新',
        salience: 'quiet',
        detail: makeDetail(fact),
        isTerminal: true,
      }
    case 'boundary':
      return {
        renderClass: 'boundary',
        summary: fact.boundary?.kind === 'reset' ? '上下文已重置' : '上下文已压缩',
        salience: 'normal',
        detail: makeDetail(fact),
        isTerminal: true,
      }
    case 'error':
      return {
        renderClass: 'error',
        summary: fact.error?.message ?? fact.text ?? '执行失败',
        salience: 'critical',
        detail: makeDetail(fact),
        isTerminal: true,
      }
    case 'unknown':
      return {
        renderClass: 'unknown',
        summary: fact.text ?? '未知运行事件',
        salience: 'normal',
        detail: makeDetail(fact),
        isTerminal: true,
      }
    case 'suppressed':
      return {
        renderClass: 'suppressed',
        summary: fact.text ?? '已省略的活动',
        salience: 'quiet',
        detail: makeDetail(fact),
        isTerminal: true,
      }
  }
}

function hasToolDescriptor(tool: TimelineToolFact | undefined): boolean {
  return tool !== undefined && (toolCommand(tool) !== undefined || toolTarget(tool) !== undefined || tool.changedFiles !== undefined)
}

function withoutOutcome(summary: string): string {
  return summary.replace(/ → (通过|失败)$/, '')
}

function outcomeFrom(summary: string): string {
  const outcome = summary.match(/( → (?:通过|失败))$/)
  return outcome?.[1] ?? ''
}

function mergeItems(existing: TimelineItem, fact: TimelineFact, replacement: Omit<TimelineItem, 'id' | 'sourceIds' | 'occurredAt'>): TimelineItem {
  if (existing.isTerminal) return existing
  const detail = replacement.detail
    ? {
        ...existing.detail,
        ...replacement.detail,
        input: replacement.detail.input ?? existing.detail?.input,
        output: replacement.detail.output ?? existing.detail?.output,
        diff: replacement.detail.diff ?? existing.detail?.diff,
        error: replacement.detail.error ?? existing.detail?.error,
      }
    : existing.detail
  const keepSummary = !hasToolDescriptor(fact.tool)
    && (existing.renderClass === replacement.renderClass || replacement.renderClass === 'error')
  return {
    ...replacement,
    id: existing.id,
    sourceIds: [...existing.sourceIds, fact.sourceId],
    occurredAt: existing.occurredAt,
    detail,
    summary: keepSummary ? `${withoutOutcome(existing.summary)}${outcomeFrom(replacement.summary)}` : replacement.summary,
    isStreaming: replacement.isStreaming && !replacement.isTerminal,
  }
}

function streamKey(fact: TimelineFact): string | undefined {
  if ((fact.kind !== 'message' && fact.kind !== 'reasoning') || !fact.correlationId) return undefined
  return `${fact.kind}:${fact.correlationId}`
}

function mergeStream(existing: TimelineItem, fact: TimelineFact): TimelineItem {
  const text = fact.text ?? ''
  const prefix = existing.renderClass === 'message' ? '回复了 ' : '思考了 '
  const previous = existing.summary.startsWith(prefix) ? existing.summary.slice(prefix.length) : ''
  return {
    ...existing,
    sourceIds: [...existing.sourceIds, fact.sourceId],
    summary: `${prefix}${previous}${text}`,
    detail: { ...existing.detail, raw: fact.raw },
  }
}

export function deriveTimelineItems(facts: TimelineFact[]): TimelineItem[] {
  const sortedFacts = [...facts].sort((left, right) => left.order - right.order || left.sourceId.localeCompare(right.sourceId))
  const items: TimelineItem[] = []
  const itemIndexByToolCall = new Map<string, number>()
  const itemIndexByStream = new Map<string, number>()

  for (const fact of sortedFacts) {
    const key = streamKey(fact)
    if (key) {
      const existingIndex = itemIndexByStream.get(key)
      if (existingIndex !== undefined) {
        const existing = items[existingIndex]
        if (existing?.isStreaming) {
          items[existingIndex] = mergeStream(existing, fact)
          continue
        }
      }
    } else {
      for (const index of itemIndexByStream.values()) {
        const item = items[index]
        if (item?.isStreaming) items[index] = { ...item, isStreaming: false, isTerminal: true }
      }
      itemIndexByStream.clear()
    }

    const classified = classifyFact(fact)
    const toolCallId = fact.tool?.callId
    if (toolCallId) {
      const existingIndex = itemIndexByToolCall.get(toolCallId)
      if (existingIndex !== undefined) {
        const existing = items[existingIndex]
        if (existing) items[existingIndex] = mergeItems(existing, fact, classified)
        continue
      }
    }

    const item: TimelineItem = {
      id: toolCallId ?? fact.sourceId,
      sourceIds: [fact.sourceId],
      occurredAt: fact.occurredAt,
      ...classified,
    }
    items.push(item)
    if (toolCallId) itemIndexByToolCall.set(toolCallId, items.length - 1)
    if (key) itemIndexByStream.set(key, items.length - 1)
  }

  return items
}
