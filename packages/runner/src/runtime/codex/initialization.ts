/**
 * Codex app-server initialization handshake.
 *
 * The handshake runs once per app-server generation, immediately
 * after the spawned child process is alive. It sends `initialize`
 * with no experimental client capabilities, requires the response's
 * `codexHome` to equal the managed state path, then sends
 * `initialized` to complete the handshake. No further request is
 * admitted before initialization completes; the readiness gate
 * consumes the result.
 *
 * The handshake is intentionally narrow: `initialize` carries no
 * capability negotiation, no `clientInfo` shape beyond the locked
 * compatibility test's narrow allowance, and no experimental fields.
 * Experimental APIs are rejected by the protocol-subset lock, not
 * by this module.
 */

import type { CodexDiagnostic, CodexResult } from './types.js'
import { isCodexInitializeResult, type CodexInitializeResult, type CodexJsonRpcMessage } from './protocol-types.js'
import { normalizeIncompatibleRuntimeCodex, normalizeUnavailableRuntimeCodex } from './errors.js'

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
  readonly clock?: { readonly now: () => number }
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
  try {
    response = await transport.send({ id: 1, method: 'initialize', params: {} })
  } catch (cause) {
    const message = cause instanceof Error ? cause.message : 'initialize request rejected'
    const diagnostic: CodexDiagnostic = {
      severity: 'error',
      code: 'initialize-failed',
      message,
    }
    diagnostics.push(diagnostic)
    const error = normalizeUnavailableRuntimeCodex(diagnostics)
    return { ok: false, error, diagnostics: error.diagnostics }
  }
  const result = initializeResult(response)
  if (result === null) {
    const diagnostic: CodexDiagnostic = {
      severity: 'error',
      code: 'incompatible-runtime',
      message: 'Codex initialize response did not match the locked v2 subset; refusing to admit the protocol',
    }
    diagnostics.push(diagnostic)
    const error = normalizeIncompatibleRuntimeCodex(diagnostics)
    return { ok: false, error, diagnostics: error.diagnostics }
  }
  if (result.codexHome !== options.managedCodexHome) {
    const diagnostic: CodexDiagnostic = {
      severity: 'error',
      code: 'incompatible-runtime',
      message: `Codex app-server responded with codexHome=${result.codexHome}; expected the managed ${options.managedCodexHome}`,
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
      protocolVersion: result.protocolVersion,
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
  subscribe?: (listener: (message: CodexJsonRpcMessage) => void) => () => void
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
      // The handle does not currently surface an exited state — the
      // send() rejection path is the canonical exit signal. We rely
      // on the runtime's attemptStart to fence the spawned handle
      // after spawn failures.
      return false
    },
  }
}
