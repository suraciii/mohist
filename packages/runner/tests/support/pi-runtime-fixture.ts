import type {
  PiCancelFacts,
  PiCancelRequest,
  PiCancelResult,
  PiCompactFacts,
  PiCompactRequest,
  PiCompactResult,
  PiError,
  PiFollowupFacts,
  PiFollowupRequest,
  PiFollowupResult,
  PiResetFacts,
  PiResetRequest,
  PiResetResult,
  PiResult,
  PiRuntime,
  PiTurnObserver,
} from "../../src/runtime/pi/index.js"

export interface FakePiRuntimeHandles {
  runtime: PiRuntime
  followupCalls: PiFollowupRequest[]
  cancelCalls: PiCancelRequest[]
  compactCalls: PiCompactRequest[]
  resetCalls: PiResetRequest[]
  setReady: (ready: boolean) => void
  setFollowupResult: (result: PiResult<PiFollowupFacts>) => void
  setCancelResult: (result: PiResult<PiCancelFacts>) => void
  setCompactResult: (result: PiResult<PiCompactFacts>) => void
  setResetResult: (result: PiResult<PiResetFacts>) => void
  setFollowupError: (error: PiError) => void
}

export function makeFakePiRuntime(): FakePiRuntimeHandles {
  const followupCalls: PiFollowupRequest[] = []
  const cancelCalls: PiCancelRequest[] = []
  const compactCalls: PiCompactRequest[] = []
  const resetCalls: PiResetRequest[] = []
  let ready = true
  let nextFollowup: PiResult<PiFollowupFacts> = {
    ok: true,
    value: { runtimeSessionId: "/virtual/sessions/one.jsonl", workDir: "/workspace" },
    diagnostics: [],
  }
  let nextCancel: PiResult<PiCancelFacts> = {
    ok: true,
    value: { runtimeSessionId: "/virtual/sessions/one.jsonl", workDir: "/workspace", cancelled: true, stopConfirmed: true },
    diagnostics: [],
  }
  let nextCompact: PiResult<PiCompactFacts> = {
    ok: true,
    value: { runtimeSessionId: "/virtual/sessions/one.jsonl", workDir: "/workspace" },
    diagnostics: [],
  }
  let nextReset: PiResult<PiResetFacts> = {
    ok: true,
    value: { runtimeSessionId: "/virtual/sessions/two.jsonl", workDir: "/workspace" },
    diagnostics: [],
  }

  const runtime: Partial<PiRuntime> = {
    ready: () => ready,
    diagnostic: () => null,
    async followup(request: PiFollowupRequest, _observer?: PiTurnObserver): Promise<PiFollowupResult> {
      followupCalls.push(request)
      return nextFollowup
    },
    async cancel(request: PiCancelRequest): Promise<PiCancelResult> {
      cancelCalls.push(request)
      return nextCancel
    },
    async compact(request: PiCompactRequest, _observer?: PiTurnObserver): Promise<PiCompactResult> {
      compactCalls.push(request)
      return nextCompact
    },
    async reset(request: PiResetRequest): Promise<PiResetResult> {
      resetCalls.push(request)
      return nextReset
    },
  }
  return {
    runtime: runtime as PiRuntime,
    followupCalls,
    cancelCalls,
    compactCalls,
    resetCalls,
    setReady(value) { ready = value },
    setFollowupResult(result) { nextFollowup = result },
    setCancelResult(result) { nextCancel = result },
    setCompactResult(result) { nextCompact = result },
    setResetResult(result) { nextReset = result },
    setFollowupError(error) {
      nextFollowup = { ok: false, error, diagnostics: error.diagnostics }
    },
  }
}
