import type {
  CodexCancelRequest,
  CodexCancelResult,
  CodexCatalog,
  CodexCompactRequest,
  CodexCompactResult,
  CodexFollowupRequest,
  CodexFollowupResult,
  CodexResetRequest,
  CodexResetResult,
  CodexResult,
  CodexRuntime,
  CodexTurnEventObserver,
  CodexTurnRequest,
  CodexTurnResult,
} from '../../src/runtime/codex/index.js'

export interface FakeCodexRuntimeHandles {
  runtime: CodexRuntime
  runTurnCalls: CodexTurnRequest[]
  followupCalls: CodexFollowupRequest[]
  cancelCalls: CodexCancelRequest[]
  compactCalls: CodexCompactRequest[]
  resetCalls: CodexResetRequest[]
  resolveSessionCalls: Array<{ runtimeSessionId: string; workDir: string }>
  setReady: (ready: boolean) => void
  setCatalog: (catalog: CodexCatalog | null) => void
  setRunTurnResult: (result: CodexResult<CodexTurnResult>) => void
  setFollowupResult: (result: CodexResult<CodexFollowupResult>) => void
  setCancelResult: (result: CodexResult<CodexCancelResult>) => void
  setCompactResult: (result: CodexResult<CodexCompactResult>) => void
  setResetResult: (result: CodexResult<CodexResetResult>) => void
}

const DEFAULT_SESSION = { runtimeSessionId: 'thread_fixture', workDir: '/workspace' }

export function makeFakeCodexRuntime(): FakeCodexRuntimeHandles {
  const runTurnCalls: CodexTurnRequest[] = []
  const followupCalls: CodexFollowupRequest[] = []
  const cancelCalls: CodexCancelRequest[] = []
  const compactCalls: CodexCompactRequest[] = []
  const resetCalls: CodexResetRequest[] = []
  const resolveSessionCalls: Array<{ runtimeSessionId: string; workDir: string }> = []
  let ready = true
  let catalog: CodexCatalog | null = null
  let nextRunTurn: CodexResult<CodexTurnResult> = {
    ok: true,
    value: {
      facts: { ...DEFAULT_SESSION, finalAssistantText: 'fixture response' },
      diagnostics: [],
    },
    diagnostics: [],
  }
  let nextFollowup: CodexResult<CodexFollowupResult> = {
    ok: true,
    value: {
      facts: { ...DEFAULT_SESSION, finalAssistantText: 'fixture follow-up' },
      diagnostics: [],
    },
    diagnostics: [],
  }
  let nextCancel: CodexResult<CodexCancelResult> = {
    ok: true,
    value: { facts: { ...DEFAULT_SESSION, cancelled: true, stopConfirmed: true }, diagnostics: [] },
    diagnostics: [],
  }
  let nextCompact: CodexResult<CodexCompactResult> = {
    ok: true,
    value: { facts: DEFAULT_SESSION, diagnostics: [] },
    diagnostics: [],
  }
  let nextReset: CodexResult<CodexResetResult> = {
    ok: true,
    value: { facts: { runtimeSessionId: 'thread_fixture_reset', workDir: '/workspace' }, diagnostics: [] },
    diagnostics: [],
  }

  const runtime: Partial<CodexRuntime> = {
    ready: () => ready,
    diagnostic: () => null,
    catalog: () => catalog,
    async runTurn(request: CodexTurnRequest, _signal?: AbortSignal, _observer?: CodexTurnEventObserver) {
      runTurnCalls.push(request)
      return nextRunTurn
    },
    async followup(request: CodexFollowupRequest, _observer?: CodexTurnEventObserver, _signal?: AbortSignal) {
      followupCalls.push(request)
      return nextFollowup
    },
    async cancel(request: CodexCancelRequest) {
      cancelCalls.push(request)
      return nextCancel
    },
    async compact(request: CodexCompactRequest, _observer?: CodexTurnEventObserver) {
      compactCalls.push(request)
      return nextCompact
    },
    async reset(request: CodexResetRequest) {
      resetCalls.push(request)
      return nextReset
    },
    async resolveSession(request: { target: { runtimeSessionId: string; workDir: string } }) {
      resolveSessionCalls.push(request.target)
      return {
        ok: true,
        value: { ...request.target, activeTurn: false },
        diagnostics: [],
      }
    },
  }

  return {
    runtime: runtime as CodexRuntime,
    runTurnCalls,
    followupCalls,
    cancelCalls,
    compactCalls,
    resetCalls,
    resolveSessionCalls,
    setReady(value) {
      ready = value
    },
    setCatalog(value) {
      catalog = value
    },
    setRunTurnResult(result) {
      nextRunTurn = result
    },
    setFollowupResult(result) {
      nextFollowup = result
    },
    setCancelResult(result) {
      nextCancel = result
    },
    setCompactResult(result) {
      nextCompact = result
    },
    setResetResult(result) {
      nextReset = result
    },
  }
}
