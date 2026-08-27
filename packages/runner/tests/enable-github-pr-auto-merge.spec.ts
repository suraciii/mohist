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
    issueFieldCommandRunner: async () => result('Ship it'),
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

  it('is idempotent for already merged', async () => {
    const gh = vi
      .fn()
      .mockResolvedValueOnce(result('gh version'))
      .mockResolvedValueOnce(result('auth ok'))
      .mockResolvedValueOnce(result(view({ state: 'MERGED', mergeCommit: { oid: 'sha' }, autoMergeRequest: {} })))
    const out: any = await withRunnerResources(resources(gh), () =>
      enableGitHubPrAutoMergeAction(inputs as any, host()),
    )
    expect(out.output.enabled).toBe(false)
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
    expect(out.error).toMatchObject({ code: 'retry-safe', message: expect.stringContaining('Cancelled') })
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
