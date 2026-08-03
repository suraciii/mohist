import type { TimelineReference, TimelineToolFact } from './types'

export interface TimelineDomainAction {
  verb: string
  object: string
  reference?: TimelineReference
  source: 'shell' | 'tool'
}

type ActionDefinition = {
  argv: string[]
  verb: string
  requiresIssue: boolean
}

const ACTIONS: ActionDefinition[] = [
  { argv: ['issue', 'comment', 'create'], verb: '评论了', requiresIssue: true },
  { argv: ['issue', 'start'], verb: '启动了', requiresIssue: true },
  { argv: ['run', 'approve'], verb: '批准了', requiresIssue: false },
  { argv: ['run', 'reject'], verb: '拒绝了', requiresIssue: false },
  { argv: ['run', 'retry'], verb: '重试了', requiresIssue: false },
  { argv: ['run', 'rerun'], verb: '重新运行了', requiresIssue: false },
  { argv: ['run', 'pause'], verb: '暂停了', requiresIssue: false },
  { argv: ['run', 'resume'], verb: '恢复了', requiresIssue: false },
  { argv: ['run', 'stop'], verb: '停止了', requiresIssue: false },
]

const TOOL_ACTIONS = new Map<string, ActionDefinition>(
  ACTIONS.map((action) => [`mohist_${action.argv.join('_')}`, action]),
)

function splitCommand(command: string): string[] | undefined {
  if (!command.trim() || /[|;&<>`$\r\n]/.test(command)) return undefined

  const words: string[] = []
  let word = ''
  let quote: '"' | "'" | undefined
  let escaped = false

  for (const character of command) {
    if (escaped) {
      word += character
      escaped = false
      continue
    }
    if (character === '\\') {
      escaped = true
      continue
    }
    if (quote) {
      if (character === quote) quote = undefined
      else word += character
      continue
    }
    if (character === '"' || character === "'") {
      quote = character
      continue
    }
    if (/\s/.test(character)) {
      if (word) {
        words.push(word)
        word = ''
      }
      continue
    }
    word += character
  }

  if (escaped || quote) return undefined
  if (word) words.push(word)
  return words.length > 0 ? words : undefined
}

function extractMoArgv(command: string): string[] | undefined {
  const argv = splitCommand(command)
  if (!argv) return undefined

  if (argv[0] === 'mo') return argv.slice(1)
  if ((argv[0] === 'bash' || argv[0] === 'sh') && (argv[1] === '-c' || argv[1] === '-lc') && argv.length === 3) {
    const nested = splitCommand(argv[2])
    return nested?.[0] === 'mo' ? nested.slice(1) : undefined
  }
  return undefined
}

function findIssueNumber(argv: string[]): number | undefined {
  for (const argument of argv) {
    if (/^\d+$/.test(argument)) {
      const number = Number(argument)
      if (Number.isSafeInteger(number) && number > 0) return number
    }
  }
  return undefined
}

function extractIssueNumber(input: unknown): number | undefined {
  if (!input || typeof input !== 'object' || Array.isArray(input)) return undefined
  const record = input as Record<string, unknown>
  for (const key of ['issueNumber', 'issue', 'number']) {
    const value = record[key]
    if (typeof value === 'number' && Number.isSafeInteger(value) && value > 0) return value
    if (typeof value === 'string' && /^\d+$/.test(value)) return Number(value)
  }
  return undefined
}

function extractWorkflowRunId(input: unknown): string | undefined {
  if (!input || typeof input !== 'object' || Array.isArray(input)) return undefined
  const record = input as Record<string, unknown>
  for (const key of ['workflowRunId', 'runId']) {
    const value = record[key]
    if (typeof value === 'string' && value.trim()) return value
  }
  return undefined
}

function actionForArgv(argv: string[]): ActionDefinition | undefined {
  return ACTIONS.find(action => action.argv.every((segment, index) => argv[index] === segment))
}

function toAction(
  definition: ActionDefinition,
  issueNumber: number | undefined,
  workflowRunId: string | undefined,
  source: TimelineDomainAction['source'],
): TimelineDomainAction | undefined {
  if (definition.requiresIssue && !issueNumber) return undefined
  if (issueNumber) {
    return {
      verb: definition.verb,
      object: `Issue #${issueNumber}`,
      reference: { kind: 'issue', label: `Issue #${issueNumber}`, issueNumber },
      source,
    }
  }
  return {
    verb: definition.verb,
    object: 'Workflow',
    reference: workflowRunId ? { kind: 'workflow', label: 'Workflow', workflowRunId } : undefined,
    source,
  }
}

export function detectShellDomainAction(command: string): TimelineDomainAction | undefined {
  const argv = extractMoArgv(command)
  if (!argv) return undefined
  const definition = actionForArgv(argv)
  return definition ? toAction(definition, findIssueNumber(argv), undefined, 'shell') : undefined
}

export function detectToolDomainAction(tool: TimelineToolFact): TimelineDomainAction | undefined {
  const normalizedName = (tool.normalizedName ?? tool.name).toLowerCase().replace(/[.\-/\s]+/g, '_')
  const definition = TOOL_ACTIONS.get(normalizedName)
  return definition
    ? toAction(definition, extractIssueNumber(tool.input), extractWorkflowRunId(tool.input), 'tool')
    : undefined
}
