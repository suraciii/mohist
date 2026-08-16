import type { DispatchWorkItem, WorkItemResult } from '../core/types.js'
import type { RuntimeResult, RuntimeTurnResult, OpenCodeRuntime } from './opencode/index.js'
import type { PiResult, PiRuntime, PiTurnResult } from './pi/index.js'
import type { RecoverableRuntime, RuntimeTurnRecoveryResult } from './binding-recovery.js'
import { projectPiTurnToWorkItemResult, projectTurnToWorkItemResult } from './agent-job-turn.js'

export type RuntimeKind = 'opencode' | 'pi'

export function usesOpenCode(work: DispatchWorkItem): boolean {
  return runtimeKindForWork(work) === 'opencode'
}

export function isAgentRecoveryDispatch(work: DispatchWorkItem): boolean {
  const recovery = work.agentRecovery
  return Boolean(recovery && recovery.runtime.trim() && recovery.runtimeSessionId.trim())
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
