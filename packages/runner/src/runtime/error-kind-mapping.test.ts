import { describe, expect, it } from 'vitest'
import type { RuntimeErrorKind } from './opencode/types.js'
import type { PiErrorKind } from './pi/types.js'
import {
  AGENT_RUNTIME_ERROR_CODES,
  mapOpenCodeErrorKind,
  mapPiErrorKind,
  normalizeAgentRuntimeErrorCode,
} from './error-kind-mapping.js'

const OPEN_CODE_CODES: ReadonlyArray<[RuntimeErrorKind, string]> = [
  ['invalid-input', 'invalid-input'],
  ['unavailable-runtime', 'unavailable-runtime'],
  ['missing-session', 'runtime-session-missing'],
  ['incompatible-runtime', 'incompatible-runtime'],
  ['unsupported-execution-configuration', 'unsupported-execution-configuration'],
  ['permission-required', 'permission-required'],
  ['deadline-exceeded', 'timeout'],
  ['interrupted', 'interrupted'],
  ['turn-failed', 'turn-failed'],
  ['generation-drain-timeout', 'generation-drain-timeout'],
]

const PI_CODES: ReadonlyArray<[PiErrorKind, string]> = [
  ['invalid-input', 'invalid-input'],
  ['unavailable-runtime', 'unavailable-runtime'],
  ['missing-session', 'runtime-session-missing'],
  ['incompatible-runtime', 'incompatible-runtime'],
  ['deadline-exceeded', 'timeout'],
  ['interrupted', 'interrupted'],
  ['turn-failed', 'turn-failed'],
  ['conflict', 'conflict'],
]

describe('Agent Runtime error catalog', () => {
  it('declares the exact non-platform result codes', () => {
    expect(AGENT_RUNTIME_ERROR_CODES).toEqual([
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
    ])
  })

  it.each(OPEN_CODE_CODES)('maps OpenCode source kind %s to %s', (source, expected) => {
    expect(mapOpenCodeErrorKind(source)).toBe(expected)
  })

  it.each(PI_CODES)('maps Pi source kind %s to %s', (source, expected) => {
    expect(mapPiErrorKind(source)).toBe(expected)
  })

  it('promotes provider quota diagnostics over a generic runtime kind', () => {
    expect(mapOpenCodeErrorKind('turn-failed', [{ code: 'provider-quota-exhausted' }])).toBe('provider-quota-exhausted')
    expect(mapPiErrorKind('turn-failed', [{ code: 'provider-quota-exhausted' }])).toBe('provider-quota-exhausted')
  })

  it('maps resolver and recorded source categories to result codes', () => {
    expect(normalizeAgentRuntimeErrorCode('skill_not_found')).toBe('skill-not-found')
    expect(normalizeAgentRuntimeErrorCode('unsupported_execution_configuration')).toBe(
      'unsupported-execution-configuration',
    )
  })

  it('preserves platform-owned codes and normalizes undeclared codes', () => {
    expect(normalizeAgentRuntimeErrorCode('invalid-input')).toBe('invalid-input')
    expect(normalizeAgentRuntimeErrorCode('timeout')).toBe('timeout')
    expect(normalizeAgentRuntimeErrorCode('unexpected-error')).toBe('unexpected-error')
    expect(normalizeAgentRuntimeErrorCode('not-declared')).toBe('unexpected-error')
  })
})
