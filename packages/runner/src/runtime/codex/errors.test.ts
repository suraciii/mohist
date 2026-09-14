import { describe, expect, it } from 'vitest'
import {
  errorKindForCodex,
  normalizeDeadlineExceededCodex,
  normalizeIncompatibleRuntimeCodex,
  normalizeInterruptedCodex,
  normalizeInvalidInputCodex,
  normalizeMissingSessionCodex,
  normalizePermissionRequiredCodex,
  normalizeTurnFailedCodex,
  normalizeUnavailableRuntimeCodex,
  normalizeUnknownCodex,
  normalizeUnsupportedExecutionConfigurationCodex,
} from './errors.js'

describe('Codex runtime error normalization', () => {
  it('maps structured thread_not_found to missing-session', () => {
    expect(errorKindForCodex({ message: 'thread_not_found: Thread does not exist', code: 'thread_not_found' })).toBe(
      'missing-session',
    )
  })

  it('maps server-initiated approval / permission requests to permission-required', () => {
    expect(errorKindForCodex({ message: 'approval required for shell command' })).toBe('permission-required')
    expect(errorKindForCodex({ message: 'permission denied for tool call' })).toBe('permission-required')
    expect(errorKindForCodex({ message: 'server requests user_input' })).toBe('permission-required')
  })

  it('maps protocol / schema mismatches to incompatible-runtime', () => {
    expect(errorKindForCodex({ message: 'unsupported method: turn/foo' })).toBe('incompatible-runtime')
    expect(errorKindForCodex({ message: 'protocol version mismatch' })).toBe('incompatible-runtime')
    expect(errorKindForCodex({ message: 'schema mismatch on threadId' })).toBe('incompatible-runtime')
  })

  it('maps validation failures to invalid-input', () => {
    expect(errorKindForCodex({ message: 'invalid input: model missing' })).toBe('invalid-input')
    expect(errorKindForCodex({ message: 'invalid params: threadId required' })).toBe('invalid-input')
  })

  it('maps transport / startup / authentication gaps to unavailable-runtime', () => {
    expect(errorKindForCodex({ message: 'connection lost during turn/start' })).toBe('unavailable-runtime')
    expect(errorKindForCodex({ message: 'runtime not ready' })).toBe('unavailable-runtime')
    expect(errorKindForCodex({ message: 'spawn failed: codex binary not found' })).toBe('unavailable-runtime')
  })

  it('maps timeouts to deadline-exceeded', () => {
    expect(errorKindForCodex({ message: 'deadline exceeded' })).toBe('deadline-exceeded')
    expect(errorKindForCodex({ message: 'turn timed out' })).toBe('deadline-exceeded')
    expect(errorKindForCodex({ code: 408, message: 'request timeout' })).toBe('deadline-exceeded')
  })

  it('maps interruption confirmations to interrupted', () => {
    expect(errorKindForCodex({ message: 'turn interrupted' })).toBe('interrupted')
    expect(errorKindForCodex({ message: 'interrupt requested' })).toBe('interrupted')
  })

  it('falls back to turn-failed when nothing else matches', () => {
    expect(errorKindForCodex('provider exhaustion')).toBe('turn-failed')
    expect(errorKindForCodex({ code: 500, message: 'upstream provider 500' })).toBe('turn-failed')
    expect(errorKindForCodex({ code: 418, message: 'I am a teapot' })).toBe('turn-failed')
  })

  it('produces a structured CodexError from each normalizer', () => {
    expect(normalizeMissingSessionCodex()).toMatchObject({
      kind: 'missing-session',
      diagnostics: [
        {
          severity: 'error',
          code: 'missing-session',
          message: expect.stringContaining('Reset'),
        },
      ],
    })
    {
      const err = normalizeUnavailableRuntimeCodex()
      expect(err.kind).toBe('unavailable-runtime')
      expect(err.diagnostics.some((d) => d.severity === 'error' && d.code === 'unavailable-runtime')).toBe(true)
    }
    expect(normalizeInvalidInputCodex('options.variant is not supported by Codex v1')).toMatchObject({
      kind: 'invalid-input',
      message: 'options.variant is not supported by Codex v1',
      diagnostics: [
        {
          severity: 'error',
          code: 'invalid-input',
          message: 'options.variant is not supported by Codex v1',
        },
      ],
    })
    expect(normalizeIncompatibleRuntimeCodex()).toMatchObject({
      kind: 'incompatible-runtime',
      diagnostics: expect.arrayContaining([
        expect.objectContaining({ severity: 'error', code: 'incompatible-runtime' }),
      ]),
    })
    expect(normalizeTurnFailedCodex({ message: 'provider exhaustion', code: 'quota' })).toMatchObject({
      kind: 'turn-failed',
      diagnostics: [
        {
          severity: 'error',
          code: 'turn-failed',
          details: { code: 'quota', data: undefined, method: undefined },
        },
      ],
    })
    expect(normalizeTurnFailedCodex('transport failed')).toMatchObject({
      kind: 'turn-failed',
      message: 'transport failed',
      diagnostics: [
        {
          severity: 'error',
          code: 'turn-failed',
          details: undefined,
        },
      ],
    })
    expect(normalizeInterruptedCodex()).toMatchObject({
      kind: 'interrupted',
      diagnostics: expect.arrayContaining([expect.objectContaining({ severity: 'info', code: 'interrupted' })]),
    })
    expect(normalizeDeadlineExceededCodex(120_000)).toMatchObject({
      kind: 'deadline-exceeded',
      message: 'Codex turn timed out after 120s',
      diagnostics: [
        {
          severity: 'error',
          code: 'deadline-exceeded',
          message: 'The runner deadline expired after 120s; the active Turn was interrupted',
        },
      ],
    })
    expect(normalizePermissionRequiredCodex()).toMatchObject({
      kind: 'permission-required',
      diagnostics: expect.arrayContaining([
        expect.objectContaining({ severity: 'error', code: 'permission-required' }),
      ]),
    })
    expect(normalizeUnknownCodex({ message: 'lost turn/start response' })).toMatchObject({
      kind: 'unknown',
      diagnostics: [
        {
          severity: 'error',
          code: 'unknown',
          message: 'lost turn/start response',
        },
      ],
    })
    expect(
      normalizeUnsupportedExecutionConfigurationCodex(
        'options.reasoningEffort unknown-effort is not published by the Codex catalog',
      ),
    ).toMatchObject({
      kind: 'unsupported-execution-configuration',
      diagnostics: [
        {
          severity: 'error',
          code: 'unsupported-execution-configuration',
        },
      ],
    })
  })

  it('appends caller-supplied diagnostics without mutating them', () => {
    const supplied = [{ severity: 'info' as const, code: 'thread-not-found-source', message: 'upstream says gone' }]
    const error = normalizeMissingSessionCodex(supplied)
    expect(error.diagnostics[0]).toEqual(supplied[0])
    expect(error.diagnostics[1]).toMatchObject({ severity: 'error', code: 'missing-session' })
  })
})
