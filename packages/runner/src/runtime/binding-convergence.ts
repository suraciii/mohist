import type { AgentSessionReconcileBinding, AgentSessionRuntimeEventReceipt } from "../server/connection.js"
import type { AgentSessionRuntimeEventOutbox, RuntimeEventRecord } from "../server/runtime-event-outbox.js"
import { BindingRecoveryCoordinator, resolveOrRecoverBinding, type BindingProbeResult, type RecoverableRuntime, type RuntimeBinding } from "./binding-recovery.js"
import type { OpenCodeRuntime } from "./opencode/index.js"
import type { PiRuntime } from "./pi/index.js"
import { runnerLogger } from "../system/logger.js"

const log = runnerLogger.child("session")

export interface BindingConvergenceConnection {
  listAgentSessionsForReconcile(signal: AbortSignal): Promise<AgentSessionReconcileBinding[]>
  reconcileMissingAgentSession(sessionId: string, body: unknown, signal: AbortSignal): Promise<AgentSessionReconcileBinding>
  reconcileAgentSessionRuntimeEvents(sessionId: string, body: unknown, signal: AbortSignal): Promise<AgentSessionRuntimeEventReceipt[]>
}

export interface BindingConvergenceOptions {
  readonly runnerId: string
  readonly connection: BindingConvergenceConnection
  readonly outbox: AgentSessionRuntimeEventOutbox
  readonly openCodeRuntime: () => OpenCodeRuntime | null
  readonly piRuntime: () => PiRuntime | null
  readonly now?: () => Date
  readonly randomId?: () => string
  readonly recoveryCoordinator?: BindingRecoveryCoordinator
}

export class BindingConvergence {
  private readonly now: () => Date
  private readonly randomId: () => string
  private running: Promise<void> | null = null

  constructor(private readonly options: BindingConvergenceOptions) {
    this.now = options.now ?? (() => new Date())
    this.randomId = options.randomId ?? (() => Math.random().toString(36).slice(2, 10))
  }

  runOnce(signal: AbortSignal): Promise<void> {
    if (this.running) return this.running
    const running = this.reconcile(signal).finally(() => {
      if (this.running === running) this.running = null
    })
    this.running = running
    return running
  }

  private async reconcile(signal: AbortSignal): Promise<void> {
    const bindings = await this.options.connection.listAgentSessionsForReconcile(signal)
    for (const binding of bindings) {
      if (signal.aborted) return
      try {
        await this.reconcileBinding(binding, signal)
      } catch (error) {
        log.error("agent-session binding reconciliation failed", { session: binding.sessionId, exception: error })
      }
    }
  }

  private async reconcileBinding(binding: AgentSessionReconcileBinding, signal: AbortSignal): Promise<void> {
    const runtime = this.runtime(binding.runtime)
    if (!runtime) return
    const expected: RuntimeBinding = {
      runnerId: this.options.runnerId,
      runtime: binding.runtime,
      runtimeSessionId: binding.runtimeSessionId,
      workDir: binding.workDir,
    }
    const result = await resolveOrRecoverBinding({
      runnerId: this.options.runnerId,
      expected,
      runtime,
      probe: (candidate) => probeBinding(runtime, candidate),
      replace: async (current, replacement) => {
        await this.options.connection.reconcileMissingAgentSession(binding.sessionId, {
          expectedRunnerId: current.runnerId,
          expectedRuntime: current.runtime,
          expectedRuntimeSessionId: current.runtimeSessionId,
          replacementRuntimeSessionId: replacement.runtimeSessionId,
        }, signal)
      },
      recoveryKey: expected.runtimeSessionId!,
      coordinator: this.options.recoveryCoordinator,
    })
    if (!result.ok) {
      if (result.kind === "candidate-unbound") throw new Error(result.message)
      return
    }
    if (result.recovered) return
    const probe = await probeBinding(runtime, result.binding)
    if (probe.ok) await this.recordActivity(binding, probe.activeTurn ? "active" : "idle")
  }

  private runtime(runtime: "opencode" | "pi"): RecoverableRuntime | null {
    if (runtime === "opencode") {
      const handle = this.options.openCodeRuntime()
      return handle ? { kind: "opencode", runtime: handle } : null
    }
    const handle = this.options.piRuntime()
    return handle ? { kind: "pi", runtime: handle } : null
  }

  private async recordActivity(binding: AgentSessionReconcileBinding, activity: "active" | "idle"): Promise<void> {
    const observedAt = this.now().toISOString()
    const record: RuntimeEventRecord = {
      id: `reconcile-activity:${binding.sessionId}:${binding.runtimeSessionId}:${activity}:${observedAt}:${this.randomId()}`,
      producerFamily: "binding-reconcile",
      target: { kind: "session", sessionId: binding.sessionId },
      runtimeSessionId: binding.runtimeSessionId,
      work: null,
      event: {
        type: "session.activity",
        payload: {
          activity,
          source: "runner-reconnect",
          runtimeSessionId: binding.runtimeSessionId,
          observedAt,
        },
      },
      acknowledgementPolicy: "successful-response",
    }
    await this.options.outbox.enqueueProducedFact(record)
  }
}

async function probeBinding(runtime: RecoverableRuntime, binding: RuntimeBinding): Promise<BindingProbeResult> {
  try {
    const result = runtime.kind === "opencode"
      ? await runtime.runtime.resolveSession({ target: { runtime: "opencode", runtimeSessionId: binding.runtimeSessionId, workDir: binding.workDir } })
      : await runtime.runtime.resolveSession({ target: { runtime: "pi", runtimeSessionId: binding.runtimeSessionId, workDir: binding.workDir } })
    return result.ok
      ? { ok: true, activeTurn: result.value.activeTurn }
      : { ok: false, kind: result.error.kind, message: result.error.message }
  } catch (error) {
    return { ok: false, kind: "unavailable-runtime", message: error instanceof Error ? error.message : String(error) }
  }
}
