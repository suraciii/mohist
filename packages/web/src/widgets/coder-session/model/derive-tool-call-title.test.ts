import { describe, expect, it } from 'vitest'
import { deriveToolCallTitle } from './session-timeline-reducer'

describe('deriveToolCallTitle', () => {
  it('returns a distinct title', () => {
    expect(deriveToolCallTitle('read', 'server.ts', '{}')).toBe('server.ts')
  })

  it('derives a filename from read input', () => {
    expect(deriveToolCallTitle('read', 'read', '{"file_path":"packages/server/src/Mohist.Server/Program.cs"}')).toBe('Program.cs')
  })

  it('derives a bash command', () => {
    expect(deriveToolCallTitle('bash', 'bash', '{"command":"npm run build"}')).toBe('npm run build')
  })

  it('falls back for malformed or absent input', () => {
    expect(deriveToolCallTitle('bash', 'bash', 'npm test')).toBe('npm test')
    expect(deriveToolCallTitle('unknown', 'unknown', null as unknown as string)).toBe('unknown')
    expect(deriveToolCallTitle('read', 'read', undefined)).toBe('read')
  })

  it('truncates long commands and recognizes tool-specific input fields', () => {
    const longCommand = 'a'.repeat(100)
    expect(deriveToolCallTitle('bash', 'bash', `{"command":"${longCommand}"}`)).toBe('a'.repeat(57) + '...')
    expect(deriveToolCallTitle('glob', 'glob', '{"pattern":"**/*.ts"}')).toBe('**/*.ts')
    expect(deriveToolCallTitle('read_file', 'read_file', '{"filePath":"src/main.ts"}')).toBe('main.ts')
  })
})
