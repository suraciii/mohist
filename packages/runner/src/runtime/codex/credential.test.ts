import { describe, expect, it } from 'vitest'
import {
  codexCredentialSecretDigest,
  CODEX_CREDENTIAL_MASK_PLACEHOLDER,
  maskCodexCredentialString,
  redactCodexCredentialDiagnostic,
  redactCodexCredentialEnvelope,
  redactCodexCredentialString,
  redactCodexCredentialStringWithIndex,
} from './credential.js'

describe('Codex credential redaction', () => {
  it('masks bearer tokens', () => {
    expect(maskCodexCredentialString('Authorization: Bearer sk-abcdefghijklmnop1234')).toContain('Bearer ***')
  })

  it('masks basic auth headers', () => {
    expect(maskCodexCredentialString('Authorization: Basic YWxhZGRpbjpvcGVuc2VzYW1l')).toContain('Basic ***')
  })

  it('masks OpenAI / vendor `sk-` keys', () => {
    expect(maskCodexCredentialString('value: sk-prod1234567890abcdefghij')).toContain('sk-***')
  })

  it('masks GitHub PAT prefixes', () => {
    expect(maskCodexCredentialString('token=ghp_abcdefghijklmnopqrstuvwxyz0123456789')).toContain('ghp_***')
  })

  it('masks URL user-info credentials', () => {
    expect(maskCodexCredentialString('remote = https://alice:hunter2@github.com/foo/bar')).toContain('***@')
  })

  it('masks `apiKey` env-style declarations', () => {
    expect(maskCodexCredentialString('apiKey="abcdefghijklmnop"')).toContain('"***"')
  })

  it('masks JSON `session` string values', () => {
    const masked = maskCodexCredentialString('{"session":"abcdefghijklmnop"}')
    expect(masked).toContain('"session":"***"')
  })

  it('returns the input unchanged when no pattern matches', () => {
    expect(maskCodexCredentialString('hello world')).toBe('hello world')
  })

  it('round-trips a non-string through redactCodexCredentialString', () => {
    expect(redactCodexCredentialString(undefined)).toBe('')
    expect(redactCodexCredentialString(null)).toBe('')
    expect(redactCodexCredentialString(42)).toBe('42')
  })

  it('redacts known sensitive keys in a structured envelope', () => {
    const envelope = {
      jsonrpc: '2.0',
      method: 'initialize',
      params: {
        clientInfo: { name: 'mohist', version: '0.1.0' },
        apiKey: 'sk-prod1234567890abcdefghij',
        authorization: 'Bearer abcdefghij',
        password: 'hunter2',
      },
    }
    const redacted = JSON.parse(redactCodexCredentialEnvelope(envelope))
    expect(redacted.params.apiKey).toBe(CODEX_CREDENTIAL_MASK_PLACEHOLDER)
    expect(redacted.params.authorization).toBe(CODEX_CREDENTIAL_MASK_PLACEHOLDER)
    expect(redacted.params.password).toBe(CODEX_CREDENTIAL_MASK_PLACEHOLDER)
    expect(redacted.params.clientInfo).toEqual({ name: 'mohist', version: '0.1.0' })
  })

  it('redacts credentials nested inside arrays', () => {
    const envelope = {
      items: [
        { id: 'a', token: 'abcdefghij' },
        { id: 'b', safe: 'value' },
      ],
    }
    const redacted = JSON.parse(redactCodexCredentialEnvelope(envelope))
    expect(redacted.items[0].token).toBe(CODEX_CREDENTIAL_MASK_PLACEHOLDER)
    expect(redacted.items[1].safe).toBe('value')
  })

  it('redacts the message of a diagnostic and any credential inside details', () => {
    const diagnostic = {
      message: 'initialize failed with token=sk-prod1234567890abcdefghij',
      details: { apiKey: 'sk-prod1234567890abcdefghij', sdk: 'codex' },
    }
    const masked = redactCodexCredentialDiagnostic(diagnostic)
    expect(masked.message).not.toContain('sk-prod1234567890abcdefghij')
    expect(masked.message).toContain('sk-***')
    expect((masked.details as { apiKey: string }).apiKey).toBe(CODEX_CREDENTIAL_MASK_PLACEHOLDER)
  })

  it('redacts via a precomputed secret index when supplied', () => {
    const secret = 'shared-runtime-secret-XYZ'
    const digest = codexCredentialSecretDigest(secret)
    const index = [{ length: secret.length, digest }]
    const text = `payload:${secret}:trailer`
    const masked = redactCodexCredentialStringWithIndex(text, index)
    expect(masked).toBe('payload:***:trailer')
  })

  it('does not mutate the source envelope', () => {
    const envelope = {
      jsonrpc: '2.0',
      apiKey: 'sk-prod1234567890abcdefghij',
    }
    const source = JSON.stringify(envelope)
    redactCodexCredentialEnvelope(envelope)
    expect(JSON.stringify(envelope)).toBe(source)
  })
})
