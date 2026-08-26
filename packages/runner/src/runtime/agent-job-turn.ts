import { errorMessage } from '../core/errors.js'
import type { JsonObject, DispatchWorkItem, WorkItemResult } from '../core/types.js'
import type {
  RuntimeResult,
  RuntimeTurnEvent,
  RuntimeTurnObserver,
  RuntimeTurnRequest,
  RuntimeTurnResult,
  RuntimeFilePart,
} from './opencode/index.js'
import type { PiRuntimeEvent, PiResult, PiTurnObserver, PiTurnRequest, PiTurnResult } from './pi/index.js'
import { callFollowup, resolveAccessor, type CommandRuntimeHandle } from '../server/command-runtime.js'
import type { ServerConnection } from '../server/connection.js'
import { boundedWait } from './bounded-wait.js'
import type { ResolvedSkill } from './skill-resolver.js'
import { runnerLogger } from '../system/logger.js'
import type { DeliveredAttachment } from './attachment-delivery.js'
import type { ManagerExecutionBoundary } from './manager-execution-boundary.js'
import { ReplyActionObservationTracker, ReplyGuardCoordinator, type ReplyGuardAdvisoryResult } from './reply-guard.js'
import type {
  AgentJobExecutorOptions,
  AgentJobRuntimeAccessors,
  BindingResolution,
  ParsedModel,
} from './agent-job-executor.js'
import { mapOpenCodeErrorKind, mapPiErrorKind } from './error-kind-mapping.js'

const log = runnerLogger.child('job')

const DEFAULT_MODEL_RETRY_INITIAL_DELAY_MS = 1_000
const DEFAULT_MODEL_RETRY_MAX_DELAY_MS = 30_000
const MODEL_UNAVAILABLE_CODES = new Set(['model-unavailable', 'model-not-found', 'model-not-available'])

export interface AgentJobTurnDeps {
  readonly connection: ServerConnection
  readonly runtimes: AgentJobRuntimeAccessors
  readonly options: AgentJobExecutorOptions
  readonly managerExecution?: ManagerExecutionBoundary | null
}

export async function executeOpenCodeTurn(
  deps: AgentJobTurnDeps,
  work: DispatchWorkItem,
  signal: AbortSignal,
  payload: JsonObject | null,
  composed: string,
  model: ParsedModel,
  modelInput: string | null,
  variant: string | null,
  reasoningEffort: string | null,
  workDir: string,
  binding: BindingResolution,
  skills: readonly ResolvedSkill[],
  attachments: readonly DeliveredAttachment[],
  managerExecution: ManagerExecutionBoundary | null = deps.managerExecution ?? null,
): Promise<WorkItemResult> {
  const boundResult = (result: WorkItemResult, runtimeSessionId = binding.runtimeSessionId) =>
    withAgentBinding(result, work, binding, 'opencode', runtimeSessionId)
  const runtime = resolveAccessor(deps.runtimes.openCode)
  if (!runtime) {
    return boundResult(
      failureResult(
        'runtime-unavailable',
        'AgentJob requires the OpenCode runtime; the runner has not yet established the runtime or it is rebuilding',
        'opencode',
      ),
    )
  }
  if (!runtime.ready()) {
    const diagnostic = runtime.diagnostic()
    return boundResult(
      failureResult(
        'runtime-unavailable',
        `AgentJob requires the OpenCode runtime to be ready: ${diagnostic?.message ?? 'no readiness diagnostic'}`,
        'opencode',
      ),
    )
  }

  const selected = binding.runtimeSessionId

  const fileParts = attachments
    .filter(
      (entry): entry is Extract<DeliveredAttachment, { status: 'delivered' }> =>
        entry.status === 'delivered' && entry.filePart !== null,
    )
    .map((entry) => entry.filePart as RuntimeFilePart)

  const turnRequest: RuntimeTurnRequest = {
    target: {
      runtime: 'opencode',
      runtimeSessionId: selected,
      workDir,
    },
    prompt: composed,
    ...(fileParts.length > 0 ? { fileParts } : {}),
    options: {
      model: model.kind === 'ok' ? { providerID: model.value.providerID, modelID: model.value.modelID } : null,
      variant: variant ?? null,
      reasoningEffort: reasoningEffort ?? null,
      ...(skills.length > 0 ? { skills } : {}),
      unknownKeys: collectUnknownKeys(payload),
    },
  }

  const observation = new ReplyActionObservationTracker()
  const eventSink = createAgentSessionEventSink(deps.connection, work, signal, binding.agentSessionId, observation)
  const runtimeHandle: CommandRuntimeHandle = { kind: 'opencode', runtime }
  // Issue-512 T-001: when the coordinator durably recorded the
  // initial input on the AgentSession before dispatch, the runner
  // must NOT re-publish a `session.input` runtime event. The
  // durable input identity is owned by the Session aggregate; the
  // dispatch only carries the correlation ids so the runner knows
  // the initial input is already accepted.
  const skipInitialInput = Boolean(work.initialInputId && work.initialTurnId)
  let attachedRuntimeSessionId: string | null = null
  let attachedInputPublished = false
  const observer: RuntimeTurnObserver = {
    onSessionReady: async (session) => {
      attachedRuntimeSessionId = session.runtimeSessionId
      await eventSink.attachSession(session.runtimeSessionId, session.workDir, modelInput)
      if (!skipInitialInput && !attachedInputPublished) {
        attachedInputPublished = true
        await eventSink.publishSessionInput(composed, session.runtimeSessionId)
      }
    },
    onEvent: (event) => {
      eventSink.observeEvent(
        managerExecution
          ? {
              ...event,
              payload: managerExecution.redact(event.payload) as Record<string, unknown>,
            }
          : event,
      )
    },
  }

  let result: RuntimeResult<RuntimeTurnResult>
  try {
    result = await runWithModelRetry(deps.options, work, modelInput, variant, signal, () =>
      runtime.runTurn(
        {
          ...turnRequest,
          target: {
            ...turnRequest.target,
            runtimeSessionId: attachedRuntimeSessionId ?? selected,
          },
        },
        signal,
        observer,
      ),
    )
  } catch (error) {
    const message = errorMessage(error)
    const originalResult = failureResult(
      managerExecution?.hasExpired() ? 'manager-credential-expired' : 'turn-failed',
      managerExecution?.hasExpired()
        ? 'Manager credentials expired before this execution completed; inspect current state before retrying.'
        : `AgentJob turn threw: ${managerExecution ? managerExecution.mask(message) : message}`,
    )
    await eventSink.drain()
    return boundResult(
      await evaluateInitialReplyGuard({
        runtime: runtimeHandle,
        runtimeSessionId: attachedRuntimeSessionId ?? selected,
        workDir,
        payload,
        signal,
        observation,
        eventSink,
        managerExecution,
        originalResult,
      }),
      attachedRuntimeSessionId ?? selected,
    )
  }
  await eventSink.drain()
  const originalResult = managerExecution?.hasExpired()
    ? failureResult(
        'manager-credential-expired',
        'Manager credentials expired before this execution completed; inspect current state before retrying.',
        'opencode',
      )
    : redactManagerResult(projectTurnToWorkItemResult(result, 'opencode', modelInput, variant), managerExecution)
  return boundResult(
    await evaluateInitialReplyGuard({
      runtime: runtimeHandle,
      runtimeSessionId: result.ok ? result.value.facts.runtimeSessionId : (attachedRuntimeSessionId ?? selected),
      workDir,
      payload,
      signal,
      observation,
      eventSink,
      managerExecution,
      originalResult,
    }),
    result.ok ? result.value.facts.runtimeSessionId : (attachedRuntimeSessionId ?? selected),
  )
}

export async function executePiTurn(
  deps: AgentJobTurnDeps,
  work: DispatchWorkItem,
  signal: AbortSignal,
  payload: JsonObject | null,
  composed: string,
  model: ParsedModel,
  modelInput: string | null,
  variant: string | null,
  reasoningEffort: string | null,
  workDir: string,
  binding: BindingResolution,
  skills: readonly ResolvedSkill[],
  managerExecution: ManagerExecutionBoundary | null = deps.managerExecution ?? null,
): Promise<WorkItemResult> {
  const boundResult = (result: WorkItemResult, runtimeSessionId = binding.runtimeSessionId) =>
    withAgentBinding(result, work, binding, 'pi', runtimeSessionId)
  if (variant) {
    return boundResult(
      failureResult(
        'incompatible-execution-configuration',
        'AgentJob Pi variant is unsupported; configure reasoningEffort for the Pi thinking level',
        'pi',
      ),
    )
  }
  if (reasoningEffort && model.kind !== 'ok') {
    return boundResult(
      failureResult(
        'incompatible-execution-configuration',
        'AgentJob reasoningEffort requires an explicit provider/model selection',
        'pi',
      ),
    )
  }
  const runtime = resolveAccessor(deps.runtimes.pi)
  if (!runtime) {
    return boundResult(
      failureResult(
        'runtime-unavailable',
        'AgentJob requires the Pi runtime; the runner has not yet established the runtime or it is rebuilding',
        'pi',
      ),
    )
  }
  if (!runtime.ready()) {
    const diagnostic = runtime.diagnostic()
    return boundResult(
      failureResult(
        'runtime-unavailable',
        `AgentJob requires the Pi runtime to be ready: ${diagnostic?.message ?? 'no readiness diagnostic'}`,
        'pi',
      ),
    )
  }

  const observation = new ReplyActionObservationTracker()
  const eventSink = createAgentSessionEventSink(deps.connection, work, signal, binding.agentSessionId, observation)
  const runtimeHandle: CommandRuntimeHandle = { kind: 'pi', runtime }
  let runtimeSessionId = binding.runtimeSessionId
  if (!runtimeSessionId) {
    try {
      const created = await runtime.createSession({
        target: { runtime: 'pi', runtimeSessionId: null, workDir },
        managerExecution,
      })
      if (!created.ok) {
        const code = mapPiErrorKind(created.error.kind)
        return boundResult(failureResult(code, created.error.message, 'pi', created.error.diagnostics), null)
      }
      runtimeSessionId = created.value.runtimeSessionId
    } catch (error) {
      return boundResult(failureResult('turn-failed', `Pi session creation threw: ${errorMessage(error)}`, 'pi'), null)
    }
  }
  try {
    await eventSink.attachSession(runtimeSessionId, workDir, modelInput)
    if (!work.initialInputId || !work.initialTurnId) {
      await eventSink.publishSessionInput(composed, runtimeSessionId)
    }
  } catch (error) {
    return boundResult(
      failureResult('session-binding-failed', `Failed to persist the AgentSession binding: ${errorMessage(error)}`, 'pi'),
      runtimeSessionId,
    )
  }

  const request: PiTurnRequest = {
    target: { runtime: 'pi', runtimeSessionId, workDir },
    prompt: composed,
    options: {
      model: model.kind === 'ok' ? `${model.value.providerID}/${model.value.modelID}` : null,
      variant: null,
      reasoningEffort,
      ...(skills.length > 0 ? { skills } : {}),
      unknownKeys: collectUnknownKeys(payload),
    },
    managerExecution,
  }
  const observer: PiTurnObserver = {
    onEvent: (event) => {
      eventSink.observePiEvent(
        managerExecution
          ? {
              ...event,
              payload: managerExecution.redact(event.payload) as Record<string, unknown>,
            }
          : event,
      )
    },
  }

  let result: PiResult<PiTurnResult>
  try {
    result = await runWithModelRetry(deps.options, work, modelInput, variant, signal, () =>
      runtime.runTurn(request, signal, observer),
    )
  } catch (error) {
    const message = errorMessage(error)
    const originalResult = failureResult(
      managerExecution?.hasExpired() ? 'manager-credential-expired' : 'turn-failed',
      managerExecution?.hasExpired()
        ? 'Manager credentials expired before this execution completed; inspect current state before retrying.'
        : `AgentJob turn threw: ${managerExecution ? managerExecution.mask(message) : message}`,
    )
    await eventSink.drain()
    return boundResult(
      await evaluateInitialReplyGuard({
        runtime: runtimeHandle,
        runtimeSessionId,
        workDir,
        payload,
        signal,
        observation,
        eventSink,
        managerExecution,
        originalResult,
      }),
      runtimeSessionId,
    )
  }
  await eventSink.drain()
  const originalResult = managerExecution?.hasExpired()
    ? failureResult(
        'manager-credential-expired',
        'Manager credentials expired before this execution completed; inspect current state before retrying.',
        'pi',
      )
    : redactManagerResult(projectPiTurnToWorkItemResult(result, 'pi', modelInput, variant), managerExecution)
  return boundResult(
    await evaluateInitialReplyGuard({
      runtime: runtimeHandle,
      runtimeSessionId: result.ok ? result.value.facts.runtimeSessionId : runtimeSessionId,
      workDir,
      payload,
      signal,
      observation,
      eventSink,
      managerExecution,
      originalResult,
    }),
    result.ok ? result.value.facts.runtimeSessionId : runtimeSessionId,
  )
}

function withAgentBinding(
  result: WorkItemResult,
  work: DispatchWorkItem,
  binding: BindingResolution,
  runtime: 'opencode' | 'pi',
  runtimeSessionId: string | null,
): WorkItemResult {
  if (!binding.agentSessionId) return result
  return {
    ...result,
    agentBinding: {
      agentSessionId: binding.agentSessionId,
      agentTurnId: work.initialTurnId ?? null,
      runtime,
      runtimeSessionId,
    },
  }
}

function redactManagerResult(result: WorkItemResult, boundary: ManagerExecutionBoundary | null): WorkItemResult {
  if (!boundary) return result
  return boundary.redact(result) as WorkItemResult
}

async function evaluateInitialReplyGuard(args: {
  runtime: CommandRuntimeHandle
  runtimeSessionId: string | null
  workDir: string
  payload: JsonObject | null
  signal: AbortSignal
  observation: ReplyActionObservationTracker
  eventSink: AgentSessionEventSink
  managerExecution: ManagerExecutionBoundary | null
  originalResult: WorkItemResult
}): Promise<WorkItemResult> {
  const guard = new ReplyGuardCoordinator({
    runtime: {
      kind: args.runtime.kind,
      isAvailable: () => args.runtime.runtime.ready(),
    },
    runtimeSessionId: args.runtimeSessionId,
    workDir: args.workDir,
    slackExecutionContext: args.payload?.['slackExecutionContext'],
    observation: args.observation,
    signal: args.signal,
    runAdvisory: async (request) => {
      const observer: RuntimeTurnObserver | PiTurnObserver =
        args.runtime.kind === 'opencode'
          ? {
              onEvent: (event: RuntimeTurnEvent) =>
                args.eventSink.observeEvent(
                  args.managerExecution
                    ? {
                        ...event,
                        payload: args.managerExecution.redact(event.payload) as Record<string, unknown>,
                      }
                    : event,
                ),
            }
          : {
              onEvent: (event: PiRuntimeEvent) =>
                args.eventSink.observePiEvent(
                  args.managerExecution
                    ? {
                        ...event,
                        payload: args.managerExecution.redact(event.payload) as Record<string, unknown>,
                      }
                    : event,
                ),
            }
      const result = await callFollowup(
        args.runtime,
        {
          target: {
            runtime: args.runtime.kind,
            runtimeSessionId: request.runtimeSessionId,
            workDir: request.workDir,
          },
          prompt: request.prompt,
          managerExecution: args.managerExecution,
          options: { skills: [request.collaborationSkill] },
        },
        observer,
        request.signal,
      )
      await args.eventSink.drain()
      if (!result.ok) return replyGuardAdvisoryResult(result.error.kind)
      return { kind: 'completed' }
    },
  })
  return await guard.evaluate(args.originalResult)
}

function replyGuardAdvisoryResult(errorKind: string): ReplyGuardAdvisoryResult {
  if (errorKind === 'unavailable-runtime') return { kind: 'unavailable' }
  if (errorKind === 'interrupted' || errorKind === 'deadline-exceeded') return { kind: 'interrupted' }
  return { kind: 'failed' }
}

async function runWithModelRetry<T extends ModelTurnResult>(
  options: AgentJobExecutorOptions,
  work: DispatchWorkItem,
  modelInput: string | null,
  variant: string | null,
  signal: AbortSignal,
  attempt: () => Promise<T>,
): Promise<T> {
  let retryAttempt = 0
  while (true) {
    const result = await attempt()
    if (!modelInput || signal.aborted || !isModelUnavailableResult(result)) return result

    retryAttempt += 1
    const delayMs = modelRetryDelay(options, retryAttempt)
    log.warn('specified AgentJob model unavailable; retrying same work', {
      workId: work.workId,
      model: modelInput,
      variant,
      attempt: retryAttempt,
      delayMs,
    })
    const wait = options.waitForModelRetry ?? waitForModelRetry
    if (signal.aborted || !(await wait(delayMs, signal)) || signal.aborted) return result
  }
}

type ModelTurnResult = RuntimeResult<RuntimeTurnResult> | PiResult<PiTurnResult>

function isModelUnavailableResult(result: ModelTurnResult): boolean {
  if (result.ok) return false
  const diagnostics = [...result.error.diagnostics, ...result.diagnostics]
  if (diagnostics.some((entry) => MODEL_UNAVAILABLE_CODES.has(entry.code.toLowerCase()))) return true
  const messages = diagnostics.map((entry) => entry.message).concat(result.error.message)
  return messages.some((message) =>
    /\bmodel\b.{0,80}\b(?:unavailable|not available|not found|does not exist)\b/i.test(message),
  )
}

function modelRetryDelay(options: AgentJobExecutorOptions, attempt: number): number {
  const initial = finiteNonNegative(options.modelRetryInitialDelayMs, DEFAULT_MODEL_RETRY_INITIAL_DELAY_MS)
  const maximum = Math.max(initial, finiteNonNegative(options.modelRetryMaxDelayMs, DEFAULT_MODEL_RETRY_MAX_DELAY_MS))
  return Math.min(maximum, initial * 2 ** Math.min(attempt - 1, 30))
}

function finiteNonNegative(value: number | undefined, fallback: number): number {
  return value !== undefined && Number.isFinite(value) && value >= 0 ? value : fallback
}

function waitForModelRetry(delayMs: number, signal: AbortSignal): Promise<boolean> {
  return new Promise((resolve) => {
    if (signal.aborted) {
      resolve(false)
      return
    }

    let settled = false
    let timer: ReturnType<typeof setTimeout> | undefined
    let onAbort: () => void = () => {}
    const finish = (ready: boolean) => {
      if (settled) return
      settled = true
      signal.removeEventListener('abort', onAbort)
      resolve(ready)
    }
    onAbort = () => {
      if (timer !== undefined) clearTimeout(timer)
      finish(false)
    }
    timer = setTimeout(() => finish(true), delayMs)
    signal.addEventListener('abort', onAbort, { once: true })
  })
}

function collectUnknownKeys(payload: JsonObject | null): readonly string[] | undefined {
  if (!payload || typeof payload !== 'object') return undefined
  const known = new Set([
    'prompt',
    'instructions',
    'model',
    'reasoningEffort',
    'variant',
    'runtime',
    'skills',
    'attachments',
    'slackExecutionContext',
  ])
  const unknown: string[] = []
  for (const key of Object.keys(payload)) {
    if (!known.has(key)) unknown.push(key)
  }
  return unknown.length > 0 ? unknown : undefined
}

interface AgentSessionEventSink {
  attachSession(runtimeSessionId: string, workDir: string, model: string | null): Promise<void>
  publishSessionInput(text: string, runtimeSessionId: string): Promise<void>
  observeEvent(event: {
    readonly type: string
    readonly runtimeSessionId: string
    readonly payload: Record<string, unknown>
  }): void
  observePiEvent(event: PiRuntimeEvent): void
  drain(): Promise<void>
}

// Runtime-event delivery is best-effort telemetry: a stalled server response
// must never hold the serialized delivery chain (and therefore the post-turn
// drain) forever. Each request gets its own deadline and the drain gets an
// overall bound; a hung work otherwise stays "running" with nothing logged.
const AGENT_EVENT_DELIVERY_TIMEOUT_MS = 30_000
const AGENT_EVENT_DRAIN_TIMEOUT_MS = 120_000

export function createAgentSessionEventSink(
  connection: ServerConnection,
  work: DispatchWorkItem,
  signal: AbortSignal,
  agentSessionId: string | null,
  observation: ReplyActionObservationTracker = new ReplyActionObservationTracker(),
): AgentSessionEventSink {
  let pending: Promise<void> = Promise.resolve()
  const deliverySignal = () => AbortSignal.any([signal, AbortSignal.timeout(AGENT_EVENT_DELIVERY_TIMEOUT_MS)])
  const projectId = work.projectId
  if (!agentSessionId || !projectId) {
    const noop = async () => undefined
    return {
      attachSession: noop,
      publishSessionInput: noop,
      observeEvent: (event) => observation.observe(event),
      observePiEvent: (event) => observation.observe(event),
      drain: noop,
    }
  }
  return {
    async attachSession(runtimeSessionId, workDir, model) {
      try {
        await connection.openAgentSession(projectId!, agentSessionId, { workDir }, signal)
        await connection.attachAgentSession(
          projectId!,
          agentSessionId,
          {
            runtimeSessionId,
            workDir,
            processPid: null,
            model,
            workId: work.workId,
            agentJobId: work.agentJobId ?? null,
          },
          signal,
        )
      } catch (error) {
        log.error('agent-session open/attach failed', {
          job: work.agentJobId,
          session: agentSessionId,
          exception: error,
        })
        throw error
      }
    },
    async publishSessionInput(text, runtimeSessionId) {
      try {
        await connection.agentSessionRuntimeEvents(
          projectId!,
          agentSessionId,
          {
            workId: work.workId,
            workType: work.workType,
            stage: work.stage,
            runtimeSessionId,
            runtimeEvents: [
              {
                type: 'session.input',
                payload: {
                  text,
                  kind: 'task',
                  source: 'agent-job',
                  role: 'user',
                  runtimeSessionId,
                },
              },
            ],
          },
          signal,
        )
      } catch (error) {
        log.error('agent-session input publish failed', {
          job: work.agentJobId,
          session: agentSessionId,
          exception: error,
        })
        throw error
      }
    },
    observeEvent(event) {
      observation.observe(event)
      pending = pending
        .then(() =>
          connection
            .agentSessionRuntimeEvents(
              projectId!,
              agentSessionId,
              {
                workId: work.workId,
                workType: work.workType,
                stage: work.stage,
                runtimeSessionId: event.runtimeSessionId,
                runtimeEvents: [{ type: event.type, payload: event.payload }],
              },
              deliverySignal(),
            )
            .then(() => undefined),
        )
        .catch((error) => {
          log.error('agent-session runtime event failed', {
            job: work.agentJobId,
            session: agentSessionId,
            exception: error,
          })
        })
    },
    observePiEvent(event) {
      observation.observe(event)
      pending = pending
        .then(() =>
          connection
            .agentSessionRuntimeEvents(
              projectId!,
              agentSessionId,
              {
                workId: work.workId,
                workType: work.workType,
                stage: work.stage,
                runtimeSessionId: event.runtimeSessionId,
                runtimeEvents: [{ type: event.type, payload: event.payload }],
              },
              deliverySignal(),
            )
            .then(() => undefined),
        )
        .catch((error) => {
          log.error('agent-session runtime event failed', {
            job: work.agentJobId,
            session: agentSessionId,
            exception: error,
          })
        })
    },
    async drain() {
      const completed = await boundedWait(() => pending, AGENT_EVENT_DRAIN_TIMEOUT_MS)
      if (!completed) {
        log.warn('agent-session runtime event drain timed out; abandoning remaining deliveries', {
          job: work.agentJobId,
          session: agentSessionId,
          timeoutMs: AGENT_EVENT_DRAIN_TIMEOUT_MS,
        })
      }
    },
  }
}

export function failureResult(
  code: string,
  message: string,
  runtime: 'opencode' | 'pi' = 'opencode',
  diagnostics?: readonly { code: string; message: string }[],
): WorkItemResult {
  return {
    status: 'failed',
    message,
    error: { code, message },
    output: diagnostics ? buildAgentJobOutput(false, null, runtime, null, null, null, message, diagnostics) : undefined,
    exitCode: 1,
  }
}

function buildAgentJobOutput(
  ok: boolean,
  runtimeSessionId: string | null,
  runtime: 'opencode' | 'pi',
  model: string | null,
  variant: string | null,
  text: string | null,
  error: string | null,
  diagnostics: readonly { code: string; message: string }[],
  hint?: 'reset',
): JsonObject {
  return {
    kind: runtime,
    status: ok ? 'success' : 'failure',
    runtimeSessionId,
    model,
    variant,
    text,
    error,
    diagnostics: diagnostics.map((d) => ({ code: d.code, message: d.message })),
    ...(hint ? { hint } : {}),
  }
}

/**
 * Convert the runtime result directly into the AgentJob-owned work result.
 * This path deliberately does not cross the Workflow Action boundary.
 */
export function projectTurnToWorkItemResult(
  result: RuntimeResult<RuntimeTurnResult>,
  runtime: 'opencode' | 'pi',
  model: string | null,
  variant: string | null,
): WorkItemResult {
  if (!result.ok) {
    const error = result.error
    const output = buildAgentJobOutput(
      false,
      null,
      runtime,
      model,
      variant,
      null,
      error.message,
      result.diagnostics,
      error.kind === 'missing-session' ? 'reset' : undefined,
    )
    return {
      status: 'failed',
      message: error.message,
      error: { code: mapOpenCodeErrorKind(error.kind), message: error.message },
      output,
      exitCode: 1,
    }
  }
  const facts = result.value.facts
  const output = buildAgentJobOutput(
    true,
    facts.runtimeSessionId,
    runtime,
    model,
    variant,
    facts.finalAssistantText,
    null,
    result.value.diagnostics,
  )
  return {
    status: 'completed',
    message: 'AgentJob completed',
    output,
    exitCode: 0,
  }
}

export function projectPiTurnToWorkItemResult(
  result: PiResult<PiTurnResult>,
  runtime: 'opencode' | 'pi',
  model: string | null,
  variant: string | null,
): WorkItemResult {
  if (!result.ok) {
    const error = result.error
    const code = mapPiErrorKind(error.kind)
    const hint = error.kind === 'missing-session' ? ('reset' as const) : undefined
    const output = buildAgentJobOutput(
      false,
      null,
      runtime,
      model,
      variant,
      null,
      error.message,
      result.diagnostics,
      hint,
    )
    return {
      status: 'failed',
      message: error.message,
      error: { code, message: error.message },
      output,
      exitCode: 1,
    }
  }
  const facts = result.value.facts
  const output = buildAgentJobOutput(
    true,
    facts.runtimeSessionId,
    runtime,
    model,
    variant,
    facts.finalAssistantText,
    null,
    result.value.diagnostics,
  )
  return {
    status: 'completed',
    message: 'AgentJob completed',
    output,
    exitCode: 0,
  }
}
