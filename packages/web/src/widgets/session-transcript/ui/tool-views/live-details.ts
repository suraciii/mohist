import { asPayloadRecord, asRecord, getNumber, getString, truncatePreview } from '../../model/transcript-payload'

export function buildLiveToolDetails(
  normalizedName: string,
  rawInput: unknown,
  rawOutput: unknown,
  metadata?: Record<string, unknown>,
  error?: string,
): Record<string, unknown> | undefined {
  const input = asPayloadRecord(rawInput)
  const output = asPayloadRecord(rawOutput)
  const lower = normalizedName.toLowerCase()

  if (lower === 'bash' || lower === 'shell' || lower === 'exec' || lower === 'command') {
    const details: Record<string, unknown> = { family: 'execution' }
    const command = getString(input?.command ?? input?.script ?? input?.cmd)
    const cwd = getString(input?.cwd ?? input?.workdir ?? input?.workingDir)
    const timeout = getNumber(input?.timeout)
    const exitCode = getNumber(output?.exitCode ?? output?.exit_code ?? output?.code)
    const outputPreview = getString(output?.stdout ?? output?.output)
    if (command) details.command = command
    if (cwd) details.cwd = cwd
    if (timeout !== undefined) details.timeout = timeout
    if (exitCode !== undefined) details.exitCode = exitCode
    if (outputPreview) details.outputPreview = truncatePreview(outputPreview)
    else if (typeof rawOutput === 'string' && rawOutput) details.outputPreview = truncatePreview(rawOutput)
    if (error) details.completionStatus = 'failed'
    else if (rawOutput !== undefined) details.completionStatus = 'completed'
    return details
  }

  if (lower === 'task') {
    const details: Record<string, unknown> = { family: 'delegation' }
    const description = getString(input?.description ?? input?.prompt ?? input?.task ?? input?.command ?? metadata?.description)
    const subagentType = getString(input?.subagent_type ?? input?.agentType ?? input?.type ?? metadata?.subagentType)
    const subagentName = getString(input?.subagent_name ?? input?.agentName ?? input?.name)
    const taskId = getString(input?.task_id ?? input?.taskId)
    const childSessionId = getString(metadata?.childSessionId ?? metadata?.sessionId ?? metadata?.child_session_id)
    if (description) details.description = description
    if (subagentType) details.subagentType = subagentType
    if (subagentName) details.subagentName = subagentName
    if (taskId) details.taskId = taskId
    if (childSessionId) details.childSessionId = childSessionId
    return details
  }

  if (lower === 'skill') {
    const details: Record<string, unknown> = { family: 'skill' }
    const title = getString(metadata?.title)
    const skillNameFromTitle = title?.match(/(?:loaded skill:?\s*)(.+)/i)?.[1]?.trim()
    const skillName = skillNameFromTitle
      ?? getString(input?.name ?? input?.skillName ?? input?.skill)
      ?? getString(metadata?.skillName ?? metadata?.name)
    if (skillName) details.skillName = skillName
    return details
  }

  if (lower === 'question' || lower === 'webfetch' || lower === 'websearch') {
    const details: Record<string, unknown> = { family: 'interaction' }
    const url = getString(input?.url ?? input?.uri)
    const query = getString(input?.query ?? input?.search_query ?? input?.searchQuery ?? input?.search ?? input?.question ?? input?.text)
    const textPreview = getString(output?.content ?? output?.text ?? output?.summary)
    const answers = Array.isArray(output?.answers) ? output.answers : undefined
    if (url) details.url = url
    if (query) details.query = query
    if (answers) details.answerCount = answers.length
    if (textPreview) details.resultPreview = textPreview.slice(0, 300)
    else if (typeof rawOutput === 'string' && rawOutput) details.resultPreview = rawOutput.slice(0, 300)
    return details
  }

  if (lower === 'todowrite' || lower === 'todo') {
    const todos = Array.isArray(input?.todos) ? input.todos : []
    const details: Record<string, unknown> = {
      family: 'planning',
      totalCount: todos.length,
    }
    const byStatus: Record<string, number> = {}
    for (const todo of todos) {
      const status = asRecord(todo)?.status
      if (typeof status === 'string' && status) {
        byStatus[status] = (byStatus[status] ?? 0) + 1
      }
    }
    if (Object.keys(byStatus).length > 0) details.statusCounts = byStatus
    return details
  }

  return undefined
}