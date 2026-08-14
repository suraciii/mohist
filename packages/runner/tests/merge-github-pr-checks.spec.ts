import { describe, expect, it as vitestIt, vi } from 'vitest'
import { callAction } from './support/call-action.js'
import { mergeGitHubPrAction } from '../src/actions/github-pr.js'
import {
  checksRollup,
  context,
  createMergeGhTestHarness,
  ghFail,
  ghOk,
  type MergeGhTestResources,
} from './support/merge-github-pr-test-helpers.js'
import { withTestRunnerResources } from './support/test-resources.js'
import { MemoryFileSystem } from './support/memory-filesystem.js'

const { installGit, installGh, installMoIssueShow } = createMergeGhTestHarness()

function it(name: string, body: (resources: MergeGhTestResources) => Promise<void> | void): void {
  vitestIt(name, async () => {
    const resources: MergeGhTestResources = { fileSystem: new MemoryFileSystem(), ghCalls: [] }
    await withTestRunnerResources(async () => await body(resources), resources)
  })
}

describe('mohist/merge-github-pr action', () => {
  describe('checks-gated merge', () => {
    it('waits through pending checks and merges once a check passes', async (resources) => {
      vi.useFakeTimers()
      try {
        const ghCalls: string[] = []
        installMoIssueShow(resources)
        installGit(resources, () => {
          throw new Error('git should not be called')
        })
        installGh(resources, (cmd, args) => {
          const full = [cmd, ...args].join(' ')
          ghCalls.push(full)
          switch (full) {
            case 'gh --version':
            case 'gh auth status':
              return ghOk('ok\n')
            case 'gh pr view 42 --json state,mergeCommit,url,number,mergeStateStatus':
              return ghOk(
                JSON.stringify({
                  state: 'OPEN',
                  number: 42,
                  url: 'https://github.com/example/repo/pull/42',
                  mergeCommit: null,
                }),
              )
            case 'gh pr view 42 --json statusCheckRollup': {
              const checksCount = ghCalls.filter((c) => c === 'gh pr view 42 --json statusCheckRollup').length
              if (checksCount < 3) {
                return ghOk(
                  checksRollup([
                    { name: 'build', status: 'IN_PROGRESS', conclusion: '' },
                    { name: 'lint', status: 'QUEUED', conclusion: '' },
                  ]),
                )
              }
              return ghOk(
                checksRollup([
                  { name: 'build', status: 'COMPLETED', conclusion: 'SUCCESS' },
                  { name: 'lint', status: 'COMPLETED', conclusion: 'SUCCESS' },
                ]),
              )
            }
            case 'gh pr view 42 --json mergeStateStatus':
              return ghOk(JSON.stringify({ mergeStateStatus: 'CLEAN' }))
            case 'gh pr merge 42 --squash --subject Use GitHub PR workflow --body ':
              return ghOk('Merged pull request #42\n')
            case 'gh pr view 42 --json state,mergeCommit,url':
              return ghOk(
                JSON.stringify({
                  state: 'MERGED',
                  url: 'https://github.com/example/repo/pull/42',
                  mergeCommit: { oid: 'merge-sha-1' },
                }),
              )
            default:
              return ghFail(`unexpected gh call: ${full}`)
          }
        })

        const ctx = context({
          prNumber: 42,
          method: 'squash',
          subjectFrom: 'issue.title',
        })
        const resultPromise = callAction(mergeGitHubPrAction, ctx)
        await vi.advanceTimersByTimeAsync(15_000)
        await vi.advanceTimersByTimeAsync(15_000)
        const result = await resultPromise
        const output = result.output as Record<string, unknown>

        expect(result.error).toBeUndefined()
        const checksCalls = ghCalls.filter((c) => c === 'gh pr view 42 --json statusCheckRollup')
        expect(checksCalls.length).toBe(3)
        expect(ghCalls).toContain('gh pr merge 42 --squash --subject Use GitHub PR workflow --body ')
        expect(output).toMatchObject({
          kind: 'merge-github-pr',
          status: 'completed',
          prNumber: 42,
          prUrl: 'https://github.com/example/repo/pull/42',
          mergeCommitSha: 'merge-sha-1',
        })
        const stepNames = (output.steps as Array<{ name: string }>).map((step) => step.name)
        expect(stepNames.filter((name: string) => name === 'gh-pr-checks').length).toBe(3)
      } finally {
        vi.useRealTimers()
      }
    })

    it('waits through UNKNOWN mergeStateStatus after checks pass instead of returning a retryable failure', async (resources) => {
      vi.useFakeTimers()
      try {
        const ghCalls: string[] = []
        installMoIssueShow(resources)
        installGit(resources, () => {
          throw new Error('git should not be called')
        })
        let mergeStateCalls = 0
        installGh(resources, (cmd, args) => {
          const full = [cmd, ...args].join(' ')
          ghCalls.push(full)
          switch (full) {
            case 'gh --version':
            case 'gh auth status':
              return ghOk('ok\n')
            case 'gh pr view 42 --json state,mergeCommit,url,number,mergeStateStatus':
              return ghOk(
                JSON.stringify({
                  state: 'OPEN',
                  number: 42,
                  url: 'https://github.com/example/repo/pull/42',
                  mergeCommit: null,
                }),
              )
            case 'gh pr view 42 --json statusCheckRollup':
              return ghOk(checksRollup([{ name: 'build', status: 'COMPLETED', conclusion: 'SUCCESS' }]))
            case 'gh pr view 42 --json mergeStateStatus':
              mergeStateCalls += 1
              return ghOk(JSON.stringify({ mergeStateStatus: mergeStateCalls < 3 ? 'UNKNOWN' : 'CLEAN' }))
            case 'gh pr merge 42 --squash --subject Use GitHub PR workflow --body ':
              return ghOk('Merged pull request #42\n')
            case 'gh pr view 42 --json state,mergeCommit,url':
              return ghOk(
                JSON.stringify({
                  state: 'MERGED',
                  url: 'https://github.com/example/repo/pull/42',
                  mergeCommit: { oid: 'merge-sha-1' },
                }),
              )
            default:
              return ghFail(`unexpected gh call: ${full}`)
          }
        })

        const ctx = context({
          prNumber: 42,
          method: 'squash',
          subjectFrom: 'issue.title',
        })
        const resultPromise = callAction(mergeGitHubPrAction, ctx)
        await vi.advanceTimersByTimeAsync(15_000)
        await vi.advanceTimersByTimeAsync(15_000)
        const result = await resultPromise
        const output = result.output as Record<string, unknown>

        expect(result.error).toBeUndefined()
        expect(mergeStateCalls).toBe(3)
        expect(ghCalls).toContain('gh pr merge 42 --squash --subject Use GitHub PR workflow --body ')
        expect(output).toMatchObject({
          kind: 'merge-github-pr',
          status: 'completed',
          prNumber: 42,
          mergeCommitSha: 'merge-sha-1',
        })
      } finally {
        vi.useRealTimers()
      }
    })

    it('cancels waiting when the context signal is aborted while checks are still pending', async (resources) => {
      vi.useFakeTimers()
      try {
        const ghCalls: string[] = []
        installMoIssueShow(resources)
        installGit(resources, () => {
          throw new Error('git should not be called')
        })
        installGh(resources, (cmd, args) => {
          const full = [cmd, ...args].join(' ')
          ghCalls.push(full)
          switch (full) {
            case 'gh --version':
            case 'gh auth status':
              return ghOk('ok\n')
            case 'gh pr view 42 --json state,mergeCommit,url,number,mergeStateStatus':
              return ghOk(
                JSON.stringify({
                  state: 'OPEN',
                  number: 42,
                  url: 'https://github.com/example/repo/pull/42',
                  mergeCommit: null,
                }),
              )
            case 'gh pr view 42 --json statusCheckRollup':
              return ghOk(checksRollup([{ name: 'build', status: 'IN_PROGRESS', conclusion: '' }]))
            default:
              return ghFail(`unexpected gh call: ${full}`)
          }
        })

        const controller = new AbortController()
        const ctx = context({
          prNumber: 42,
          method: 'squash',
          subjectFrom: 'issue.title',
        })
        Object.assign(ctx, { signal: controller.signal })
        const resultPromise = callAction(mergeGitHubPrAction, ctx)
        const probe = resultPromise.then(
          () => 'resolved' as const,
          (error: unknown) => ({ kind: 'rejected' as const, error }),
        )
        await vi.advanceTimersByTimeAsync(15_000)
        controller.abort(new Error('run canceled'))
        const outcome = await probe
        const result = await resultPromise
        expect(outcome).toBe('resolved')
        expect(result.error).toMatchObject({ code: 'retry-safe' })
        expect(result.error?.message).toContain('Cancelled while waiting for PR #42 checks')
        expect(ghCalls).not.toContain('gh pr merge 42 --squash --subject Use GitHub PR workflow --body ')
      } finally {
        vi.useRealTimers()
      }
    })

    it('merges when all checks are PASS/SKIP', async (resources) => {
      const cases: Array<{ checks: unknown[] }> = [
        {
          checks: [
            { name: 'build', status: 'COMPLETED', conclusion: 'SUCCESS' },
            { name: 'lint', status: 'COMPLETED', conclusion: 'SKIPPED' },
          ],
        },
      ]

      for (const scenario of cases) {
        const ghCalls: string[] = []
        installMoIssueShow(resources)
        installGh(resources, (cmd, args) => {
          const full = [cmd, ...args].join(' ')
          ghCalls.push(full)
          switch (full) {
            case 'gh --version':
            case 'gh auth status':
              return ghOk('ok\n')
            case 'gh pr view 42 --json state,mergeCommit,url,number,mergeStateStatus':
              return ghOk(
                JSON.stringify({
                  state: 'OPEN',
                  number: 42,
                  url: 'https://github.com/example/repo/pull/42',
                  mergeCommit: null,
                }),
              )
            case 'gh pr view 42 --json statusCheckRollup':
              return ghOk(checksRollup(scenario.checks))
            case 'gh pr view 42 --json mergeStateStatus':
              return ghOk(JSON.stringify({ mergeStateStatus: 'CLEAN' }))
            case 'gh pr merge 42 --squash --subject Use GitHub PR workflow --body ':
              return ghOk('Merged pull request #42\n')
            case 'gh pr view 42 --json state,mergeCommit,url':
              return ghOk(
                JSON.stringify({
                  state: 'MERGED',
                  url: 'https://github.com/example/repo/pull/42',
                  mergeCommit: { oid: 'merge-sha-1' },
                }),
              )
            default:
              return ghFail(`unexpected gh call: ${full}`)
          }
        })

        const result = await callAction(
          mergeGitHubPrAction,
          context({
            prNumber: 42,
            method: 'squash',
            subjectFrom: 'issue.title',
          }),
        )
        const output = result.output as Record<string, unknown>

        expect(result.error).toBeUndefined()
        expect(ghCalls).toContain('gh pr view 42 --json statusCheckRollup')
        expect(ghCalls).toContain('gh pr merge 42 --squash --subject Use GitHub PR workflow --body ')
        expect(output).toMatchObject({
          kind: 'merge-github-pr',
          status: 'completed',
          prNumber: 42,
        })
      }
    })
  })
})
