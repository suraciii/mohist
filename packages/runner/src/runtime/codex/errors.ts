/**
 * Codex app-server error normalization.
 *
 * Every app-server / provider failure maps to one of the small set of
 * Mohist `CodexErrorKind` values declared in `./types.ts`. Provider-
 * specific detail is carried only as `CodexDiagnostic` records, never
 * as an output field. The runtime never adds Codex protocol names to
 * domain models or creates a global Workflow error enum.
 *
 * Approval / permission / user-input / MCP elicitation / dynamic-tool
 * server requests are answered with the protocol-defined denial
 * response, the exact active Turn is interrupted, and `permission-
 * required` is returned only after the Turn reaches a terminal state.
 * Unconfirmed denial stays `unknown` and never creates a Workflow
 * Approval Point.
 */

import type { CodexDiagnostic, CodexError, CodexErrorKind } from './types.js'
import { redactCodexCredentialString, redactCodexCredentialValue } from './credential.js'

/**
 * Raw app-server error captured from a JSON-RPC error envelope or a
 * protocol-level failure. The runtime normalizes to a CodexError;
 * details are preserved in diagnostics, never in the kind field.
 */
export interface RawCodexError {
  readonly code?: number | string
  readonly message: string
  readonly method?: string
  readonly data?: unknown
}

const STRUCTURED_NOT_FOUND_PATTERN = /thread[_\s-]?not[_\s-]?found|thread[_\s-]?missing|no[_\s-]?such[_\s-]?thread/i
const PERMISSION_PATTERN =
  /permission|approval|approval[_\s-]?required|user[_\s-]?input|elicitation|dynamic[_\s-]?tool/i
const INCOMPATIBLE_PATTERN =
  /incompatible|protocol[_\s-]?version|unsupported[_\s-]?method|unknown[_\s-]?method|schema[_\s-]?mismatch/i
const INVALID_INPUT_PATTERN = /invalid[_\s-]?input|invalid[_\s-]?params|invalid[_\s-]?request|missing[_\s-]?field/i
const UNAVAILABLE_PATTERN =
  /unavailable|runtime[_\s-]?not[_\s-]?ready|not[_\s-]?ready|connection[_\s-]?lost|spawn[_\s-]?failed|startup[_\s-]?timeout/i
const DEADLINE_PATTERN = /deadline|timeout|timed[_\s-]?out/i
const INTERRUPTED_PATTERN = /interrupted|interrupt[_\s-]?requested/i

/**
 * Map a raw app-server / protocol error to its Mohist `CodexErrorKind`.
 * The defaults favour strict kinds; transports, timeouts, and
 * authentication failures stay `turn-failed` so the upper layers can
 * treat them as actionable terminal failures without falling back to
 * another Runtime.
 */
export function errorKindForCodex(raw: RawCodexError | string): CodexErrorKind {
  const message = typeof raw === 'string' ? raw : (raw.message ?? '')
  const code = typeof raw === 'string' ? undefined : raw.code
  const messageMatch = (pattern: RegExp) => pattern.test(message)

  if (messageMatch(STRUCTURED_NOT_FOUND_PATTERN)) return 'missing-session'
  if (messageMatch(PERMISSION_PATTERN)) return 'permission-required'
  if (messageMatch(INCOMPATIBLE_PATTERN)) return 'incompatible-runtime'
  if (messageMatch(INVALID_INPUT_PATTERN)) return 'invalid-input'
  if (messageMatch(UNAVAILABLE_PATTERN)) return 'unavailable-runtime'
  if (messageMatch(DEADLINE_PATTERN)) return 'deadline-exceeded'
  if (messageMatch(INTERRUPTED_PATTERN)) return 'interrupted'

  if (typeof code === 'number') {
    if (code === 404) return 'missing-session'
    if (code === 403) return 'permission-required'
    if (code === 400) return 'invalid-input'
    if (code === 408) return 'deadline-exceeded'
    if (code === 426) return 'incompatible-runtime'
  }

  return 'turn-failed'
}

/**
 * Build a `missing-session` `CodexError`. The runtime only reports this
 * after a structured `thread_not_found` from the bound managed state;
 * transport, timeout, authentication, permission, 5xx, and protocol
 * mismatches stay `unknown`.
 */
export function normalizeMissingSessionCodex(diagnostics: readonly CodexDiagnostic[] = []): CodexError {
  return {
    kind: 'missing-session',
    message:
      'No current Codex Thread is bound to this logical AgentSession — issue a Reset to establish a fresh Thread, then retry',
    diagnostics: [
      ...diagnostics,
      {
        severity: 'error',
        code: 'missing-session',
        message: 'Issue a Reset to establish a fresh Codex Thread, then retry',
      },
    ],
  }
}

/**
 * `unavailable-runtime` carries startup, transport, authentication, or
 * CLI availability gaps surfaced as actionable diagnostics. The
 * Runner does not fall back to another Runtime.
 */
export function normalizeUnavailableRuntimeCodex(diagnostics: readonly CodexDiagnostic[] = []): CodexError {
  return {
    kind: 'unavailable-runtime',
    message: 'Codex runtime is not available',
    diagnostics: [
      ...diagnostics,
      {
        severity: 'error',
        code: 'unavailable-runtime',
        message: 'Wait for readiness to re-pass, or investigate the readiness diagnostic for recovery steps',
      },
    ],
  }
}

/**
 * `invalid-input` carries caller-side validation failures. The runtime
 * never treats an invalid input as an opportunity to fall back.
 */
export function normalizeInvalidInputCodex(message: string, diagnostics: readonly CodexDiagnostic[] = []): CodexError {
  return {
    kind: 'invalid-input',
    message,
    diagnostics: [...diagnostics, { severity: 'error', code: 'invalid-input', message }],
  }
}

/**
 * `incompatible-runtime` covers unsupported protocol versions, schema
 * mismatches, and unknown methods. The runtime rejects these before
 * claiming work.
 */
export function normalizeIncompatibleRuntimeCodex(diagnostics: readonly CodexDiagnostic[] = []): CodexError {
  return {
    kind: 'incompatible-runtime',
    message: 'Installed Codex is incompatible with the locked v2 protocol subset',
    diagnostics: [
      ...diagnostics,
      {
        severity: 'error',
        code: 'incompatible-runtime',
        message: 'Update Codex to a version within the supported range and verify the locked compatibility smoke',
      },
    ],
  }
}

/**
 * `turn-failed` is the default terminal failure kind. Transport,
 * provider, and unclassified app-server failures land here unless an
 * existing stable kind matches.
 */
export function normalizeTurnFailedCodex(
  raw: RawCodexError | string,
  diagnostics: readonly CodexDiagnostic[] = [],
): CodexError {
  const message = redactCodexCredentialString(typeof raw === 'string' ? raw : raw.message || 'Codex turn failed')
  return {
    kind: 'turn-failed',
    message,
    diagnostics: [
      ...diagnostics,
      {
        severity: 'error',
        code: 'turn-failed',
        message,
        details:
          typeof raw === 'string'
            ? undefined
            : (redactCodexCredentialValue({ code: raw.code, data: raw.data, method: raw.method }) as Record<
                string,
                unknown
              >),
      },
    ],
  }
}

/**
 * `interrupted` confirms interruption through the matching terminal
 * `turn/completed` event for the exact active Turn. Used by cancel and
 * closeout paths.
 */
export function normalizeInterruptedCodex(diagnostics: readonly CodexDiagnostic[] = []): CodexError {
  return {
    kind: 'interrupted',
    message: 'Codex turn was interrupted before completion',
    diagnostics: [
      ...diagnostics,
      {
        severity: 'info',
        code: 'interrupted',
        message: 'The turn was aborted by a deadline, an explicit cancel, or an approval rejection',
      },
    ],
  }
}

/**
 * `deadline-exceeded` is fixed at the deadline before
 * `turn/interrupt`. A late completion does not reverse the fixed
 * result.
 */
export function normalizeDeadlineExceededCodex(
  deadlineMs: number,
  diagnostics: readonly CodexDiagnostic[] = [],
): CodexError {
  const seconds = deadlineMs / 1000
  return {
    kind: 'deadline-exceeded',
    message: `Codex turn timed out after ${seconds}s`,
    diagnostics: [
      ...diagnostics,
      {
        severity: 'error',
        code: 'deadline-exceeded',
        message: `The runner deadline expired after ${seconds}s; the active Turn was interrupted`,
      },
    ],
  }
}

/**
 * `permission-required` is returned only after the Turn reaches a
 * terminal state following a server-initiated approval / permission /
 * user-input rejection. Unconfirmed denial stays `unknown` and never
 * creates a Workflow Approval Point.
 */
export function normalizePermissionRequiredCodex(diagnostics: readonly CodexDiagnostic[] = []): CodexError {
  return {
    kind: 'permission-required',
    message: 'Codex server-initiated request could not be answered by the headless runtime',
    diagnostics: [
      ...diagnostics,
      {
        severity: 'error',
        code: 'permission-required',
        message:
          'Restore Codex server connectivity, or run with an interactive approval product; the runner fails closed',
      },
    ],
  }
}

/**
 * `unknown` is preserved when the runtime cannot prove an effect.
 * Process loss, lost `turn/start` responses, unconfirmed stop, and
 * Runner-generation loss all land here. The runtime never replays
 * an unknown input.
 */
export function normalizeUnknownCodex(
  raw: RawCodexError | string,
  diagnostics: readonly CodexDiagnostic[] = [],
): CodexError {
  const message = redactCodexCredentialString(typeof raw === 'string' ? raw : raw.message || 'Codex effect is unknown')
  return {
    kind: 'unknown',
    message,
    diagnostics: [
      ...diagnostics,
      {
        severity: 'error',
        code: 'unknown',
        message,
        details:
          typeof raw === 'string'
            ? undefined
            : (redactCodexCredentialValue({ code: raw.code, data: raw.data, method: raw.method }) as Record<
                string,
                unknown
              >),
      },
    ],
  }
}

/**
 * `unsupported-execution-configuration` for configuration values the
 * Codex v1 surface cannot honour (e.g. `variant`).
 */
export const UNSUPPORTED_EXECUTION_CONFIGURATION_CATEGORY = 'unsupported_execution_configuration'

export function normalizeUnsupportedExecutionConfigurationCodex(
  message: string,
  diagnostics: readonly CodexDiagnostic[] = [],
): CodexError {
  return {
    kind: 'unsupported-execution-configuration',
    message,
    diagnostics: [
      ...diagnostics,
      {
        severity: 'error',
        code: UNSUPPORTED_EXECUTION_CONFIGURATION_CATEGORY,
        message,
      },
    ],
  }
}
