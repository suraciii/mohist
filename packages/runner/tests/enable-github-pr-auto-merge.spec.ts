import { describe, expect, it, vi } from 'vitest'
import { enableGitHubPrAutoMergeAction } from '../src/actions/enable-github-pr-auto-merge.js'
import { classifyPrChecks } from '../src/actions/github-pr-checks.js'
import { withRunnerResources, type RunnerCommandRunner, type RunnerResourceContext } from '../src/system/filesystem.js'

function result(stdout: string, exitCode = 0, stderr = '') {
  return { stdout, stderr, exitCode }
}
function view(overrides: Record<string, unknown> = {}) {
  return JSON.stringify({
    state: 'OPEN',
    url: 'https://github.com/o/r/pull/42',
    title: 'Pull request title',
    mergeStateStatus: 'CLEAN',
    mergeCommit: null,
    autoMergeRequest: null,
    statusCheckRollup: [],
    ...overrides,
  })
}
function host(signal = new AbortController().signal): any {
  return { workDir: '/tmp', signal, variables: { issue: { title: 'Ship it' } } }
}
const inputs = { repositoryUrl: 'https://github.com/o/r.git', prNumber: 42, method: 'squash', subject: 'Ship it' }

function resources(
  gh: RunnerCommandRunner,
  overrides: NonNullable<RunnerResourceContext['githubPrChecksTiming']> = {},
): RunnerResourceContext {
  return {
    githubPrGhRunner: gh,
    githubPrChecksTiming: { pollIntervalMs: 1, autoMergeWaitMs: 50, ...overrides },
  }
}

describe('enable auto merge', () => {
  it('registers once and completes when GitHub reports merged', async () => {
    const gh = vi
      .fn()
      .mockResolvedValueOnce(result('gh version'))
      .mockResolvedValueOnce(result('auth ok'))
      .mockResolvedValueOnce(result(view()))
      .mockResolvedValueOnce(result('enabled'))
      .mockResolvedValueOnce(result(view({ state: 'MERGED', mergeCommit: { oid: 'sha' }, autoMergeRequest: {} })))
    const out: any = await withRunnerResources(resources(gh), () =>
      enableGitHubPrAutoMergeAction(inputs as any, host()),
    )
    expect(out.output).toMatchObject({ enabled: true, mergeCommitSha: 'sha' })
    expect(out.output.output).toContain('enabled')
    expect(gh.mock.calls.filter((call: any) => call[1].includes('--auto'))).toHaveLength(1)
  })

  it('uses the bounded PR title when no explicit subject is provided', async () => {
    const gh = vi
      .fn()
      .mockResolvedValueOnce(result('gh version'))
      .mockResolvedValueOnce(result('auth ok'))
      .mockResolvedValueOnce(result(view({ title: 'Bounded PR title' })))
      .mockResolvedValueOnce(result('enabled'))
      .mockResolvedValueOnce(result(view({ state: 'MERGED', mergeCommit: { oid: 'sha' }, autoMergeRequest: {} })))
    await withRunnerResources(resources(gh), () =>
      enableGitHubPrAutoMergeAction({ ...inputs, subject: undefined } as any, host()),
    )
    const registration = gh.mock.calls.find((call: any) => call[1].includes('--auto'))!
    expect(registration[1]).toContain('Bounded PR title')
    expect(gh.mock.calls.every((call: any) => call[0] === 'gh')).toBe(true)
  })

  it('gives an explicit subject precedence over the bounded PR title', async () => {
    const gh = vi
      .fn()
      .mockResolvedValueOnce(result('gh version'))
      .mockResolvedValueOnce(result('auth ok'))
      .mockResolvedValueOnce(result(view({ title: 'Bounded PR title' })))
      .mockResolvedValueOnce(result('enabled'))
      .mockResolvedValueOnce(result(view({ state: 'MERGED', mergeCommit: { oid: 'sha' }, autoMergeRequest: {} })))
    await withRunnerResources(resources(gh), () =>
      enableGitHubPrAutoMergeAction({ ...inputs, subject: 'Explicit subject' } as any, host()),
    )
    const registration = gh.mock.calls.find((call: any) => call[1].includes('--auto'))!
    expect(registration[1]).toContain('Explicit subject')
    expect(registration[1]).not.toContain('Bounded PR title')
  })

  it('is idempotent for already merged and performs no registration write', async () => {
    const gh = vi
      .fn()
      .mockResolvedValueOnce(result('gh version'))
      .mockResolvedValueOnce(result('auth ok'))
      .mockResolvedValueOnce(result(view({ state: 'MERGED', mergeCommit: { oid: 'sha' }, autoMergeRequest: {} })))
    const out: any = await withRunnerResources(resources(gh), () =>
      enableGitHubPrAutoMergeAction(inputs as any, host()),
    )
    expect(out.output.enabled).toBe(false)
    expect(gh.mock.calls.filter((call: any) => call[1].includes('--auto'))).toHaveLength(0)
  })

  it('is idempotent when auto-merge is already enabled and performs no registration write', async () => {
    const gh = vi
      .fn()
      .mockResolvedValueOnce(result('gh version'))
      .mockResolvedValueOnce(result('auth ok'))
      .mockResolvedValueOnce(result(view({ autoMergeRequest: {} })))
      .mockResolvedValueOnce(result(view({ state: 'MERGED', mergeCommit: { oid: 'sha' }, autoMergeRequest: {} })))
    const out: any = await withRunnerResources(resources(gh), () =>
      enableGitHubPrAutoMergeAction({ ...inputs, subject: undefined } as any, host()),
    )
    expect(out.output.enabled).toBe(false)
    expect(gh.mock.calls.filter((call: any) => call[1].includes('--auto'))).toHaveLength(0)
  })

  it('re-reads an ambiguous registration failure and waits when auto-merge is already enabled', async () => {
    const gh = vi
      .fn()
      .mockResolvedValueOnce(result('gh version'))
      .mockResolvedValueOnce(result('auth ok'))
      .mockResolvedValueOnce(result(view()))
      .mockResolvedValueOnce(result('', 1, 'GraphQL mutation response was lost'))
      .mockResolvedValueOnce(result(view({ autoMergeRequest: { enabledAt: 'now' } })))
      .mockResolvedValueOnce(result(view({ state: 'MERGED', mergeCommit: { oid: 'sha' }, autoMergeRequest: {} })))
    const out: any = await withRunnerResources(resources(gh), () =>
      enableGitHubPrAutoMergeAction(inputs as any, host()),
    )
    expect(out.output).toMatchObject({ enabled: false, mergeCommitSha: 'sha' })
    expect(gh.mock.calls.filter((call: any) => call[1].includes('--auto'))).toHaveLength(1)
  })

  it('accepts a merged re-read after an ambiguous registration failure without another write', async () => {
    const gh = vi
      .fn()
      .mockResolvedValueOnce(result('gh version'))
      .mockResolvedValueOnce(result('auth ok'))
      .mockResolvedValueOnce(result(view()))
      .mockResolvedValueOnce(result('', 1, 'temporary network failure'))
      .mockResolvedValueOnce(result(view({ state: 'MERGED', mergeCommit: { oid: 'sha' }, autoMergeRequest: null })))
    const out: any = await withRunnerResources(resources(gh), () =>
      enableGitHubPrAutoMergeAction(inputs as any, host()),
    )
    expect(out.output).toMatchObject({ enabled: false, mergeCommitSha: 'sha' })
    expect(gh.mock.calls.filter((call: any) => call[1].includes('--auto'))).toHaveLength(1)
    expect(gh).toHaveBeenCalledTimes(5)
  })

  it('classifies ambiguous registration failure as retry-safe without retrying the write', async () => {
    const gh = vi
      .fn()
      .mockResolvedValueOnce(result('gh version'))
      .mockResolvedValueOnce(result('auth ok'))
      .mockResolvedValueOnce(result(view()))
      .mockResolvedValueOnce(result('', 1, 'temporary network failure'))
      .mockResolvedValueOnce(result(view()))
    const out: any = await withRunnerResources(resources(gh), () =>
      enableGitHubPrAutoMergeAction(inputs as any, host()),
    )
    expect(out.error.code).toBe('retry-safe')
    expect(gh.mock.calls.filter((call: any) => call[1].includes('--auto'))).toHaveLength(1)
  })

  it('classifies definite non-transient registration failures without retrying the write', async () => {
    const gh = vi
      .fn()
      .mockResolvedValueOnce(result('gh version'))
      .mockResolvedValueOnce(result('auth ok'))
      .mockResolvedValueOnce(result(view()))
      .mockResolvedValueOnce(result('', 1, 'subject must not be blank'))
    const out: any = await withRunnerResources(resources(gh), () =>
      enableGitHubPrAutoMergeAction(inputs as any, host()),
    )
    expect(out.error.code).toBe('enable-failed')
    expect(gh.mock.calls.filter((call: any) => call[1].includes('--auto'))).toHaveLength(1)
  })

  it('establishes the deadline before precheck and clamps command budgets', async () => {
    let now = 100
    const timeouts: number[] = []
    const gh = vi.fn(async (_command, _args, _cwd, _signal, _env, options) => {
      timeouts.push(options.timeoutMs)
      now += 6
      return result('gh version')
    })
    const out: any = await withRunnerResources(resources(gh, { now: () => now, autoMergeWaitMs: 10 }), () =>
      enableGitHubPrAutoMergeAction(inputs as any, host()),
    )
    expect(out.error.code).toBe('retry-safe')
    expect(timeouts).toEqual([10, 4])
  })

  it('uses the injected timer for deterministic overall timeout', async () => {
    let now = 0
    const gh = vi
      .fn()
      .mockResolvedValueOnce(result('gh version'))
      .mockResolvedValueOnce(result('auth ok'))
      .mockResolvedValueOnce(result(view({ autoMergeRequest: {} })))
      .mockResolvedValueOnce(result(view({ autoMergeRequest: {} })))
    const out: any = await withRunnerResources(
      resources(gh, {
        now: () => now,
        autoMergeWaitMs: 5,
        pollIntervalMs: 5,
        delay: async (ms: number) => {
          now += ms
        },
      }),
      () => enableGitHubPrAutoMergeAction(inputs as any, host()),
    )
    expect(out.error).toMatchObject({ code: 'retry-safe', message: expect.stringContaining('Timed out') })
  })

  it('cancels deterministically during the injected wait', async () => {
    const controller = new AbortController()
    const gh = vi
      .fn()
      .mockResolvedValueOnce(result('gh version'))
      .mockResolvedValueOnce(result('auth ok'))
      .mockResolvedValueOnce(result(view({ autoMergeRequest: {} })))
      .mockResolvedValueOnce(result(view({ autoMergeRequest: {} })))
    const out: any = await withRunnerResources(
      resources(gh, {
        delay: async () => {
          controller.abort()
          throw new Error('cancelled')
        },
      }),
      () => enableGitHubPrAutoMergeAction(inputs as any, host(controller.signal)),
    )
    expect(out.error).toMatchObject({ code: 'aborted', message: expect.stringContaining('Cancelled') })
  })

  it('maps precheck command rejection after host cancellation to aborted', async () => {
    const controller = new AbortController()
    const gh = vi.fn(async () => {
      controller.abort(new Error('precheck cancelled'))
      throw controller.signal.reason
    })
    const out: any = await withRunnerResources(resources(gh), () =>
      enableGitHubPrAutoMergeAction(inputs as any, host(controller.signal)),
    )
    expect(out.error.code).toBe('aborted')
  })

  it('maps PR read command rejection after host cancellation to aborted', async () => {
    const controller = new AbortController()
    const gh = vi
      .fn()
      .mockResolvedValueOnce(result('gh version'))
      .mockResolvedValueOnce(result('auth ok'))
      .mockImplementationOnce(async () => {
        controller.abort(new Error('read cancelled'))
        throw controller.signal.reason
      })
    const out: any = await withRunnerResources(resources(gh), () =>
      enableGitHubPrAutoMergeAction(inputs as any, host(controller.signal)),
    )
    expect(out.error.code).toBe('aborted')
  })

  it('classifies an aborted successful read before malformed JSON parsing', async () => {
    const controller = new AbortController()
    const gh = vi
      .fn()
      .mockResolvedValueOnce(result('gh version'))
      .mockResolvedValueOnce(result('auth ok'))
      .mockImplementationOnce(async () => {
        controller.abort(new Error('read completed during cancellation'))
        return result('{not-json')
      })
    const out: any = await withRunnerResources(resources(gh), () =>
      enableGitHubPrAutoMergeAction(inputs as any, host(controller.signal)),
    )
    expect(out.error).toMatchObject({ code: 'aborted', message: 'PR read was cancelled' })
  })

  it('maps registration command rejection after host cancellation to aborted', async () => {
    const controller = new AbortController()
    const gh = vi
      .fn()
      .mockResolvedValueOnce(result('gh version'))
      .mockResolvedValueOnce(result('auth ok'))
      .mockResolvedValueOnce(result(view()))
      .mockImplementationOnce(async () => {
        controller.abort(new Error('registration cancelled'))
        throw controller.signal.reason
      })
    const out: any = await withRunnerResources(resources(gh), () =>
      enableGitHubPrAutoMergeAction(inputs as any, host(controller.signal)),
    )
    expect(out.error.code).toBe('aborted')
    expect(gh.mock.calls.filter((call: any) => call[1].includes('--auto'))).toHaveLength(1)
  })

  it.each([
    ['name=AbortError', Object.assign(new Error('spawn aborted'), { name: 'AbortError' })],
    ['code=ABORT_ERR', Object.assign(new Error('spawn aborted'), { code: 'ABORT_ERR' })],
  ])('maps an abort-shaped %s command rejection to aborted', async (_label, rejection) => {
    const controller = new AbortController()
    const gh = vi.fn(async () => {
      controller.abort(new Error('host cancelled'))
      throw rejection
    })
    const out: any = await withRunnerResources(resources(gh), () =>
      enableGitHubPrAutoMergeAction(inputs as any, host(controller.signal)),
    )
    expect(out.error.code).toBe('aborted')
  })

  it('rethrows a distinct spawn error even when the host signal is aborted', async () => {
    const controller = new AbortController()
    const spawnError = new Error('spawn failed independently')
    const gh = vi.fn(async () => {
      controller.abort(new Error('host cancelled'))
      throw spawnError
    })
    await expect(
      withRunnerResources(resources(gh), () => enableGitHubPrAutoMergeAction(inputs as any, host(controller.signal))),
    ).rejects.toBe(spawnError)
  })

  it('maps reconciliation read rejection after host cancellation to aborted', async () => {
    const controller = new AbortController()
    const gh = vi
      .fn()
      .mockResolvedValueOnce(result('gh version'))
      .mockResolvedValueOnce(result('auth ok'))
      .mockResolvedValueOnce(result(view()))
      .mockResolvedValueOnce(result('', 1, 'GraphQL mutation response was lost'))
      .mockImplementationOnce(async () => {
        controller.abort(new Error('reconciliation cancelled'))
        throw controller.signal.reason
      })
    const out: any = await withRunnerResources(resources(gh), () =>
      enableGitHubPrAutoMergeAction(inputs as any, host(controller.signal)),
    )
    expect(out.error.code).toBe('aborted')
    expect(gh.mock.calls.filter((call: any) => call[1].includes('--auto'))).toHaveLength(1)
  })

  it('gives cancellation precedence when cancellation and the deadline coincide', async () => {
    let now = 0
    const controller = new AbortController()
    const gh = vi
      .fn()
      .mockResolvedValueOnce(result('gh version'))
      .mockResolvedValueOnce(result('auth ok'))
      .mockResolvedValueOnce(result(view({ autoMergeRequest: {} })))
      .mockResolvedValueOnce(result(view({ autoMergeRequest: {} })))
    const out: any = await withRunnerResources(
      resources(gh, {
        now: () => now,
        autoMergeWaitMs: 5,
        pollIntervalMs: 5,
        delay: async (ms: number) => {
          now += ms
          controller.abort(new Error('cancelled at deadline'))
        },
      }),
      () => enableGitHubPrAutoMergeAction(inputs as any, host(controller.signal)),
    )
    expect(out.error.code).toBe('aborted')
  })

  it('rethrows a non-cancellation polling delay failure', async () => {
    const delayError = new Error('poll timer failed')
    const gh = vi
      .fn()
      .mockResolvedValueOnce(result('gh version'))
      .mockResolvedValueOnce(result('auth ok'))
      .mockResolvedValueOnce(result(view({ autoMergeRequest: {} })))
      .mockResolvedValueOnce(result(view({ autoMergeRequest: {} })))
    await expect(
      withRunnerResources(resources(gh, { delay: async () => Promise.reject(delayError) }), () =>
        enableGitHubPrAutoMergeAction(inputs as any, host()),
      ),
    ).rejects.toBe(delayError)
  })

  it('rethrows a non-cancellation retry-backoff delay failure', async () => {
    const delayError = new Error('retry timer failed')
    const gh = vi
      .fn()
      .mockResolvedValueOnce(result('gh version'))
      .mockResolvedValueOnce(result('auth ok'))
      .mockResolvedValueOnce(result('', 1, 'temporary network failure'))
    await expect(
      withRunnerResources(resources(gh, { delay: async () => Promise.reject(delayError) }), () =>
        enableGitHubPrAutoMergeAction(inputs as any, host()),
      ),
    ).rejects.toBe(delayError)
  })

  it('rejects unexpected gh JSON field shapes', async () => {
    const gh = vi
      .fn()
      .mockResolvedValueOnce(result('gh version'))
      .mockResolvedValueOnce(result('auth ok'))
      .mockResolvedValueOnce(result(JSON.stringify({ state: 'OPEN', url: 12, statusCheckRollup: {} })))
    const out: any = await withRunnerResources(resources(gh), () =>
      enableGitHubPrAutoMergeAction(inputs as any, host()),
    )
    expect(out.error).toMatchObject({ code: 'retry-safe', message: expect.stringContaining('field shapes') })
  })

  it('classifies failed PR checks after registration', async () => {
    const gh = vi
      .fn()
      .mockResolvedValueOnce(result('gh version'))
      .mockResolvedValueOnce(result('auth ok'))
      .mockResolvedValueOnce(result(view()))
      .mockResolvedValueOnce(result('enabled'))
      .mockResolvedValueOnce(
        result(
          view({
            autoMergeRequest: {},
            statusCheckRollup: [{ name: 'ci', status: 'COMPLETED', conclusion: 'FAILURE' }],
          }),
        ),
      )
    const out: any = await withRunnerResources(resources(gh), () =>
      enableGitHubPrAutoMergeAction(inputs as any, host()),
    )
    expect(out.error.code).toBe('pr-checks-failed')
    expect(gh.mock.calls.filter((call: any) => call[1].includes('--auto'))).toHaveLength(1)
  })

  it('classifies conflicts', async () => {
    const gh = vi
      .fn()
      .mockResolvedValueOnce(result('gh version'))
      .mockResolvedValueOnce(result('auth ok'))
      .mockResolvedValueOnce(result(view({ mergeStateStatus: 'DIRTY' })))
    const out: any = await withRunnerResources(resources(gh), () =>
      enableGitHubPrAutoMergeAction(inputs as any, host()),
    )
    expect(out.error.code).toBe('conflict')
  })

  it('failed checks outrank pending checks', () => {
    expect(
      classifyPrChecks([
        { name: 'still running', bucket: 'pending', state: 'IN_PROGRESS' },
        { name: 'failed', bucket: 'fail', state: 'FAILURE' },
      ]),
    ).toMatchObject({ kind: 'failed', message: expect.stringContaining('failed') })
  })
})
