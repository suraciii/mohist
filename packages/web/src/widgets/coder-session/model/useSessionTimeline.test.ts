import { describe, it, expect } from 'vitest'
import { deriveToolCallTitle } from './useSessionTimeline'

describe('deriveToolCallTitle', () => {
  it('returns title when title differs from toolName', () => {
    expect(deriveToolCallTitle('read', 'server.ts', '{}')).toBe('server.ts')
  })

  it('derives filename from JSON file_path for read tool', () => {
    expect(
      deriveToolCallTitle('read', 'read', '{"file_path":"packages/server/src/Mohist.Server/Program.cs"}')
    ).toBe('Program.cs')
  })

  it('derives command from JSON command for bash tool', () => {
    expect(
      deriveToolCallTitle('bash', 'bash', '{"command":"npm run build"}')
    ).toBe('npm run build')
  })

  it('returns rawInput string when JSON parse fails', () => {
    expect(deriveToolCallTitle('bash', 'bash', 'npm test')).toBe('npm test')
  })

  it('returns toolName when rawInput is null', () => {
    expect(deriveToolCallTitle('unknown', 'unknown', null as unknown as string)).toBe('unknown')
  })

  it('returns toolName when rawInput is undefined', () => {
    expect(deriveToolCallTitle('read', 'read', undefined)).toBe('read')
  })

  it('truncates long bash commands', () => {
    const longCmd = 'a'.repeat(100)
    expect(deriveToolCallTitle('bash', 'bash', `{"command":"${longCmd}"}`)).toBe(
      'a'.repeat(57) + '...'
    )
  })

  it('derives pattern from glob tool', () => {
    expect(deriveToolCallTitle('glob', 'glob', '{"pattern":"**/*.ts"}')).toBe('**/*.ts')
  })

  it('handles filePath variant for read tool', () => {
    expect(
      deriveToolCallTitle('read_file', 'read_file', '{"filePath":"src/main.ts"}')
    ).toBe('main.ts')
  })
})
