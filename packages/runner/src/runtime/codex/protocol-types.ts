/**
 * Locked narrow v2 protocol subset for `codex app-server`.
 *
 * The full Codex app-server protocol is generated from upstream sources
 * (see design/runtimes/codex.md). The generated tree is large and
 * unstable; Mohist only depends on the small stable v2 method set and
 * the matched notification / server-request / terminal event shapes the
 * `CodexRuntime` deep module uses. This file pins the names and shapes
 * used at runtime boundaries.
 *
 * Rules:
 *
 *   1. Only the methods listed under {@link CODEX_LOCKED_METHODS} and the
 *      event / server-request shapes listed in the discriminant maps
 *      cross into or out of the module. The protocol may grow upstream
 *      without affecting `CodexRuntime`.
 *   2. The shape predicates (`isCodexInitializeRequest`,
 *      `isCodexThreadStartRequest`, etc.) discriminate incoming
 *      JSON-RPC payloads by stable, exact field names. Anything that
 *      does not match is a protocol failure for the active request.
 *   3. The committed subset is checked by a CI script that regenerates
 *      the upstream outputs and proves the locked subset still matches
 *      before the supported version range widens.
 *   4. Experimental APIs, history surfaces, Apps, connectors, realtime,
 *      and review Turns are not part of this subset.
 *
 * These types are intentionally narrow Mohist-owned shapes. The full
 * generated app-server DTO tree is an implementation detail of the
 * line-framed JSON-RPC consumer (see `server-process.ts`) and MUST NOT
 * cross the module boundary.
 */

// ---------------------------------------------------------------------------
// JSON-RPC framing
// ---------------------------------------------------------------------------

/**
 * JSON-RPC v2 envelope used for every request sent to `codex app-server`
 * and every response / notification / server-request we receive. Only
 * the fields Mohist actually relies on are declared; unknown fields are
 * ignored at the consumer layer.
 */
export interface CodexJsonRpcRequest<P = unknown> {
  readonly jsonrpc: '2.0'
  readonly id: number | string
  readonly method: string
  readonly params?: P
}

export interface CodexJsonRpcSuccess<TResult = unknown> {
  readonly jsonrpc: '2.0'
  readonly id: number | string
  readonly result: TResult
}

export interface CodexJsonRpcError {
  readonly jsonrpc: '2.0'
  readonly id: number | string
  readonly error: {
    readonly code: number
    readonly message: string
    readonly data?: unknown
  }
}

export interface CodexJsonRpcNotification<P = unknown> {
  readonly jsonrpc: '2.0'
  readonly method: string
  readonly params?: P
}

export interface CodexJsonRpcServerRequest<P = unknown> {
  readonly jsonrpc: '2.0'
  readonly id: number | string
  readonly method: string
  readonly params?: P
}

export type CodexJsonRpcMessage =
  | CodexJsonRpcSuccess
  | CodexJsonRpcError
  | CodexJsonRpcNotification
  | CodexJsonRpcServerRequest

// ---------------------------------------------------------------------------
// Locked v2 method names (the narrow subset Mohist drives)
// ---------------------------------------------------------------------------

/**
 * The exact method set Mohist drives. Anything outside this set is a
 * protocol-failure boundary — the runtime never accepts it, the line-
 * framed consumer never advertises it, and the registered subset never
 * widens without an update-script review.
 */
export const CODEX_LOCKED_METHODS = [
  'initialize',
  'initialized',
  'thread/start',
  'thread/resume',
  'turn/start',
  'turn/steer',
  'turn/interrupt',
  'thread/compact/start',
  'model/list',
] as const

export type CodexLockedMethod = (typeof CODEX_LOCKED_METHODS)[number]

export function isCodexLockedMethod(method: string): method is CodexLockedMethod {
  return (CODEX_LOCKED_METHODS as readonly string[]).includes(method)
}

// ---------------------------------------------------------------------------
// initialize / initialized
// ---------------------------------------------------------------------------

/**
 * `initialize` request — no experimental client capabilities, no
 * `clientInfo` shape beyond what the locked compatibility test allows.
 * A managed `CODEX_HOME` is implied via environment; the server echoes
 * it back on the response, where Mohist asserts equality with the
 * managed path.
 */
export interface CodexInitializeParams {
  readonly protocolVersion?: string
  readonly clientInfo?: { readonly name: string; readonly version: string }
}

/**
 * `initialize` response — `codexHome` MUST equal the managed state
 * path before any other request is admitted.
 */
export interface CodexInitializeResult {
  readonly protocolVersion: string
  readonly codexHome: string
  readonly userAgent?: string
}

export const CODEX_INITIALIZE_PARAMS: CodexInitializeParams = Object.freeze({})

/**
 * `initialized` notification — body is intentionally empty; declaring
 * the discriminant on the empty shape keeps the consumer routing
 * uniform.
 */
export interface CodexInitializedParams {
  readonly _marker?: never
}

export function isCodexInitializeRequest(value: unknown): value is CodexJsonRpcRequest<CodexInitializeParams> {
  if (!isCodexRequestEnvelope(value)) return false
  if (value.method !== 'initialize') return false
  const params = value.params
  if (params === undefined) return true
  if (params === null || typeof params !== 'object') return false
  const candidate = params as { protocolVersion?: unknown; clientInfo?: unknown }
  if (candidate.protocolVersion !== undefined && typeof candidate.protocolVersion !== 'string') return false
  if (candidate.clientInfo !== undefined) {
    const info = candidate.clientInfo as { name?: unknown; version?: unknown } | null
    if (info === null || typeof info !== 'object') return false
    if (typeof info.name !== 'string' || typeof info.version !== 'string') return false
  }
  return true
}

export function isCodexInitializeResult(value: unknown): value is CodexJsonRpcSuccess<CodexInitializeResult> {
  if (!isCodexSuccessEnvelope(value)) return false
  const result = value.result
  if (!result || typeof result !== 'object') return false
  const candidate = result as { protocolVersion?: unknown; codexHome?: unknown; userAgent?: unknown }
  if (typeof candidate.protocolVersion !== 'string') return false
  if (typeof candidate.codexHome !== 'string') return false
  if (candidate.userAgent !== undefined && typeof candidate.userAgent !== 'string') return false
  return true
}

// ---------------------------------------------------------------------------
// thread/start, thread/resume, thread/compact/start
// ---------------------------------------------------------------------------

/**
 * Approval policy locked to `never` and sandbox locked to
 * `danger-full-access`. The runtime never overrides these — headless
 * execution fails closed.
 */
export const CODEX_APPROVAL_POLICY = 'never'
export const CODEX_SANDBOX_POLICY = 'danger-full-access'

export interface CodexThreadStartParams {
  readonly cwd: string
  readonly approvalPolicy?: string
  readonly sandbox?: string
  readonly model?: string
  readonly reasoningEffort?: string
  readonly ephemeral?: boolean
  readonly persistHistory?: boolean
}

export interface CodexThreadStartResult {
  readonly threadId: string
  readonly cwd: string
  readonly model?: string
  readonly reasoningEffort?: string
}

export function isCodexThreadStartRequest(value: unknown): value is CodexJsonRpcRequest<CodexThreadStartParams> {
  if (!isCodexRequestEnvelope(value) || value.method !== 'thread/start') return false
  const params = value.params
  if (!params || typeof params !== 'object') return false
  const candidate = params as { cwd?: unknown }
  if (typeof candidate.cwd !== 'string') return false
  return true
}

export function isCodexThreadStartResult(value: unknown): value is CodexJsonRpcSuccess<CodexThreadStartResult> {
  if (!isCodexSuccessEnvelope(value)) return false
  const result = value.result
  if (!result || typeof result !== 'object') return false
  const candidate = result as { threadId?: unknown; cwd?: unknown }
  return typeof candidate.threadId === 'string' && typeof candidate.cwd === 'string'
}

export interface CodexThreadResumeParams {
  readonly threadId: string
  readonly cwd: string
  readonly excludeTurns?: boolean
}

export interface CodexThreadResumeResult {
  readonly threadId: string
  readonly cwd: string
  readonly model?: string
  readonly reasoningEffort?: string
}

export function isCodexThreadResumeRequest(value: unknown): value is CodexJsonRpcRequest<CodexThreadResumeParams> {
  if (!isCodexRequestEnvelope(value) || value.method !== 'thread/resume') return false
  const params = value.params
  if (!params || typeof params !== 'object') return false
  const candidate = params as { threadId?: unknown; cwd?: unknown; excludeTurns?: unknown }
  if (typeof candidate.threadId !== 'string') return false
  if (typeof candidate.cwd !== 'string') return false
  if (candidate.excludeTurns !== undefined && typeof candidate.excludeTurns !== 'boolean') return false
  return true
}

export interface CodexThreadCompactStartParams {
  readonly threadId: string
  readonly cwd: string
}

export interface CodexThreadCompactStartResult {
  readonly threadId: string
  readonly turnId: string
}

export function isCodexThreadCompactStartRequest(
  value: unknown,
): value is CodexJsonRpcRequest<CodexThreadCompactStartParams> {
  if (!isCodexRequestEnvelope(value) || value.method !== 'thread/compact/start') return false
  const params = value.params
  if (!params || typeof params !== 'object') return false
  const candidate = params as { threadId?: unknown; cwd?: unknown }
  return typeof candidate.threadId === 'string' && typeof candidate.cwd === 'string'
}

// ---------------------------------------------------------------------------
// turn/start, turn/steer, turn/interrupt
// ---------------------------------------------------------------------------

export interface CodexTurnStartParams {
  readonly threadId: string
  readonly input: ReadonlyArray<CodexTurnInputItem>
  readonly model?: string
  readonly reasoningEffort?: string
  /**
   * Mohist SessionInput ID for correlation only — not a provider
   * idempotency key. The runtime never resubmits an unconfirmed input
   * even with the same value.
   */
  readonly clientUserMessageId?: string
}

export type CodexTurnInputItem =
  | { readonly type: 'text'; readonly text: string }
  | { readonly type: 'image'; readonly url: string; readonly mime: string; readonly filename?: string }
  | { readonly type: 'localImage'; readonly path: string }

export interface CodexTurnStartResult {
  readonly turnId: string
  readonly threadId: string
  readonly status: string
}

export function isCodexTurnStartRequest(value: unknown): value is CodexJsonRpcRequest<CodexTurnStartParams> {
  if (!isCodexRequestEnvelope(value) || value.method !== 'turn/start') return false
  const params = value.params
  if (!params || typeof params !== 'object') return false
  const candidate = params as { threadId?: unknown; input?: unknown }
  if (typeof candidate.threadId !== 'string') return false
  if (!Array.isArray(candidate.input)) return false
  return candidate.input.every(isCodexTurnInputItem)
}

export function isCodexTurnInputItem(value: unknown): value is CodexTurnInputItem {
  if (!value || typeof value !== 'object') return false
  const candidate = value as {
    type?: unknown
    text?: unknown
    url?: unknown
    mime?: unknown
    filename?: unknown
    path?: unknown
  }
  if (candidate.type === 'text') return typeof candidate.text === 'string'
  if (candidate.type === 'image') {
    if (typeof candidate.url !== 'string') return false
    if (typeof candidate.mime !== 'string') return false
    if (candidate.filename !== undefined && typeof candidate.filename !== 'string') return false
    return true
  }
  if (candidate.type === 'localImage') return typeof candidate.path === 'string'
  return false
}

export interface CodexTurnSteerParams {
  readonly threadId: string
  readonly turnId: string
  readonly input: ReadonlyArray<CodexTurnInputItem>
}

export function isCodexTurnSteerRequest(value: unknown): value is CodexJsonRpcRequest<CodexTurnSteerParams> {
  if (!isCodexRequestEnvelope(value) || value.method !== 'turn/steer') return false
  const params = value.params
  if (!params || typeof params !== 'object') return false
  const candidate = params as { threadId?: unknown; turnId?: unknown; input?: unknown }
  if (typeof candidate.threadId !== 'string') return false
  if (typeof candidate.turnId !== 'string') return false
  if (!Array.isArray(candidate.input)) return false
  return candidate.input.every(isCodexTurnInputItem)
}

export interface CodexTurnInterruptParams {
  readonly threadId: string
  readonly turnId: string
}

export interface CodexTurnInterruptResult {
  readonly threadId: string
  readonly turnId: string
  readonly accepted: boolean
}

export function isCodexTurnInterruptRequest(value: unknown): value is CodexJsonRpcRequest<CodexTurnInterruptParams> {
  if (!isCodexRequestEnvelope(value) || value.method !== 'turn/interrupt') return false
  const params = value.params
  if (!params || typeof params !== 'object') return false
  const candidate = params as { threadId?: unknown; turnId?: unknown }
  return typeof candidate.threadId === 'string' && typeof candidate.turnId === 'string'
}

export function isCodexTurnInterruptResult(value: unknown): value is CodexJsonRpcSuccess<CodexTurnInterruptResult> {
  if (!isCodexSuccessEnvelope(value)) return false
  const result = value.result
  if (!result || typeof result !== 'object') return false
  const candidate = result as { threadId?: unknown; turnId?: unknown; accepted?: unknown }
  return (
    typeof candidate.threadId === 'string' &&
    typeof candidate.turnId === 'string' &&
    typeof candidate.accepted === 'boolean'
  )
}

// ---------------------------------------------------------------------------
// model/list
// ---------------------------------------------------------------------------

export interface CodexModelListParams {
  readonly cursor?: string | null
  readonly pageSize?: number
}

export interface CodexModelDescriptor {
  readonly id: string
  readonly displayName?: string
  readonly reasoningEfforts?: readonly string[]
  readonly defaultReasoningEffort?: string
  readonly supportsReasoningEffort?: boolean
}

export interface CodexModelListResult {
  readonly models: readonly CodexModelDescriptor[]
  readonly nextCursor?: string | null
  readonly complete: boolean
}

export function isCodexModelListResult(value: unknown): value is CodexJsonRpcSuccess<CodexModelListResult> {
  if (!isCodexSuccessEnvelope(value)) return false
  const result = value.result
  if (!result || typeof result !== 'object') return false
  const candidate = result as { models?: unknown; nextCursor?: unknown; complete?: unknown }
  if (!Array.isArray(candidate.models)) return false
  if (typeof candidate.complete !== 'boolean') return false
  if (candidate.nextCursor !== undefined && candidate.nextCursor !== null && typeof candidate.nextCursor !== 'string') {
    return false
  }
  return true
}

// ---------------------------------------------------------------------------
// Notifications, server requests, terminal turn/completed
// ---------------------------------------------------------------------------

/**
 * Server-initiated approval / permission / user-input / MCP elicitation
 * / dynamic-tool requests are rejected with the protocol-defined denial
 * response. The runtime never answers them on the user's behalf.
 */
export interface CodexServerRequestParams {
  readonly threadId?: string
  readonly turnId?: string
  readonly reason?: string
}

export function isCodexServerRequest(value: unknown): value is CodexJsonRpcServerRequest<CodexServerRequestParams> {
  if (!value || typeof value !== 'object') return false
  const candidate = value as { jsonrpc?: unknown; id?: unknown; method?: unknown; params?: unknown }
  if (candidate.jsonrpc !== '2.0') return false
  if (typeof candidate.method !== 'string') return false
  if (typeof candidate.id !== 'string' && typeof candidate.id !== 'number') return false
  if (candidate.params !== undefined && (candidate.params === null || typeof candidate.params !== 'object')) {
    return false
  }
  return true
}

/**
 * Server-pushed item events. Each item has an exact `type` discriminant;
 * unknown types stay as diagnostics and never change execution state.
 */
export type CodexItemEvent =
  | { readonly type: 'agentMessage'; readonly text: string }
  | { readonly type: 'reasoning'; readonly summary: string }
  | { readonly type: 'commandExecution'; readonly command: string; readonly status: string }
  | { readonly type: 'fileChange'; readonly path: string; readonly kind: 'create' | 'modify' | 'delete' }
  | { readonly type: 'mcpToolCall'; readonly tool: string; readonly status: string }
  | { readonly type: 'webSearch'; readonly query: string }
  | { readonly type: 'contextCompaction'; readonly threadId: string; readonly turnId: string }
  | { readonly type: 'usage'; readonly inputTokens: number; readonly outputTokens: number }

export function isCodexItemEvent(value: unknown): value is CodexItemEvent {
  if (!value || typeof value !== 'object') return false
  const candidate = value as { type?: unknown }
  if (typeof candidate.type !== 'string') return false
  return true
}

/**
 * Terminal `turn/completed` event — sole provider completion authority.
 * Only the exact active Thread + Turn IDs may complete an AgentJob.
 */
export type CodexTurnCompletedStatus = 'completed' | 'failed' | 'interrupted'

export interface CodexTurnCompletedEvent {
  readonly type: 'turn/completed'
  readonly threadId: string
  readonly turnId: string
  readonly status: CodexTurnCompletedStatus
  readonly error?: { readonly code?: string; readonly message: string }
}

export function isCodexTurnCompletedEvent(value: unknown): value is CodexTurnCompletedEvent {
  if (!value || typeof value !== 'object') return false
  const candidate = value as { type?: unknown; threadId?: unknown; turnId?: unknown; status?: unknown }
  if (candidate.type !== 'turn/completed') return false
  if (typeof candidate.threadId !== 'string') return false
  if (typeof candidate.turnId !== 'string') return false
  return candidate.status === 'completed' || candidate.status === 'failed' || candidate.status === 'interrupted'
}

/**
 * Thread status push — informational only, never completion authority.
 */
export interface CodexThreadStatusEvent {
  readonly type: 'thread/status'
  readonly threadId: string
  readonly turnId?: string
  readonly status: string
}

export function isCodexThreadStatusEvent(value: unknown): value is CodexThreadStatusEvent {
  if (!value || typeof value !== 'object') return false
  const candidate = value as { type?: unknown; threadId?: unknown; status?: unknown }
  if (candidate.type !== 'thread/status') return false
  if (typeof candidate.threadId !== 'string') return false
  if (typeof candidate.status !== 'string') return false
  return true
}

// ---------------------------------------------------------------------------
// Envelope predicates
// ---------------------------------------------------------------------------

function isCodexRequestEnvelope(value: unknown): value is CodexJsonRpcRequest {
  if (!value || typeof value !== 'object') return false
  const candidate = value as { jsonrpc?: unknown; id?: unknown; method?: unknown; params?: unknown }
  if (candidate.jsonrpc !== '2.0') return false
  if (typeof candidate.method !== 'string') return false
  if (typeof candidate.id !== 'string' && typeof candidate.id !== 'number') return false
  if (candidate.params !== undefined && (candidate.params === null || typeof candidate.params !== 'object')) {
    return false
  }
  return true
}

function isCodexSuccessEnvelope(value: unknown): value is CodexJsonRpcSuccess {
  if (!value || typeof value !== 'object') return false
  const candidate = value as { jsonrpc?: unknown; id?: unknown; result?: unknown }
  if (candidate.jsonrpc !== '2.0') return false
  if (typeof candidate.id !== 'string' && typeof candidate.id !== 'number') return false
  return 'result' in candidate
}
