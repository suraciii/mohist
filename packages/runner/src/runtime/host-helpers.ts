import type {
  DispatchWorkItem,
  RunnerOptions,
  RunnerRegistration,
  RuntimeReadinessWitness,
  WorkItemResult,
} from '../core/types.js'
import type { ManagerExecutionGrantResponse } from '../core/types.js'
import { ManagerExecutionBoundary, type ManagerExecutionBoundaryOptions } from './manager-execution-boundary.js'
import type { RuntimeResult, RuntimeTurnResult, OpenCodeRuntime } from './opencode/index.js'
import type { PiResult, PiRuntime, PiTurnResult } from './pi/index.js'
import type { RecoverableRuntime, RuntimeTurnRecoveryResult } from './binding-recovery.js'
import { projectPiTurnToWorkItemResult, projectTurnToWorkItemResult } from './agent-job-turn.js'
import type { FollowupTarget, FollowupTargetResolution, SessionTarget } from '../server/session-target.js'
import type { ServerConnection } from '../server/connection.js'
import type { HostTaskLogDeps } from './host-task-log.js'
import type { TerminalTaskLogDeliveryStore } from './terminal-task-log-delivery.js'
import type { AwaitingAckEntry, InFlightEntry } from './host-state.js'
import { workKey } from './work-result-journal.js'

export type RuntimeKind = 'opencode' | 'pi'

export const MANAGER_RUNTIME_CAPABILITIES = [
  'manager-execution-grant-v1',
  'manager-deployment-epoch-v1',
  'manager-private-broker-v1',
  'manager-pi-scoped-executor-v1',
  'manager-opencode-isolated-v1',
  'manager-redaction-v1',
] as const

export function isManagerExecutionWork(work: Pick<DispatchWorkItem, 'projectId'>): boolean {
  return work.projectId === '__mohist_slack_manager__'
}

export function supportsManagerExecution(registration: RunnerRegistration): boolean {
  return (
    process.platform === 'linux' &&
    MANAGER_RUNTIME_CAPABILITIES.every((capability) => registration.capabilities.includes(capability))
  )
}

export async function createManagerExecutionBoundary(
  grant: ManagerExecutionGrantResponse,
  runnerRoot: string,
  options?: ManagerExecutionBoundaryOptions,
): Promise<ManagerExecutionBoundary | null> {
  try {
    return await ManagerExecutionBoundary.create(grant, runnerRoot, options)
  } catch {
    return null
  }
}

export function gateManagerCapabilities(state: RunnerRegistration, openCodeReady: boolean): RunnerRegistration {
  if (openCodeReady) return state
  return {
    ...state,
    capabilities: state.capabilities.filter(
      (capability) => !MANAGER_RUNTIME_CAPABILITIES.includes(capability as never),
    ),
  }
}

export function usesOpenCode(work: DispatchWorkItem): boolean {
  return runtimeKindForWork(work) === 'opencode'
}

export function isAgentRecoveryDispatch(work: DispatchWorkItem): boolean {
  const recovery = work.agentRecovery
  return Boolean(recovery && recovery.runtime.trim() && recovery.runtimeSessionId.trim())
}

export function openCodeOwners(
  inFlight: Iterable<InFlightEntry>,
  awaitingAck: Iterable<{ work: DispatchWorkItem; entry: AwaitingAckEntry }>,
): string[] {
  return [
    ...[...inFlight]
      .filter((entry) => usesOpenCode(entry.work) && !isManagerExecutionWork(entry.work))
      .map((entry) => workKey(entry.work)),
    ...[...awaitingAck]
      .filter((entry) => usesOpenCode(entry.work) && !isManagerExecutionWork(entry.work))
      .map((entry) => workKey(entry.work)),
  ]
}

export function syncOpenCodeWorkOwners(
  runtime: OpenCodeRuntime | null,
  inFlight: Iterable<InFlightEntry>,
  awaitingAck: Iterable<{ work: DispatchWorkItem; entry: AwaitingAckEntry }>,
): void {
  if (runtime) runtime.setWorkOwners(openCodeOwners(inFlight, awaitingAck))
}

export function isOpenCodeReadyForClaim(
  runtime: OpenCodeRuntime | null,
  runtimeEventOutbox: { ready(): boolean },
): boolean {
  return runtime !== null && runtime.ready() && runtimeEventOutbox.ready()
}

export function runtimeReadinessWitnesses(
  openCodeRuntime: OpenCodeRuntime | null,
  piRuntime: PiRuntime | null,
  piRuntimeGeneration: number,
): RuntimeReadinessWitness[] {
  return [
    {
      runtime: 'opencode',
      ready: openCodeRuntime?.ready() === true,
      generation: openCodeRuntime?.ownership().generation ?? null,
    },
    {
      runtime: 'pi',
      ready: piRuntime?.ready() === true,
      generation: piRuntime?.ready() === true ? piRuntimeGeneration : null,
    },
  ]
}

export interface RunnerPollReport {
  processGeneration: string
  inFlight: string[]
  awaitingAck: string[]
  runtimeReadiness: RuntimeReadinessWitness[]
  connectionId: string | null
  admissionReady: boolean
  deploymentEpoch: string | null
}

export function buildRunnerPollReport(input: {
  processGeneration: string
  durableStarted: readonly string[]
  inFlight: Iterable<string>
  awaitingAck: Iterable<string>
  runtimeReadiness: RuntimeReadinessWitness[]
  connectionId: string | null
  admissionReady: boolean
  deploymentEpoch: string | null
}): RunnerPollReport {
  return {
    processGeneration: input.processGeneration,
    inFlight: [...new Set([...input.inFlight, ...input.durableStarted])],
    awaitingAck: [...input.awaitingAck],
    runtimeReadiness: input.runtimeReadiness,
    connectionId: input.connectionId,
    admissionReady: input.admissionReady,
    deploymentEpoch: input.deploymentEpoch,
  }
}

export function resolveFollowupTarget(options: RunnerOptions, target: SessionTarget): FollowupTargetResolution {
  if (options.projectId && options.projectId !== target.projectId) return null
  const binding = target.binding ?? null
  if (!binding) return null
  const runtime = binding.runtime.toLowerCase()
  if (runtime !== 'opencode' && runtime !== 'pi') return null
  if (binding.runnerId !== options.runnerId) return null
  if (!binding.runtimeSessionId) return null
  if (!binding.workDir) return null
  const resolved: FollowupTarget = {
    runtimeSessionId: binding.runtimeSessionId,
    workDir: binding.workDir,
    projectId: target.projectId,
    ...(target.kind === 'generic' && target.definition ? { definition: target.definition } : {}),
  }
  return resolved
}

export function currentCatalogRevision(
  catalogs: RunnerRegistration['runtimeCatalogs'],
  runtime: string,
): string | null {
  const normalized = runtime.trim().toLowerCase()
  if (!catalogs) return null
  for (const [key, entry] of Object.entries(catalogs)) {
    if (key.trim().toLowerCase() === normalized) return entry.capabilityRevision ?? null
  }
  return null
}

export function createHostTaskLogDeps(
  connection: ServerConnection,
  terminalTaskLogDelivery: TerminalTaskLogDeliveryStore,
  options: RunnerOptions,
): HostTaskLogDeps {
  return { connection, terminalTaskLogDelivery, options }
}

export function runtimeKindForWork(work: DispatchWorkItem): RuntimeKind | null {
  const declared = typeof work.with?.runtime === 'string' ? work.with.runtime : work.agentDefinition?.runtime
  const candidate = (declared ?? work.uses ?? '').trim().toLowerCase()
  if (candidate === 'opencode' || candidate === 'mohist/opencode') return 'opencode'
  if (candidate === 'pi' || candidate === 'mohist/pi') return 'pi'
  return null
}

export function runtimeForKind(
  kind: RuntimeKind,
  openCodeRuntime: OpenCodeRuntime | null,
  piRuntime: PiRuntime | null,
): RecoverableRuntime | null {
  if (kind === 'opencode') return openCodeRuntime ? { kind, runtime: openCodeRuntime } : null
  return piRuntime ? { kind, runtime: piRuntime } : null
}

export function projectReattachedRuntimeResult(
  work: DispatchWorkItem,
  runtimeKind: RuntimeKind,
  adopted: RuntimeTurnRecoveryResult,
): WorkItemResult {
  const model = stringProperty(work.with, 'model') ?? work.agentDefinition?.model ?? null
  const variant = stringProperty(work.with, 'variant') ?? work.agentDefinition?.variant ?? null
  if (work.ownerKind === 'agent-job') {
    return runtimeKind === 'opencode'
      ? projectTurnToWorkItemResult(adopted as RuntimeResult<RuntimeTurnResult>, runtimeKind, model, variant)
      : projectPiTurnToWorkItemResult(adopted as PiResult<PiTurnResult>, runtimeKind, model, variant)
  }
  if (!adopted.ok) {
    return {
      status: 'failed',
      message: adopted.error.message,
      error: { code: adopted.error.kind, message: adopted.error.message },
      exitCode: 1,
    }
  }
  return {
    status: 'completed',
    message: 'Agent turn completed after runner restart',
    output: {
      kind: runtimeKind,
      status: 'success',
      runtimeSessionId: adopted.value.facts.runtimeSessionId,
      model,
      variant,
      text: adopted.value.facts.finalAssistantText,
      diagnostics: adopted.value.diagnostics.map((diagnostic) => ({
        code: diagnostic.code,
        message: diagnostic.message,
      })),
    },
    exitCode: 0,
  }
}

function stringProperty(value: Record<string, unknown> | null | undefined, key: string): string | null {
  const candidate = value?.[key]
  return typeof candidate === 'string' ? candidate : null
}

export async function delay(ms: number, signal: AbortSignal): Promise<void> {
  if (signal.aborted) throw signal.reason
  await new Promise<void>((resolve, reject) => {
    const timer = setTimeout(() => {
      signal.removeEventListener('abort', onAbort)
      resolve()
    }, ms)
    const onAbort = () => {
      clearTimeout(timer)
      reject(signal.reason)
    }
    signal.addEventListener('abort', onAbort, { once: true })
  })
}

/**
 * Race a poll-interval timer against in-flight work promises. The timer is
 * owned here so whichever racer settles first clears it before resolving.
 */
export function raceInterval(ms: number, signal: AbortSignal, racers: Promise<unknown>[]): Promise<void> {
  return new Promise((resolve) => {
    let timer: ReturnType<typeof setTimeout> | null = null
    let settled = false
    const done = () => {
      if (settled) return
      settled = true
      if (timer) clearTimeout(timer)
      signal.removeEventListener('abort', onAbort)
      resolve()
    }
    const onAbort = done
    if (signal.aborted) {
      done()
      return
    }
    timer = setTimeout(done, ms)
    timer.unref?.()
    signal.addEventListener('abort', onAbort, { once: true })
    for (const racer of racers) racer.then(done, done)
  })
}

export function boundedSignal(parent: AbortSignal, timeoutMs: number): { signal: AbortSignal; dispose: () => void } {
  const controller = new AbortController()
  const abortFromParent = () => controller.abort(parent.reason)
  if (parent.aborted) abortFromParent()
  else parent.addEventListener('abort', abortFromParent, { once: true })

  const timeout = setTimeout(() => controller.abort(new Error(`request timed out after ${timeoutMs}ms`)), timeoutMs)
  timeout.unref?.()

  return {
    signal: controller.signal,
    dispose: () => {
      clearTimeout(timeout)
      parent.removeEventListener('abort', abortFromParent)
    },
  }
}
