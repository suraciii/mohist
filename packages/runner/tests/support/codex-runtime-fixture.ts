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
  CodexRuntimeTurnEvent,
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
  createSessionCalls: Array<{ target: { runtimeSessionId: null; workDir: string }; model?: string | null }>
  shutdownCalls: Array<{ clearDiagnostic?: boolean }>
  setReady: (ready: boolean) => void
  setCatalog: (catalog: CodexCatalog | null) => void
  setRunTurnResult: (result: CodexResult<CodexTurnResult>) => void
  setFollowupResult: (result: CodexResult<CodexFollowupResult>) => void
  setCancelResult: (result: CodexResult<CodexCancelResult>) => void
  setCompactResult: (result: CodexResult<CodexCompactResult>) => void
  setResetResult: (result: CodexResult<CodexResetResult>) => void
  setCreateSessionResult: (result: CodexResult<{ runtimeSessionId: string; workDir: string }>) => void
  setEmitSessionReady: (emit: boolean) => void
  setEvents: (events: readonly CodexRuntimeTurnEvent[]) => void
  setShutdownError: (error: Error | null) => void
}

const DEFAULT_SESSION = { runtimeSessionId: 'thread_fixture', workDir: '/workspace' }

export function makeFakeCodexRuntime(): FakeCodexRuntimeHandles {
  const runTurnCalls: CodexTurnRequest[] = []
  const followupCalls: CodexFollowupRequest[] = []
  const cancelCalls: CodexCancelRequest[] = []
  const compactCalls: CodexCompactRequest[] = []
  const resetCalls: CodexResetRequest[] = []
  const resolveSessionCalls: Array<{ runtimeSessionId: string; workDir: string }> = []
  const createSessionCalls: Array<{ target: { runtimeSessionId: null; workDir: string }; model?: string | null }> = []
  const shutdownCalls: Array<{ clearDiagnostic?: boolean }> = []
  let ready = true
  let catalog: CodexCatalog | null = null
  let emitSessionReady = true
  let events: readonly CodexRuntimeTurnEvent[] = []
  let shutdownError: Error | null = null
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
  let nextCreateSession: CodexResult<{ runtimeSessionId: string; workDir: string }> = {
    ok: true,
    value: { runtimeSessionId: 'thread_fixture_created', workDir: '/workspace' },
    diagnostics: [],
  }

  const runtime: Partial<CodexRuntime> = {
    ready: () => ready,
    diagnostic: () => null,
    catalog: () => catalog,
    async runTurn(request: CodexTurnRequest, _signal?: AbortSignal, observer?: CodexTurnEventObserver) {
      runTurnCalls.push(request)
      if (emitSessionReady) {
        const session = nextRunTurn.ok
          ? { runtimeSessionId: nextRunTurn.value.facts.runtimeSessionId, workDir: nextRunTurn.value.facts.workDir }
          : request.target.runtimeSessionId !== null
            ? { runtimeSessionId: request.target.runtimeSessionId, workDir: request.target.workDir }
            : null
        if (session) await observer?.onSessionReady?.(session)
      }
      for (const event of events) observer?.onEvent?.(event)
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
    async compact(request: CodexCompactRequest, observer?: CodexTurnEventObserver) {
      compactCalls.push(request)
      for (const event of events) observer?.onEvent?.(event)
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
    async createSession(request: { target: { runtimeSessionId: null; workDir: string }; model?: string | null }) {
      createSessionCalls.push(request)
      return nextCreateSession
    },
    async shutdown(options: { clearDiagnostic?: boolean } = {}) {
      shutdownCalls.push(options)
      if (shutdownError) throw shutdownError
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
    createSessionCalls,
    shutdownCalls,
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
    setCreateSessionResult(result) {
      nextCreateSession = result
    },
    setEmitSessionReady(value) {
      emitSessionReady = value
    },
    setEvents(value) {
      events = value
    },
    setShutdownError(error) {
      shutdownError = error
    },
  }
}
