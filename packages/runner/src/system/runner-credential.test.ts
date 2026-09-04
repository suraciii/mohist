import { describe, expect, it as vitestIt } from 'vitest'

import {
  loadRunnerCredential,
  registerWithEnrollmentToken,
  resolveRunnerCredential,
  runnerCredentialPath,
  runnerEnrollmentTokenPath,
  writeRunnerCredential,
} from './runner-credential.js'
import { withFakeTransport, type FakeTransport } from '../../tests/support/fake-transport.js'

const serverUrl = 'https://runner.test'
const signal = new AbortController().signal

type StoredCredential = { content: string; mode?: number }

interface CredentialTestContext {
  readonly files: Map<string, StoredCredential>
  readonly fetch: FakeTransport['fetch']
}

function it(name: string, body: (context: CredentialTestContext) => Promise<void> | void): void {
  vitestIt(name, async () => {
    const files = new Map<string, StoredCredential>()
    const runnerCredentialFileSystem = {
      mkdirSync(directory: string) {
        files.set(directory, { content: '' })
      },
      readFileSync(path: string, _encoding: 'utf8') {
        const entry = files.get(path)
        if (!entry) {
          const error = new Error(`ENOENT: no such file or directory, open '${path}'`) as NodeJS.ErrnoException
          error.code = 'ENOENT'
          throw error
        }
        return entry.content
      },
      writeFileSync(path: string, content: string, options?: { mode?: number }) {
        files.set(path, { content, mode: options?.mode })
      },
      unlinkSync(path: string) {
        if (!files.delete(path)) {
          const error = new Error(`ENOENT: no such file or directory, unlink '${path}'`) as NodeJS.ErrnoException
          error.code = 'ENOENT'
          throw error
        }
      },
    }
    await withFakeTransport(async (transport) => await body({ files, fetch: transport.fetch }), {
      runnerCredentialFileSystem,
    })
  })
}

describe('runner credential file', () => {
  it('loads the persisted credential', ({ files }) => {
    files.set(runnerCredentialPath('/runner'), { content: 'moh_runner_abc\n' })

    expect(loadRunnerCredential('/runner')).toBe('moh_runner_abc')
  })

  it('returns null when the file does not exist', () => {
    expect(loadRunnerCredential('/runner')).toBeNull()
  })

  it('writes owner-only (0600) with a trailing newline', ({ files }) => {
    writeRunnerCredential('/runner', 'moh_runner_abc')

    expect(files.has('/runner')).toBe(true)
    expect(files.get(runnerCredentialPath('/runner'))).toEqual({ content: 'moh_runner_abc\n', mode: 0o600 })
  })
})

describe('registerWithEnrollmentToken', () => {
  it('posts the enrollment token and returns the machine credential', async ({ fetch }) => {
    fetch.mockResolvedValue(
      new Response(JSON.stringify({ success: true, data: { token: 'moh_runner_abc', runnerId: 'runner-1' } }), {
        status: 201,
        headers: { 'content-type': 'application/json' },
      }),
    )

    const credential = await registerWithEnrollmentToken(serverUrl, 'runner-1', 'host-1', 'moh_enroll_xyz', signal)

    expect(credential).toBe('moh_runner_abc')
    const [url, init] = fetch.mock.calls[0]!
    expect(url).toBe('https://runner.test/api/runners/register')
    expect(init?.method).toBe('POST')
    expect(JSON.parse(init?.body as string)).toEqual({
      token: 'moh_enroll_xyz',
      runnerId: 'runner-1',
      hostname: 'host-1',
    })
  })

  it('throws when the server rejects the token', async ({ fetch }) => {
    fetch.mockResolvedValue(new Response('expired', { status: 401 }))

    await expect(
      registerWithEnrollmentToken(serverUrl, 'runner-1', 'host-1', 'moh_enroll_xyz', signal),
    ).rejects.toThrow(/registration with enrollment token failed: 401/)
  })

  it('throws on a malformed response', async ({ fetch }) => {
    fetch.mockResolvedValue(new Response(JSON.stringify({ success: true }), { status: 201 }))

    await expect(
      registerWithEnrollmentToken(serverUrl, 'runner-1', 'host-1', 'moh_enroll_xyz', signal),
    ).rejects.toThrow(/malformed/)
  })
})

describe('resolveRunnerCredential', () => {
  it('uses the persisted credential without any registration call', async ({ files, fetch }) => {
    files.set(runnerCredentialPath('/runner'), { content: 'moh_runner_abc\n' })

    const credential = await resolveRunnerCredential({
      serverUrl,
      runnerId: 'runner-1',
      runnerRoot: '/runner',
      hostname: 'host-1',
      enrollmentToken: 'moh_enroll_xyz',
      signal,
    })

    expect(credential).toBe('moh_runner_abc')
    expect(fetch).not.toHaveBeenCalled()
  })

  it('registers through the enrollment token and persists the credential', async ({ files, fetch }) => {
    fetch.mockResolvedValue(
      new Response(JSON.stringify({ success: true, data: { token: 'moh_runner_new' } }), { status: 201 }),
    )

    const credential = await resolveRunnerCredential({
      serverUrl,
      runnerId: 'runner-1',
      runnerRoot: '/runner',
      hostname: 'host-1',
      enrollmentToken: 'moh_enroll_xyz',
      signal,
    })

    expect(credential).toBe('moh_runner_new')
    expect(files.get(runnerCredentialPath('/runner'))).toEqual({ content: 'moh_runner_new\n', mode: 0o600 })
  })

  it('consumes a persisted enrollment token after the machine credential is durable', async ({ files, fetch }) => {
    files.set(runnerEnrollmentTokenPath('/runner'), { content: 'moh_enroll_file\n', mode: 0o600 })
    fetch.mockResolvedValue(
      new Response(JSON.stringify({ success: true, data: { token: 'moh_runner_new' } }), { status: 201 }),
    )

    const credential = await resolveRunnerCredential({
      serverUrl,
      runnerId: 'runner-1',
      runnerRoot: '/runner',
      hostname: 'host-1',
      signal,
    })

    expect(credential).toBe('moh_runner_new')
    expect(JSON.parse(fetch.mock.calls[0]![1]?.body as string).token).toBe('moh_enroll_file')
    expect(files.get(runnerCredentialPath('/runner'))).toEqual({ content: 'moh_runner_new\n', mode: 0o600 })
    expect(files.has(runnerEnrollmentTokenPath('/runner'))).toBe(false)
  })

  it('keeps a persisted enrollment token when registration fails', async ({ files, fetch }) => {
    files.set(runnerEnrollmentTokenPath('/runner'), { content: 'moh_enroll_retry\n', mode: 0o600 })
    fetch.mockResolvedValue(new Response('unavailable', { status: 503 }))

    await expect(
      resolveRunnerCredential({
        serverUrl,
        runnerId: 'runner-1',
        runnerRoot: '/runner',
        hostname: 'host-1',
        signal,
      }),
    ).rejects.toThrow(/registration with enrollment token failed: 503/)

    expect(files.get(runnerEnrollmentTokenPath('/runner'))?.content).toBe('moh_enroll_retry\n')
    expect(files.has(runnerCredentialPath('/runner'))).toBe(false)
  })

  it('removes a stale enrollment token when the machine credential already exists', async ({ files, fetch }) => {
    files.set(runnerCredentialPath('/runner'), { content: 'moh_runner_abc\n', mode: 0o600 })
    files.set(runnerEnrollmentTokenPath('/runner'), { content: 'moh_enroll_stale\n', mode: 0o600 })

    const credential = await resolveRunnerCredential({
      serverUrl,
      runnerId: 'runner-1',
      runnerRoot: '/runner',
      hostname: 'host-1',
      signal,
    })

    expect(credential).toBe('moh_runner_abc')
    expect(fetch).not.toHaveBeenCalled()
    expect(files.has(runnerEnrollmentTokenPath('/runner'))).toBe(false)
  })

  it('returns null when there is no credential and no enrollment token', async ({ fetch }) => {
    const credential = await resolveRunnerCredential({
      serverUrl,
      runnerId: 'runner-1',
      runnerRoot: '/runner',
      hostname: 'host-1',
      signal,
    })

    expect(credential).toBeNull()
    expect(fetch).not.toHaveBeenCalled()
  })
})
