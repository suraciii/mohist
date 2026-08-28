import { NON_RECOVERABLE_PROVIDER_ERROR_CODE } from '../core/types.js'

export const AGENT_RUNTIME_ERROR_CODES = [
  'attachment-delivery-failed',
  'conflict',
  'generation-drain-timeout',
  'incompatible-execution-configuration',
  'incompatible-runtime',
  'interrupted',
  'invalid-dispatch',
  'manager-credential-expired',
  'permission-required',
  'provider-quota-exhausted',
  'runtime-session-missing',
  'runtime-unavailable',
  'session-binding-failed',
  'skill-not-found',
  'turn-failed',
  'unavailable-runtime',
  'unsupported-execution-configuration',
  'workspace-home-claimed',
  'workspace-materialization-failed',
] as const

const DECLARED_AGENT_RUNTIME_ERROR_CODES = new Set<string>(AGENT_RUNTIME_ERROR_CODES)
const PLATFORM_ERROR_CODES = new Set(['invalid-input', 'unexpected-error', 'timeout'])

/**
 * Normalize runtime and resolver categories at the Agent-to-Runner result
 * boundary. Diagnostics keep their source spelling; WorkItemResult error codes
 * use the declared kebab-case catalog or a platform-owned code.
 */
export function normalizeAgentRuntimeErrorCode(
  sourceCode: string,
  diagnostics: readonly { readonly code: string }[] = [],
): string {
  if (diagnostics.some((entry) => entry.code === NON_RECOVERABLE_PROVIDER_ERROR_CODE)) {
    return NON_RECOVERABLE_PROVIDER_ERROR_CODE
  }

  const code = sourceCategoryCode(sourceCode)
  if (PLATFORM_ERROR_CODES.has(code) || DECLARED_AGENT_RUNTIME_ERROR_CODES.has(code)) return code
  return 'unexpected-error'
}

export function mapPiErrorKind(kind: string, diagnostics: readonly { readonly code: string }[] = []): string {
  return normalizeAgentRuntimeErrorCode(kind, diagnostics)
}

export function mapOpenCodeErrorKind(kind: string, diagnostics: readonly { readonly code: string }[] = []): string {
  return normalizeAgentRuntimeErrorCode(kind, diagnostics)
}

export function mapRuntimeErrorKind(
  _runtime: 'opencode' | 'pi',
  kind: string,
  diagnostics: readonly { readonly code: string }[] = [],
): string {
  return normalizeAgentRuntimeErrorCode(kind, diagnostics)
}

function sourceCategoryCode(code: string): string {
  if (code === 'deadline-exceeded') return 'timeout'
  if (code === 'missing-session') return 'runtime-session-missing'
  if (code === 'skill_not_found') return 'skill-not-found'
  if (code === 'unsupported_execution_configuration' || code === 'unsupported-execution-configuration') {
    return 'unsupported-execution-configuration'
  }
  return code
}
