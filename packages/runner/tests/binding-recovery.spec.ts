import { describe, expect, it, vi } from "vitest"
import { BindingRecoveryCoordinator, resolveOrRecoverBinding, type RuntimeBinding } from "../src/runtime/binding-recovery.js"
import type { OpenCodeRuntime } from "../src/runtime/opencode/index.js"

const expected: RuntimeBinding = {
  runnerId: "runner-1",
  runtime: "opencode",
  runtimeSessionId: "session-old",
  workDir: "/work/project",
}

function runtime(create = vi.fn(async () => ({
  ok: true as const,
  value: { runtimeSessionId: "session-new", workDir: "/work/project" },
  diagnostics: [],
}))) {
  return { createSession: create } as unknown as OpenCodeRuntime
}

describe("resolveOrRecoverBinding", () => {
  it("reuses a binding that resolves ready", async () => {
    const create = vi.fn()
    const replace = vi.fn()
    const result = await resolveOrRecoverBinding({
      runnerId: "runner-1",
      expected,
      runtime: { kind: "opencode", runtime: runtime(create) },
      probe: async () => ({ ok: true, activeTurn: false }),
      replace,
    })
    expect(result).toEqual({ ok: true, binding: expected, recovered: false })
    expect(create).not.toHaveBeenCalled()
    expect(replace).not.toHaveBeenCalled()
  })

  it("fails closed for a workflow retry when the bound physical session is still active", async () => {
    const create = vi.fn()
    const replace = vi.fn()
    const result = await resolveOrRecoverBinding({
      runnerId: "runner-1",
      expected,
      runtime: { kind: "opencode", runtime: runtime(create) },
      probe: async () => ({ ok: true, activeTurn: true }),
      replace,
      rejectActiveTurn: true,
    })
    expect(result).toEqual({
      ok: false,
      kind: "active-turn",
      message: "The bound Runtime Session still has an active turn; refusing to reuse it for a workflow retry",
    })
    expect(create).not.toHaveBeenCalled()
    expect(replace).not.toHaveBeenCalled()
  })

  it("creates one candidate and confirms the replacement after deterministic missing", async () => {
    const create = vi.fn(async () => ({ ok: true as const, value: { runtimeSessionId: "session-new", workDir: expected.workDir }, diagnostics: [] }))
    const replace = vi.fn(async () => undefined)
    const result = await resolveOrRecoverBinding({
      runnerId: "runner-1",
      expected,
      runtime: { kind: "opencode", runtime: runtime(create) },
      probe: async () => ({ ok: false, kind: "missing-session", message: "missing" }),
      replace,
    })
    expect(result).toMatchObject({ ok: true, recovered: true, binding: { runtimeSessionId: "session-new" } })
    expect(create).toHaveBeenCalledTimes(1)
    expect(replace).toHaveBeenCalledTimes(1)
  })

  it.each(["unavailable-runtime", "deadline-exceeded", "permission-required", "turn-failed", "incompatible-runtime"])(
    "preserves the binding for %s",
    async (kind) => {
      const create = vi.fn()
      const replace = vi.fn()
      const result = await resolveOrRecoverBinding({
        runnerId: "runner-1",
        expected,
        runtime: { kind: "opencode", runtime: runtime(create) },
        probe: async () => ({ ok: false, kind, message: kind }),
        replace,
      })
      expect(result).toEqual({ ok: false, kind, message: kind })
      expect(create).not.toHaveBeenCalled()
      expect(replace).not.toHaveBeenCalled()
    },
  )

  it("does not resolve or migrate a binding owned by another runner", async () => {
    const probe = vi.fn()
    const create = vi.fn()
    const result = await resolveOrRecoverBinding({
      runnerId: "runner-2",
      expected,
      runtime: { kind: "opencode", runtime: runtime(create) },
      probe,
      replace: vi.fn(),
    })
    expect(result).toMatchObject({ ok: false, kind: "different-runner" })
    expect(probe).not.toHaveBeenCalled()
    expect(create).not.toHaveBeenCalled()
  })

  it("returns an unbound-candidate diagnostic when CAS confirmation fails", async () => {
    const result = await resolveOrRecoverBinding({
      runnerId: "runner-1",
      expected,
      runtime: { kind: "opencode", runtime: runtime() },
      probe: async () => ({ ok: false, kind: "missing-session", message: "missing" }),
      replace: async () => { throw new Error("stale binding") },
    })
    expect(result).toEqual({
      ok: false,
      kind: "candidate-unbound",
      message: "stale binding",
      candidateRuntimeSessionId: "session-new",
    })
  })

  it("coalesces concurrent recovery attempts before candidate creation", async () => {
    const coordinator = new BindingRecoveryCoordinator()
    let releaseCreate!: () => void
    let markCreateStarted!: () => void
    const createStarted = new Promise<void>((resolve) => { markCreateStarted = resolve })
    const create = vi.fn(async () => {
      markCreateStarted()
      await new Promise<void>((resolve) => { releaseCreate = resolve })
      return { ok: true as const, value: { runtimeSessionId: "session-new", workDir: expected.workDir }, diagnostics: [] }
    })
    const request = {
      runnerId: "runner-1",
      expected,
      runtime: { kind: "opencode" as const, runtime: runtime(create) },
      probe: async () => ({ ok: false as const, kind: "missing-session", message: "missing" }),
      replace: async () => undefined,
      recoveryKey: "session-1",
      coordinator,
    }
    const first = resolveOrRecoverBinding(request)
    const second = resolveOrRecoverBinding(request)
    await createStarted
    expect(create).toHaveBeenCalledOnce()
    releaseCreate()
    await expect(Promise.all([first, second])).resolves.toEqual([
      { ok: true, binding: { ...expected, runtimeSessionId: "session-new" }, recovered: true },
      { ok: true, binding: { ...expected, runtimeSessionId: "session-new" }, recovered: true },
    ])
  })
})
