import { errorMessage } from '../core/errors.js'
import type { AgentExecutionBinding, JsonObject, DispatchWorkItem, WorkItemResult } from '../core/types.js'
import type {
  CodexDiagnostic,
  CodexResult,
  CodexTurnOptions,
  CodexTurnRequest,
  CodexTurnResult,
  CodexTurnEventObserver,
} from './codex/index.js'
import { resolveAccessor } from '../server/command-runtime.js'
import type { ResolvedSkill } from './skill-resolver.js'
import type { DeliveredAttachment } from './attachment-delivery.js'
import type { ManagerExecutionBoundary } from './manager-execution-boundary.js'
import type { AgentJobTurnDeps } from './agent-job-turn.js'
import type { BindingResolution } from './agent-job-executor.js'
import { knownBinding } from './agent-job-executor.js'
import { admitInitialProviderSubmission } from './agent-job-initial-provider-admission.js'
import {
  buildAgentJobOutput,
  collectUnknownKeys,
  createAgentSessionEventSink,
  failureResult,
  physicalBinding,
  redactManagerResult,
  withAgentBinding,
} from './agent-job-turn.js'
import { mapRuntimeErrorKind } from './error-kind-mapping.js'
import { ReplyActionObservationTracker } from './reply-guard.js'

export async function executeCodexTurn(
  deps: AgentJobTurnDeps,
  work: DispatchWorkItem,
  signal: AbortSignal,
  payload: JsonObject | null,
  composed: string,
  modelInput: string | null,
  variant: string | null,
  reasoningEffort: string | null,
  workDir: string,
  binding: BindingResolution,
  skills: readonly ResolvedSkill[],
  attachments: readonly DeliveredAttachment[],
): Promise<WorkItemResult> {
  let executionBinding: AgentExecutionBinding | null = knownBinding(work, binding, 'codex')
  const boundResult = (result: WorkItemResult) => withAgentBinding(result, executionBinding)
  if (variant) {
    return boundResult(
      failureResult(
        'unsupported-execution-configuration',
        'AgentJob Codex variant is unsupported; configure model and reasoningEffort instead',
        'codex',
      ),
    )
  }
  const runtime = resolveAccessor(deps.runtimes.codex)
  if (!runtime) {
    return boundResult(
      failureResult(
        'runtime-unavailable',
        'AgentJob requires the Codex runtime; the runner has not yet established the runtime or it is rebuilding',
        'codex',
      ),
    )
  }
  if (!runtime.ready()) {
    const diagnostic = runtime.diagnostic()
    return boundResult(
      failureResult(
        'runtime-unavailable',
        `AgentJob requires the Codex runtime to be ready: ${diagnostic?.message ?? 'no readiness diagnostic'}`,
        'codex',
        diagnostic ? [diagnostic] : undefined,
      ),
    )
  }

  const observation = new ReplyActionObservationTracker()
  const eventSink = createAgentSessionEventSink(deps.connection, work, signal, binding.agentSessionId, observation)
  const skipInitialInput = Boolean(work.initialInputId && work.initialTurnId)
  const fileParts = attachments.flatMap((entry) =>
    entry.status === 'delivered' && entry.filePart ? [entry.filePart] : [],
  )
  const observer: CodexTurnEventObserver = {
    onSessionReady: async (session) => {
      executionBinding = physicalBinding(work, binding.agentSessionId, 'codex', session.runtimeSessionId)
      await eventSink.attachSession(session.runtimeSessionId, session.workDir, modelInput)
      await admitInitialProviderSubmission(deps.connection, work, binding, 'codex', session.runtimeSessionId, signal)
      if (!skipInitialInput) await eventSink.publishSessionInput(composed, session.runtimeSessionId)
    },
    onEvent: (event) => {
      eventSink.observeCodexEvent(
        deps.managerExecution
          ? { ...event, payload: deps.managerExecution.redact(event.payload) as Record<string, unknown> }
          : event,
      )
    },
    onDiagnostic: (diagnostic) => {
      eventSink.observeCodexDiagnostic(
        deps.managerExecution ? (deps.managerExecution.redact(diagnostic) as CodexDiagnostic) : diagnostic,
      )
    },
  }
  const request: CodexTurnRequest = {
    target: { runtime: 'codex', runtimeSessionId: binding.runtimeSessionId, workDir },
    prompt: composed,
    clientUserMessageId: work.initialInputId ?? null,
    fileParts: fileParts.length > 0 ? fileParts : null,
    options: {
      model: modelInput,
      reasoningEffort: reasoningEffort as CodexTurnOptions['reasoningEffort'],
      variant,
      unknownKeys: collectUnknownKeys(payload),
    },
  }

  let result: CodexResult<CodexTurnResult>
  try {
    result = await runtime.runTurn(request, signal, observer)
  } catch (error) {
    result = {
      ok: false,
      error: {
        kind: 'turn-failed',
        message: `AgentJob Codex turn threw: ${errorMessage(error)}`,
        diagnostics: [{ severity: 'error', code: 'turn-failed', message: errorMessage(error) }],
      },
      diagnostics: [],
    }
  }
  await eventSink.drain()
  if (result.ok)
    executionBinding = physicalBinding(work, binding.agentSessionId, 'codex', result.value.facts.runtimeSessionId)
  return boundResult(
    redactManagerResult(projectCodexTurnToWorkItemResult(result, modelInput, variant), deps.managerExecution ?? null),
  )
}

export function projectCodexTurnToWorkItemResult(
  result: CodexResult<CodexTurnResult>,
  model: string | null,
  variant: string | null,
): WorkItemResult {
  if (!result.ok) {
    const error = result.error
    const diagnostics = [...error.diagnostics, ...result.diagnostics]
    return {
      status: 'failed',
      message: error.message,
      error: {
        code: mapRuntimeErrorKind('codex', error.kind, diagnostics),
        message: error.message,
      },
      output: buildAgentJobOutput(
        false,
        null,
        'codex',
        model,
        variant,
        null,
        error.message,
        diagnostics,
        error.kind === 'missing-session' ? 'reset' : undefined,
      ),
      exitCode: 1,
    }
  }
  const facts = result.value.facts
  return {
    status: 'completed',
    message: 'AgentJob completed',
    output: buildAgentJobOutput(
      true,
      facts.runtimeSessionId,
      'codex',
      model,
      variant,
      facts.finalAssistantText,
      null,
      result.value.diagnostics,
    ),
    exitCode: 0,
  }
}
