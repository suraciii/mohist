import { describe, expect, it, vi } from 'vitest'
import { NETWORK_COMMAND_TIMEOUT_MS } from '../actions/git.js'
import { createRepositoryPreflightHandler } from './repository-preflight-handler.js'

describe('repository preflight handler', () => {
  it('runs a credentialed remote branch probe without returning output', async () => {
    const runCommand = vi.fn(async () => ({ exitCode: 0, stdout: 'oid\trefs/heads/main\n', stderr: '' }))
    const handler = createRepositoryPreflightHandler({ runCommand })

    await expect(handler({ gitUrl: 'https://example.test/repo.git', baseBranch: 'main' })).resolves.toEqual({
      exitCode: 0,
    })
    expect(runCommand).toHaveBeenCalledWith(
      'git',
      [
        '-c',
        'protocol.ext.allow=never',
        'ls-remote',
        '--heads',
        '--exit-code',
        '--',
        'https://example.test/repo.git',
        'refs/heads/main',
      ],
      '.',
      expect.any(AbortSignal),
      undefined,
      { timeoutMs: NETWORK_COMMAND_TIMEOUT_MS },
    )
  })

  it('classifies missing branches, timeouts, spawn failures, and unsafe transports', async () => {
    await expect(
      createRepositoryPreflightHandler({ runCommand: vi.fn(async () => ({ exitCode: 2, stdout: '', stderr: '' })) })({
        gitUrl: 'https://example.test/repo.git',
        baseBranch: 'missing',
      }),
    ).resolves.toEqual({ exitCode: 2 })
    await expect(
      createRepositoryPreflightHandler({
        runCommand: vi.fn(async () => ({ exitCode: 1, stdout: '', stderr: '', status: 'timeout' as const })),
      })({
        gitUrl: 'https://example.test/repo.git',
        baseBranch: 'main',
      }),
    ).resolves.toEqual({ exitCode: 124 })
    await expect(
      createRepositoryPreflightHandler({
        runCommand: vi.fn(async () => {
          throw new Error('spawn')
        }),
      })({
        gitUrl: 'https://example.test/repo.git',
        baseBranch: 'main',
      }),
    ).resolves.toEqual({ exitCode: -1 })
    const unsafeRunner = vi.fn()
    await expect(
      createRepositoryPreflightHandler({ runCommand: unsafeRunner })({ gitUrl: 'user::host/repo', baseBranch: 'main' }),
    ).resolves.toEqual({ exitCode: -1 })
    await expect(
      createRepositoryPreflightHandler({ runCommand: unsafeRunner })({
        gitUrl: 'evil://host/repo',
        baseBranch: 'main',
      }),
    ).resolves.toEqual({ exitCode: -1 })
    expect(unsafeRunner).not.toHaveBeenCalled()
  })
})
