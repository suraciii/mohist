import type { RuntimeDiagnostic, RuntimeFollowupRequest, RuntimeTurnOptions } from './types.js'
import { normalizeInvalidInput } from './errors.js'
import type { OpencodeServerHandle } from './server-process.js'
import type { RuntimeEventSubscription } from './event-subscription.js'

export interface RuntimeGeneration {
  readonly id: number
  readonly server: OpencodeServerHandle
  readonly events: RuntimeEventSubscription
  readonly drained: Promise<void>
  readonly resolveDrained: () => void
  readonly activeTurns: Set<ActiveGenerationTurn>
  quarantined: boolean
  closed: boolean
  drainResolved: boolean
}

export interface ActiveGenerationTurn {
  readonly abortController: AbortController
  readonly forced: Promise<void>
  readonly resolveForced: () => void
  forcedFailure: boolean
}

export function newRuntimeGeneration(
  id: number,
  server: OpencodeServerHandle,
  events: RuntimeEventSubscription,
): RuntimeGeneration {
  let resolveDrained!: () => void
  const drained = new Promise<void>((resolve) => {
    resolveDrained = resolve
  })
  return {
    id,
    server,
    events,
    drained,
    resolveDrained,
    activeTurns: new Set(),
    quarantined: false,
    closed: false,
    drainResolved: false,
  }
}

export function toDiagnostic(cause: unknown, code: string, fallback: string): RuntimeDiagnostic {
  if (cause instanceof Error) {
    return { severity: 'error', code, message: cause.message || fallback }
  }
  return { severity: 'error', code, message: fallback, details: { cause: String(cause) } }
}

export function toRawError(cause: unknown): { message: string; status?: number; code?: string; service?: string } {
  if (cause instanceof Error) {
    const message = cause.message || 'OpenCode error'
    const status = (cause as { status?: number }).status
    const code = (cause as { code?: string }).code
    const service = (cause as { service?: string }).service
    return {
      message,
      ...(typeof status === 'number' ? { status } : {}),
      ...(typeof code === 'string' ? { code } : {}),
      ...(typeof service === 'string' ? { service } : {}),
    }
  }
  return { message: String(cause) }
}

export function errorMessage(cause: unknown, fallback: string): string {
  if (cause instanceof Error) return cause.message || fallback
  return String(cause) || fallback
}

export function positiveDuration(value: number | null | undefined): number | undefined {
  return value !== undefined && value !== null && Number.isFinite(value) && value > 0 ? Math.floor(value) : undefined
}

export function combineAbortSignals(
  parent: AbortSignal,
  forced: AbortSignal,
): { signal: AbortSignal; dispose: () => void } {
  const controller = new AbortController()
  const abort = () => controller.abort(parent.aborted ? parent.reason : forced.reason)
  if (parent.aborted || forced.aborted) {
    abort()
    return { signal: controller.signal, dispose: () => {} }
  }
  parent.addEventListener('abort', abort, { once: true })
  forced.addEventListener('abort', abort, { once: true })
  return {
    signal: controller.signal,
    dispose: () => {
      parent.removeEventListener('abort', abort)
      forced.removeEventListener('abort', abort)
    },
  }
}

type FollowupValidationOk = {
  kind: 'ok'
  value: { model: { providerID: string; modelID: string } | null; variant: string | null }
}
type FollowupValidationFailure = {
  kind: 'failure'
  error: ReturnType<typeof normalizeInvalidInput>
}
type FollowupValidationResult = FollowupValidationOk | FollowupValidationFailure

export function validateFollowupInput(
  request: RuntimeFollowupRequest,
  diagnostics: RuntimeDiagnostic[],
): FollowupValidationResult {
  const options: RuntimeTurnOptions | undefined | null = request.options ?? undefined
  if (options?.unknownKeys && options.unknownKeys.length > 0) {
    diagnostics.push({
      severity: 'info',
      code: 'options-unknown-keys',
      message: `Ignored unknown option keys: ${options.unknownKeys.join(', ')}`,
      details: { keys: options.unknownKeys },
    })
  }
  let model: { providerID: string; modelID: string } | null = null
  if (options?.model !== undefined && options.model !== null) {
    if (typeof options.model !== 'object') {
      return {
        kind: 'failure',
        error: normalizeInvalidInput('options.model must be an object with providerID and modelID when present'),
      }
    }
    model = options.model
  }
  let variant: string | null = null
  if (options?.variant !== undefined && options.variant !== null) {
    if (typeof options.variant !== 'string') {
      return { kind: 'failure', error: normalizeInvalidInput('options.variant must be a string when present') }
    }
    variant = options.variant
  }
  if (!request.prompt || typeof request.prompt !== 'string' || request.prompt.trim().length === 0) {
    return { kind: 'failure', error: normalizeInvalidInput('Follow-up prompt must be a non-empty string') }
  }
  return { kind: 'ok', value: { model, variant } }
}
