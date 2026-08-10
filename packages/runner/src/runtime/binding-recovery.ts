import type { OpenCodeRuntime, RuntimeResult, RuntimeSessionCreateResult } from "./opencode/index.js"
import type { PiResult, PiRuntime, PiSessionResult } from "./pi/index.js"

export type RecoverableRuntime =
  | { readonly kind: "opencode"; readonly runtime: OpenCodeRuntime }
  | { readonly kind: "pi"; readonly runtime: PiRuntime }

export interface RuntimeBinding {
  readonly runnerId: string
  readonly runtime: "opencode" | "pi"
  readonly runtimeSessionId: string | null
  readonly workDir: string
}

export type BindingProbeResult =
  | { readonly ok: true; readonly activeTurn: boolean }
  | { readonly ok: false; readonly kind: string; readonly message: string }

export type BindingRecoveryResult =
  | { readonly ok: true; readonly binding: RuntimeBinding; readonly recovered: boolean }
  | { readonly ok: false; readonly kind: string; readonly message: string; readonly candidateRuntimeSessionId?: string }

export interface ResolveOrRecoverBindingRequest {
  readonly runnerId: string
  readonly expected: RuntimeBinding
  readonly runtime: RecoverableRuntime
  readonly probe: (binding: RuntimeBinding) => Promise<BindingProbeResult>
  readonly replace: (expected: RuntimeBinding, replacement: RuntimeBinding) => Promise<void>
  readonly model?: { readonly providerID: string; readonly modelID: string } | null
  /**
   * Workflow retries must not submit a second prompt while the previous
   * physical session still has an active turn. Background convergence and
   * follow-up steering deliberately leave this false.
   */
  readonly rejectActiveTurn?: boolean
  readonly recoveryKey?: string
  readonly coordinator?: BindingRecoveryCoordinator
}

export class BindingRecoveryCoordinator {
  private readonly inFlight = new Map<string, Promise<BindingRecoveryResult>>()

  run(key: string, operation: () => Promise<BindingRecoveryResult>): Promise<BindingRecoveryResult> {
    const existing = this.inFlight.get(key)
    if (existing) return existing
    const current = operation().finally(() => {
      if (this.inFlight.get(key) === current) this.inFlight.delete(key)
    })
    this.inFlight.set(key, current)
    return current
  }
}

export async function resolveOrRecoverBinding(
  request: ResolveOrRecoverBindingRequest,
): Promise<BindingRecoveryResult> {
  if (request.coordinator && request.recoveryKey) {
    const mode = request.rejectActiveTurn === true ? "reject-active" : "allow-active"
    return request.coordinator.run(`${mode}:${request.recoveryKey}`, () => resolveOrRecoverBinding({ ...request, coordinator: undefined, recoveryKey: undefined }))
  }
  const expected = request.expected
  if (expected.runnerId !== request.runnerId) {
    return failure("different-runner", "The Runtime Session binding belongs to a different Runner")
  }
  if (request.runtime.kind !== expected.runtime) {
    return failure("incompatible-runtime", "The Runtime Session binding does not match the selected runtime")
  }

  if (expected.runtimeSessionId) {
    let resolved: BindingProbeResult
    try {
      resolved = await request.probe(expected)
    } catch (error) {
      return failure("unavailable-runtime", error instanceof Error ? error.message : String(error))
    }
    if (resolved.ok) {
      if (request.rejectActiveTurn === true && resolved.activeTurn) {
        return failure("active-turn", "The bound Runtime Session still has an active turn; refusing to reuse it for a workflow retry")
      }
      return { ok: true, binding: expected, recovered: false }
    }
    if (resolved.kind !== "missing-session") return failure(resolved.kind, resolved.message)
  }

  const created = await createEmptySession(request.runtime, expected, request.model)
  if (!created.ok) return failure(created.error.kind, created.error.message)

  const replacement: RuntimeBinding = {
    ...expected,
    runtimeSessionId: created.value.runtimeSessionId,
    workDir: created.value.workDir,
  }
  try {
    await request.replace(expected, replacement)
  } catch (error) {
    return {
      ok: false,
      kind: "candidate-unbound",
      message: error instanceof Error ? error.message : String(error),
      candidateRuntimeSessionId: replacement.runtimeSessionId ?? undefined,
    }
  }
  return { ok: true, binding: replacement, recovered: expected.runtimeSessionId !== null }
}

export async function createEmptySession(
  handle: RecoverableRuntime,
  binding: RuntimeBinding,
  model: { readonly providerID: string; readonly modelID: string } | null | undefined,
): Promise<RuntimeResult<RuntimeSessionCreateResult> | PiResult<PiSessionResult>> {
  if (handle.kind === "opencode") {
    return await handle.runtime.createSession({
      target: { runtime: "opencode", runtimeSessionId: null, workDir: binding.workDir },
      model,
    })
  }
  return await handle.runtime.createSession({
    target: { runtime: "pi", runtimeSessionId: null, workDir: binding.workDir },
  })
}

function failure(kind: string, message: string): BindingRecoveryResult {
  return { ok: false, kind, message }
}
