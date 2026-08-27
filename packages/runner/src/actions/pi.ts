import {
  NON_RECOVERABLE_PROVIDER_ERROR_CODE,
  type ActionResult,
  type JsonObject,
  type ParentIssueContext,
} from '../core/types.js'
import type { ServerConnection } from '../server/connection.js'
import type { TaskLogger } from '../runtime/task-log.js'
import type { PiRuntime } from '../runtime/pi/index.js'
import type { ActionHost } from './host.js'
import { isObject } from '../core/json.js'
import { resolvePrompt } from '../core/prompt.js'
import { sessionNameFromContext } from './workflow-session-name.js'
import { hasUnconfirmedCleanup, parseModelIdentifier } from '../runtime/opencode/index.js'
import type { PiRuntimeEvent, PiTurnRequest } from '../runtime/pi/index.js'
import { actionErrorMessage, fail, succeed } from './action-result.js'
import type { PromptLoaderContext } from '../core/prompt.js'
import { SkillResolver } from '../runtime/skill-resolver.js'
import { buildExecutionEnvelope } from '../runtime/execution-envelope.js'
import type { AgentExecutionDefinition } from '../core/types.js'
import { WorkflowAgentSessionReporter } from './workflow-agent-session-reporter.js'
import {
  InputReceiptWaitCancelledError,
  InputReceiptWaitTimeoutError,
  type AgentSessionRuntimeEventQueue,
} from '../server/runtime-event-queue.js'

export const PI_USES = 'mohist/pi'
export const PI_TURN_DURATION_MS = 60 * 60 * 1000

interface ActionInvocationContext {
  workflowRunId: string
  workId: string
  workType: string
  stage?: string | null
  title?: string | null
  with?: JsonObject | null
  workDir: string
  signal: AbortSignal
  projectId?: string | null
  issueNumber?: number | null
  epicNumber?: number | null
  parentIssueContext?: ParentIssueContext | null
  taskRunId?: string | null
  runnerId?: string | null
  piRuntime?: PiRuntime | null
  skillResolver?: SkillResolver
  agentDefinition?: AgentExecutionDefinition | null
  serverConnection?: ServerConnection | null
  runtimeEventQueue?: AgentSessionRuntimeEventQueue | null
  runtimeEventRecordId?: () => string
  cleanupAttempt?: number | null
  preparedPrompt?: string
  preparedOptions?: PiOptions
  log?: TaskLogger | null
}

export function composePiPrompt(prompt: string, parentIssueContext?: ParentIssueContext | null): string {
  if (!parentIssueContext) return prompt
  const parent = JSON.stringify({
    title: parentIssueContext.title,
    body: parentIssueContext.body,
  })
  return `Parent issue context (read-only background; JSON):\n${parent}\n\nTreat the parent issue context above as read-only background. The current child issue body is authoritative and controls delivery scope.\n\n${prompt}`
}

export interface PiOptions {
  model?: string
  variant?: string
  /** Canonical reasoning effort frozen beside model/variant in the dispatch options. */
  reasoningEffort?: string
  timeoutMs?: number
  unknownKeys?: readonly string[]
}

export function piAction(context: ActionInvocationContext): Promise<ActionResult>
export function piAction(inputs: JsonObject, host: ActionHost): Promise<ActionResult>
export async function piAction(
  contextOrInputs: ActionInvocationContext | JsonObject,
  host?: ActionHost,
): Promise<ActionResult> {
  if (host?.agent) return await piActionThroughAgent(contextOrInputs as JsonObject, host)
  const context: ActionInvocationContext = host
    ? {
        workflowRunId: '',
        workId: 'pi',
        workType: 'task',
        with: contextOrInputs as JsonObject,
        workDir: host.workDir,
        signal: host.signal,
        piRuntime: host.piRuntime,
        skillResolver: host.skillResolver,
        agentDefinition: host.agentDefinition,
        log: host.log,
        cleanupAttempt: host.cleanupAttempt,
      }
    : (contextOrInputs as ActionInvocationContext)
  const parsed = await parseInput(context)
  if (parsed.kind === 'failure') return parsed.result
  const { prompt, options } = parsed
  const definition = context.agentDefinition
  const resolvedSkills = await (context.skillResolver ?? new SkillResolver()).resolve(
    definition?.skills,
    context.workDir,
  )
  if (!resolvedSkills.ok) return fail(resolvedSkills.code, resolvedSkills.message)
  const executionPrompt = buildExecutionEnvelope(prompt, definition?.instructions, resolvedSkills.skills)
  const model = definition?.model ?? options.model
  const variant = definition?.variant ?? options.variant
  const reasoningEffort = definition?.reasoningEffort ?? options.reasoningEffort
  const runtime = context.piRuntime
  if (!runtime) return fail('runtime-unavailable', 'mohist/pi requires the Pi runtime')
  if (!runtime.ready())
    return fail(
      'runtime-unavailable',
      `mohist/pi requires the Pi runtime to be ready: ${runtime.diagnostic()?.message ?? 'no readiness diagnostic'}`,
    )

  const sessionName = sessionNameFromContext(context)
  const canBind = !!context.serverConnection && !!context.projectId
  let runtimeSessionId: string | null = null
  let expectedRuntime: string | null = null
  let expectedRuntimeSessionId: string | null = null
  let agentSessionId: string | null = null
  if (canBind) {
    try {
      const opened = await context.serverConnection!.openWorkflowAgentSession(
        context.projectId!,
        context.workflowRunId,
        sessionName,
        {
          workId: context.workId,
          workType: context.workType,
          stage: context.stage,
          title: context.title,
          issueNumber: context.issueNumber,
          epicNumber: context.epicNumber,
          workDir: context.workDir,
          runtime: 'pi',
        },
        context.signal,
      )
      if (opened.workDir && opened.workDir !== context.workDir)
        return fail(
          'session-workspace-mismatch',
          'Workflow AgentSession is bound to a different workspace; rerun the stage with a new task attempt before retrying',
        )
      agentSessionId = opened.sessionId
      runtimeSessionId = opened.runtimeSessionId ?? null
      expectedRuntime = opened.runtime ?? null
      expectedRuntimeSessionId = opened.runtimeSessionId ?? null
    } catch (error) {
      return fail(
        'session-binding-failed',
        `Failed to resolve the Workflow AgentSession binding: ${actionErrorMessage(error)}`,
      )
    }
  }

  if (runtimeSessionId === null || expectedRuntime !== 'pi') {
    let created: Awaited<ReturnType<PiRuntime['createSession']>>
    try {
      created = await runtime.createSession({
        target: {
          runtime: 'pi',
          runtimeSessionId: null,
          workDir: context.workDir,
        },
      })
    } catch (error) {
      return boundFailure(
        'turn-failed',
        `Pi session creation failed: ${actionErrorMessage(error)}`,
        agentSessionId,
        null,
      )
    }
    if (!created.ok)
      return boundFailure(
        runtimeErrorCode(created.error.kind, created.error.diagnostics),
        created.error.message,
        agentSessionId,
        null,
      )
    runtimeSessionId = created.value.runtimeSessionId
    if (canBind) {
      try {
        await context.serverConnection!.attachWorkflowAgentSession(
          context.projectId!,
          context.workflowRunId,
          sessionName,
          {
            runtimeSessionId,
            workDir: context.workDir,
            processPid: null,
            model: model ?? null,
            workId: context.workId,
            runtime: 'pi',
            expectedRuntime,
            expectedRuntimeSessionId,
          },
          context.signal,
        )
      } catch (error) {
        return boundFailure(
          'session-binding-failed',
          `Failed to record the Workflow AgentSession binding: ${actionErrorMessage(error)}`,
          agentSessionId,
          runtimeSessionId,
        )
      }
    }
  }

  const events: PiRuntimeEvent[] = []
  const reporter = createWorkflowReporter(
    context,
    sessionName,
    agentSessionId,
    runtimeSessionId,
    options.timeoutMs ?? PI_TURN_DURATION_MS,
  )
  if (context.cleanupAttempt && !reporter) {
    return boundFailure(
      'session-reporting-failed',
      'Workflow cleanup requires the AgentSession runtime-event queue',
      agentSessionId,
      runtimeSessionId,
      reporter,
    )
  }
  const report = async (facts: readonly PiRuntimeEvent[], signal = context.signal) => {
    if (!canBind || facts.length === 0) return
    if (reporter) {
      const input = facts.find((event) => event.type === 'session.input')
      if (input) {
        if (facts.length !== 1 || typeof input.payload.text !== 'string') {
          throw new Error('Workflow session.input must be reported by itself with a text payload')
        }
        await reporter.awaitInput(input.payload.text, input.runtimeSessionId)
        return
      }
      for (const event of facts) reporter.registerEvent(event as never)
      await reporter.settle()
      return
    }
    await context.serverConnection!.workflowAgentSessionRuntimeEvents(
      context.projectId!,
      context.workflowRunId,
      sessionName,
      {
        workId: context.workId,
        workType: context.workType,
        stage: context.stage,
        runtimeSessionId,
        runtimeEvents: facts.map((event) => ({
          id: event.id,
          type: event.type,
          payload: event.payload,
        })),
      },
      signal,
    )
  }

  try {
    await report([inputEvent(runtimeSessionId, executionPrompt, context)])
  } catch (error) {
    const receiptWaitError =
      error instanceof InputReceiptWaitTimeoutError || error instanceof InputReceiptWaitCancelledError ? error : null
    const message =
      receiptWaitError?.message ?? 'Workflow AgentSession rejected session.input; prompt was not submitted'
    return boundFailure('session-reporting-failed', message, agentSessionId, runtimeSessionId, reporter)
  }

  const request: PiTurnRequest = {
    target: { runtime: 'pi', runtimeSessionId, workDir: context.workDir },
    prompt: executionPrompt,
    durationMs: options.timeoutMs ?? PI_TURN_DURATION_MS,
    options: {
      model: model ?? null,
      variant: variant ?? null,
      reasoningEffort: reasoningEffort ?? null,
      ...(resolvedSkills.skills.length > 0 ? { skills: resolvedSkills.skills } : {}),
      unknownKeys: options.unknownKeys,
    },
  }
  let result
  try {
    result = await runtime.runTurn(request, context.signal, {
      onEvent: (event) => {
        events.push(event)
      },
    })
  } catch (error) {
    let terminalReportingFailed = false
    try {
      await reportWithTerminalSignal(report, [
        ...events,
        {
          id: `turn-failed-${context.workId}`,
          type: 'turn.failed',
          runtimeSessionId,
          workDir: context.workDir,
          payload: { status: 'failed', errorCode: 'turn-failed' },
        },
        activityEvent(runtimeSessionId, 'idle', context),
      ])
    } catch {
      terminalReportingFailed = true
    }
    const message = actionErrorMessage(error)
    return fail(
      'turn-failed',
      terminalReportingFailed
        ? `${message}; Session terminal reporting failed and terminal state was not accepted`
        : message,
      { exitCode: 1, turnFact: boundTurnFact(null, agentSessionId, reporter, runtimeSessionId) },
    )
  }

  const unknownOutcome = !result.ok && hasUnconfirmedCleanup(result.diagnostics ?? result.error.diagnostics ?? [])
  const finalText = result.ok ? result.value.facts.finalAssistantText : null
  const runtimeCode = result.ok ? null : runtimeErrorCode(result.error.kind, result.error.diagnostics)
  const finalFacts = [...events]
  const submittedFailure =
    !result.ok &&
    (result.error.kind === 'deadline-exceeded' ||
      result.error.kind === 'interrupted' ||
      result.error.kind === 'turn-failed')
  if (result.ok || submittedFailure) {
    if (!result.ok)
      finalFacts.push({
        id: `turn-failed-${context.workId}`,
        type: 'turn.failed',
        runtimeSessionId,
        workDir: context.workDir,
        payload: {
          status: unknownOutcome ? 'unknown' : 'failed',
          errorCode: runtimeCode ?? 'turn-failed',
          message: result.error.message,
        },
      })
    finalFacts.push(activityEvent(runtimeSessionId, 'idle', context))
  }
  try {
    await reportWithTerminalSignal(report, finalFacts)
  } catch {
    if (result.ok)
      return fail('session-reporting-failed', 'Workflow AgentSession did not accept the final Pi turn facts', {
        exitCode: 1,
        turnFact: boundTurnFact(null, agentSessionId, reporter, runtimeSessionId),
      })
    return fail(
      runtimeCode ?? 'turn-failed',
      `${result.error.message}; Session terminal reporting failed and terminal state was not accepted`,
      { exitCode: 1, turnFact: boundTurnFact(null, agentSessionId, reporter, runtimeSessionId) },
    )
  }
  if (!result.ok)
    return fail(runtimeCode ?? 'turn-failed', result.error.message, {
      exitCode: 1,
      outcome: unknownOutcome ? 'unknown' : undefined,
      turnFact: boundTurnFact(null, agentSessionId, reporter, runtimeSessionId),
    })
  return succeed(null, {
    exitCode: 0,
    turnFact: boundTurnFact(finalText, agentSessionId, reporter, runtimeSessionId),
  })
}

function boundFailure(
  code: string,
  message: string,
  agentSessionId: string | null,
  runtimeSessionId: string | null,
  reporter: WorkflowAgentSessionReporter | null = null,
): ActionResult {
  return fail(code, message, {
    exitCode: 1,
    turnFact:
      runtimeSessionId === null
        ? { finalAssistantText: null }
        : boundTurnFact(null, agentSessionId, reporter, runtimeSessionId),
  })
}

function boundTurnFact(
  finalAssistantText: string | null,
  agentSessionId: string | null,
  reporter: WorkflowAgentSessionReporter | null,
  runtimeSessionId: string,
) {
  return {
    finalAssistantText,
    ...(agentSessionId
      ? {
          agentBinding: {
            agentSessionId,
            agentTurnId: reporter?.getAgentTurnId() ?? null,
            runtime: 'pi' as const,
            runtimeSessionId,
          },
        }
      : {}),
  }
}

function buildPromptLoaderContext(
  context: Pick<ActionInvocationContext, 'workDir' | 'workId' | 'title' | 'stage'>,
): PromptLoaderContext {
  return {
    with: {},
    workDir: context.workDir,
    workId: context.workId,
    title: context.title ?? null,
    stage: context.stage ?? null,
  }
}

async function reportWithTerminalSignal(
  report: (facts: readonly PiRuntimeEvent[], signal?: AbortSignal) => Promise<void>,
  facts: readonly PiRuntimeEvent[],
): Promise<void> {
  const controller = new AbortController()
  const timeout = setTimeout(() => controller.abort(), 30_000)
  try {
    await report(facts, controller.signal)
  } finally {
    clearTimeout(timeout)
  }
}

async function parseInput(
  context: ActionInvocationContext,
): Promise<{ kind: 'ok'; prompt: string; options: PiOptions } | { kind: 'failure'; result: ActionResult }> {
  if (context.preparedPrompt !== undefined) {
    return {
      kind: 'ok',
      prompt: context.preparedPrompt,
      options: context.preparedOptions ?? {},
    }
  }
  const input = context.with ?? {}
  const allowed = new Set(['prompt', 'session', 'options', 'timeout', 'working-directory'])
  const invalid = Object.keys(input).find((key) => !allowed.has(key))
  if (invalid)
    return {
      kind: 'failure',
      result: fail('invalid-input', `mohist/pi does not accept top-level input '${invalid}'`),
    }
  const rawSession = input.session
  if (rawSession !== undefined && rawSession !== null && typeof rawSession !== 'string')
    return {
      kind: 'failure',
      result: fail('invalid-input', "mohist/pi 'session' must be a string when present"),
    }
  if (rawSession !== undefined && rawSession !== null && !rawSession.trim())
    return {
      kind: 'failure',
      result: fail('invalid-input', "mohist/pi 'session' must not be empty"),
    }
  let prompt: string | undefined
  try {
    prompt = await resolvePrompt(input.prompt, buildPromptLoaderContext(context))
  } catch (error) {
    return {
      kind: 'failure',
      result: fail('invalid-input', actionErrorMessage(error)),
    }
  }
  if (!prompt?.trim())
    return {
      kind: 'failure',
      result: fail('invalid-input', "mohist/pi requires 'prompt' that resolves to non-empty text"),
    }
  const timeout = input.timeout
  if (timeout !== undefined && (typeof timeout !== 'number' || !Number.isFinite(timeout) || timeout <= 0)) {
    return {
      kind: 'failure',
      result: fail('invalid-input', "mohist/pi 'timeout' must be a positive finite number when present"),
    }
  }
  const rawOptions = input.options
  if (rawOptions !== undefined && rawOptions !== null && !isObject(rawOptions))
    return {
      kind: 'failure',
      result: fail('invalid-input', "mohist/pi 'options' must be an object when present"),
    }
  const options: PiOptions = {}
  if (typeof timeout === 'number') options.timeoutMs = timeout
  const record = (rawOptions ?? {}) as Record<string, unknown>
  for (const key of ['model', 'variant', 'reasoningEffort'] as const) {
    const value = record[key]
    if (value === undefined || value === null) continue
    if (typeof value !== 'string')
      return {
        kind: 'failure',
        result: fail('invalid-input', `mohist/pi 'options.${key}' must be a string when present`),
      }
    if (key === 'model') {
      const parsed = parseModelIdentifier(value)
      if (parsed.kind === 'failure')
        return {
          kind: 'failure',
          result: fail('invalid-input', `mohist/pi ${parsed.message}`),
        }
    }
    options[key] = value
  }
  const unknownKeys = Object.keys(record).filter(
    (key) => key !== 'model' && key !== 'variant' && key !== 'reasoningEffort',
  )
  if (unknownKeys.length > 0) options.unknownKeys = unknownKeys
  return {
    kind: 'ok',
    prompt: composePiPrompt(prompt, context.parentIssueContext),
    options,
  }
}

async function piActionThroughAgent(inputs: JsonObject, host: ActionHost): Promise<ActionResult> {
  const parsed = await parseInput({
    workflowRunId: '',
    workId: 'pi',
    workType: 'task',
    with: inputs,
    workDir: host.workDir,
    signal: host.signal,
    skillResolver: host.skillResolver,
    agentDefinition: host.agentDefinition,
  })
  if (parsed.kind === 'failure') return parsed.result
  const session = typeof inputs.session === 'string' ? inputs.session : undefined
  return await host.agent!.turn({
    prompt: parsed.prompt,
    session,
    options: parsed.options,
    deadlineMs: parsed.options.timeoutMs,
  })
}

function createWorkflowReporter(
  context: ActionInvocationContext,
  sessionName: string,
  agentSessionId: string | null,
  runtimeSessionId: string | null,
  inputReceiptBudgetMs?: number,
): WorkflowAgentSessionReporter | null {
  if (!context.projectId || !context.runtimeEventQueue || !context.runtimeEventRecordId) return null
  if (!context.taskRunId || !context.runnerId || !agentSessionId || !runtimeSessionId) return null
  return new WorkflowAgentSessionReporter({
    outbox: context.runtimeEventQueue,
    projectId: context.projectId,
    workflowRunId: context.workflowRunId,
    sessionName,
    workMetadata: {
      workId: context.workId,
      taskRunId: context.taskRunId,
      workType: context.workType,
      stage: context.stage ?? null,
      runnerId: context.runnerId,
      agentSessionId,
    },
    runtime: 'pi',
    randomId: context.runtimeEventRecordId,
    inputReceiptBudgetMs,
    signal: context.signal,
    cleanupAttempt: context.cleanupAttempt,
  })
}

function inputEvent(runtimeSessionId: string, prompt: string, context: ActionInvocationContext): PiRuntimeEvent {
  return {
    id: `session-input-${context.workId}`,
    type: 'session.input',
    runtimeSessionId,
    workDir: context.workDir,
    payload: {
      text: prompt,
      kind: context.workType,
      source: 'workflow',
      role: 'user',
      runtimeSessionId,
    },
  }
}

function activityEvent(
  runtimeSessionId: string,
  activity: 'idle' | 'unknown',
  context: ActionInvocationContext,
): PiRuntimeEvent {
  return {
    id: `session-activity-${context.workId}-${activity}`,
    type: 'session.activity',
    runtimeSessionId,
    workDir: context.workDir,
    payload: { activity, observedAt: new Date().toISOString() },
  }
}

function runtimeErrorCode(kind: string, diagnostics: readonly { code: string }[] = []): string {
  if (diagnostics.some((item) => item.code === NON_RECOVERABLE_PROVIDER_ERROR_CODE))
    return NON_RECOVERABLE_PROVIDER_ERROR_CODE
  if (kind === 'deadline-exceeded') return 'timeout'
  if (kind === 'missing-session') return 'runtime-session-missing'
  return kind
}

function runtimeFailure(
  kind: string,
  message: string,
  diagnostics: readonly { code: string; message: string }[],
): ActionResult {
  const code = runtimeErrorCode(kind, diagnostics)
  const hint = kind === 'missing-session' ? ' Reset the Workflow Session before retrying.' : ''
  return fail(code, `${message}${hint}`, {
    exitCode: 1,
    turnFact: { finalAssistantText: null },
  })
}
