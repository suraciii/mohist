/**
 * Codex app-server initialization handshake.
 *
 * The handshake runs once per app-server generation, immediately
 * after the spawned child process is alive. It sends `initialize`
 * with the required client identity and no experimental client capabilities,
 * requires the response's `codexHome` to equal the managed state path,
 * then sends `initialized` to complete the handshake. No further request
 * is admitted before initialization completes; the readiness gate consumes
 * the result.
 *
 * The handshake is intentionally narrow: `initialize` carries the required
 * `clientInfo` identity and a null capabilities value. It does not opt into
 * experimental APIs; those are rejected by the protocol-subset lock.
 */

import type { CodexDiagnostic, CodexResult } from './types.js'
import { CODEX_INITIALIZE_PARAMS, isCodexInitializeResult, type CodexInitializeResult } from './protocol-types.js'
import { normalizeIncompatibleRuntimeCodex, normalizeUnavailableRuntimeCodex } from './errors.js'
import { redactCodexCredentialString } from './credential.js'

/**
 * The narrow shape of an app-server child that the handshake drives.
 * The runtime owns the rest of the lifecycle; the handshake only
 * needs to send two envelopes and listen for one response plus the
 * optional server-initiated request that some app-server builds emit
 * after `initialize`.
 */
export interface CodexInitializationTransport {
  send<P, R>(request: { readonly method: 'initialize'; readonly params?: P; readonly id: number }): Promise<R>
  /**
   * Send a notification (no id, no response). Used for the
   * `initialized` notification that completes the handshake.
   */
  notify<P>(notification: { readonly method: 'initialized'; readonly params?: P }): boolean
  /**
   * Optional callback used by the transport to expose the child
   * exit state. When the child has exited before the response, the
   * handshake fails closed.
   */
  hasExited(): boolean
}

export interface CodexInitializationOptions {
  readonly managedCodexHome: string
  readonly startupTimeoutMs: number
  readonly clock?: {
    readonly setTimeout: (callback: () => void, delayMs: number) => unknown
    readonly clearTimeout: (handle: unknown) => void
  }
}

export interface CodexInitializationOutcome {
  readonly handshakeComplete: true
  readonly protocolVersion: string
  readonly codexHome: string
  readonly userAgent: string | null
  readonly diagnostics: readonly CodexDiagnostic[]
}

/**
 * Run the `initialize` → `initialized` handshake on the supplied
 * transport. Returns the recognized `codexHome`, `protocolVersion`,
 * and (when provided) `userAgent` from the server.
 *
 * Failure modes:
 *
 *   - the transport rejects / the response shape is not the locked
 *     `initialize` result ⇒ `unavailable-runtime` with a structured
 *     diagnostic;
 *   - the server's `codexHome` does not equal the managed state path
 *     ⇒ `incompatible-runtime` (the runtime is talking to a
 *     non-managed Codex home and must refuse work);
 *   - the server's response advertises experimental APIs (anything
 *     outside the locked subset) ⇒ `incompatible-runtime`;
 *   - the child has exited before the handshake completes ⇒ the
 *     handshake fails closed.
 */
export async function performCodexInitialization(
  transport: CodexInitializationTransport,
  options: CodexInitializationOptions,
): Promise<CodexResult<CodexInitializationOutcome>> {
  const diagnostics: CodexDiagnostic[] = []
  if (transport.hasExited()) {
    const diagnostic: CodexDiagnostic = {
      severity: 'error',
      code: 'initialize-failed',
      message: 'Codex app-server exited before the initialize handshake could complete',
    }
    diagnostics.push(diagnostic)
    const error = normalizeUnavailableRuntimeCodex(diagnostics)
    return { ok: false, error, diagnostics: error.diagnostics }
  }
  let response: unknown
  let timeoutHandle: unknown
  const startupTimeout = new Promise<never>((_, reject) => {
    const callback = () => reject(new Error(`Codex app-server initialization exceeded ${options.startupTimeoutMs}ms`))
    timeoutHandle = options.clock
      ? options.clock.setTimeout(callback, options.startupTimeoutMs)
      : setTimeout(callback, options.startupTimeoutMs)
  })
  try {
    response = await Promise.race([
      transport.send({ id: 1, method: 'initialize', params: CODEX_INITIALIZE_PARAMS }),
      startupTimeout,
    ])
  } catch (cause) {
    const message = cause instanceof Error ? redactCodexCredentialString(cause.message) : 'initialize request rejected'
    const timedOut = message.includes('initialization exceeded')
    const diagnostic: CodexDiagnostic = {
      severity: 'error',
      code: timedOut ? 'startup-timeout' : 'initialize-failed',
      message,
    }
    diagnostics.push(diagnostic)
    const error = normalizeUnavailableRuntimeCodex(diagnostics)
    return { ok: false, error, diagnostics: error.diagnostics }
  } finally {
    if (timeoutHandle !== undefined) {
      if (options.clock) options.clock.clearTimeout(timeoutHandle)
      else clearTimeout(timeoutHandle as ReturnType<typeof setTimeout>)
    }
  }
  const result = initializeResult(response)
  if (result === null || (result.protocolVersion !== undefined && result.protocolVersion !== 'v2')) {
    const diagnostic: CodexDiagnostic = {
      severity: 'error',
      code: 'incompatible-runtime',
      message:
        result === null
          ? 'Codex initialize response did not match the locked v2 subset; refusing to admit the protocol'
          : `Codex app-server protocol version ${result.protocolVersion} is outside the locked v2 protocol`,
    }
    diagnostics.push(diagnostic)
    const error = normalizeIncompatibleRuntimeCodex(diagnostics)
    return { ok: false, error, diagnostics: error.diagnostics }
  }
  if (containsExperimentalApi(result)) {
    const diagnostic: CodexDiagnostic = {
      severity: 'error',
      code: 'experimental-api-rejected',
      message: 'Codex app-server advertised experimental APIs; refusing to admit the protocol',
    }
    diagnostics.push(diagnostic)
    const error = normalizeIncompatibleRuntimeCodex(diagnostics)
    return { ok: false, error, diagnostics: error.diagnostics }
  }
  if (result.codexHome !== options.managedCodexHome) {
    const diagnostic: CodexDiagnostic = {
      severity: 'error',
      code: 'incompatible-runtime',
      message: redactCodexCredentialString(
        `Codex app-server responded with codexHome=${result.codexHome}; expected the managed ${options.managedCodexHome}`,
      ),
    }
    diagnostics.push(diagnostic)
    const error = normalizeIncompatibleRuntimeCodex(diagnostics)
    return { ok: false, error, diagnostics: error.diagnostics }
  }
  // Emit `initialized` immediately after a successful `initialize`.
  // The notification has no id and no response; failures writing the
  // notification surface as diagnostics but do not block the
  // handshake, since the runtime's next request will fail fast
  // against an unprepared server.
  const notified = transport.notify({ method: 'initialized' })
  if (!notified) {
    diagnostics.push({
      severity: 'warning',
      code: 'initialized-notify-failed',
      message: 'Codex app-server did not accept the initialized notification',
    })
  }
  return {
    ok: true,
    value: {
      handshakeComplete: true,
      protocolVersion: result.protocolVersion ?? 'v2',
      codexHome: result.codexHome,
      userAgent: result.userAgent ?? null,
      diagnostics,
    },
    diagnostics,
  }
}

/**
 * Build a transport adapter over a {@link CodexServerHandle}. The
 * helper exists so {@link performCodexInitialization} can be unit-
 * tested with a fake transport and so the runtime can wire a real
 * handle to it.
 */
function initializeResult(value: unknown): CodexInitializeResult | null {
  if (isCodexInitializeResult(value)) return value.result
  if (!value || typeof value !== 'object') return null
  const envelope = {
    jsonrpc: '2.0' as const,
    id: 0,
    result: value,
  }
  return isCodexInitializeResult(envelope) ? envelope.result : null
}

export function codexInitializationTransportFromHandle(handle: {
  send<P, R>(request: { readonly method: string; readonly params?: P; readonly id: number }): Promise<R>
  notify?: (envelope: { readonly method: string; readonly params?: unknown }) => boolean
  hasExited?: () => boolean
}): CodexInitializationTransport {
  return {
    async send(request) {
      return await handle.send(request)
    },
    notify(notification) {
      if (!handle.notify) return false
      return handle.notify(notification)
    },
    hasExited() {
      return handle.hasExited?.() ?? false
    },
  }
}

function containsExperimentalApi(value: unknown): boolean {
  if (!value || typeof value !== 'object') return false
  if (Array.isArray(value)) return value.some(containsExperimentalApi)
  for (const [key, entry] of Object.entries(value)) {
    const normalized = key.toLowerCase()
    if (normalized.includes('experimental') || normalized === 'capabilities' || normalized === 'clientcapabilities') {
      return true
    }
    if (containsExperimentalApi(entry)) return true
  }
  return false
}
