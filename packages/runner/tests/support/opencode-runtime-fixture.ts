import type {
  OpenCodeRuntime,
  RuntimeCancelRequest,
  RuntimeCancelResult,
  RuntimeFollowupRequest,
  RuntimeFollowupResult,
  RuntimeResult,
  RuntimeSessionCreateRequest,
  RuntimeSessionCreateResult,
} from "../../src/runtime/opencode/index.js"

export interface FakeRuntimeHandles {
  runtime: OpenCodeRuntime
  followupCalls: RuntimeFollowupRequest[]
  cancelCalls: RuntimeCancelRequest[]
  createSessionCalls: RuntimeSessionCreateRequest[]
  setFollowupResult: (result: RuntimeResult<RuntimeFollowupResult>) => void
  setCancelResult: (result: RuntimeResult<RuntimeCancelResult>) => void
  setReady: (ready: boolean) => void
}

export function makeFakeRuntime(): FakeRuntimeHandles {
  const followupCalls: RuntimeFollowupRequest[] = []
  const cancelCalls: RuntimeCancelRequest[] = []
  const createSessionCalls: RuntimeSessionCreateRequest[] = []
  let ready = true
  let nextFollowup: RuntimeResult<RuntimeFollowupResult> = {
    ok: true,
    value: {
      facts: { runtimeSessionId: "ses_runtime", workDir: "/work/project" },
      diagnostics: [],
    },
    diagnostics: [],
  }
  let nextCancel: RuntimeResult<RuntimeCancelResult> = {
    ok: true,
    value: {
      facts: { runtimeSessionId: "ses_runtime", workDir: "/work/project", cancelled: true },
      diagnostics: [],
    },
    diagnostics: [],
  }
  const runtime: Partial<OpenCodeRuntime> = {
    ready: () => ready,
    diagnostic: () => null,
    async createSession(request: RuntimeSessionCreateRequest): Promise<RuntimeResult<RuntimeSessionCreateResult>> {
      createSessionCalls.push(request)
      return { ok: true, value: { runtimeSessionId: "ses_replacement", workDir: request.target.workDir }, diagnostics: [] }
    },
    async followup(request: RuntimeFollowupRequest): Promise<RuntimeResult<RuntimeFollowupResult>> {
      followupCalls.push(request)
      return nextFollowup
    },
    async cancel(request: RuntimeCancelRequest): Promise<RuntimeResult<RuntimeCancelResult>> {
      cancelCalls.push(request)
      return nextCancel
    },
  }
  return {
    runtime: runtime as OpenCodeRuntime,
    followupCalls,
    cancelCalls,
    createSessionCalls,
    setFollowupResult(result) { nextFollowup = result },
    setCancelResult(result) { nextCancel = result },
    setReady(value) { ready = value },
  }
}
