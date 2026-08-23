/**
 * Map a runtime error kind to the failure category recorded by AgentJob and
 * follow-up terminal activity events.
 *
 * Keep these mappings in one place: AgentJob projection and follow-up
 * delivery must produce the same category for the same runtime error.
 */
export function mapPiErrorKind(kind: string): string {
  if (kind === 'deadline-exceeded') return 'timeout'
  if (kind === 'missing-session') return 'runtime-session-missing'
  return kind
}

/**
 * OpenCode error kinds are normally recorded verbatim. The capability
 * configuration error is the one existing AgentJob exception: its recorded
 * category uses the established underscore spelling.
 */
export function mapOpenCodeErrorKind(kind: string): string {
  if (kind === 'unsupported-execution-configuration') return 'unsupported_execution_configuration'
  return kind
}

export function mapRuntimeErrorKind(runtime: 'opencode' | 'pi', kind: string): string {
  return runtime === 'pi' ? mapPiErrorKind(kind) : mapOpenCodeErrorKind(kind)
}
