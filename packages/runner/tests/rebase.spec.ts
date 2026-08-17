import { describe, expect, it as vitestIt } from 'vitest'
import { NETWORK_COMMAND_TIMEOUT_MS } from '../src/actions/git.js'
import { rebaseAction } from '../src/actions/rebase.js'
import type { RunnerFileSystem, RunnerGitRunner } from '../src/system/filesystem.js'
import type { JsonObject } from '../src/core/types.js'
import type { ActionTestContext as ActionContext } from './support/action-test-context.js'
import { callAction } from './support/call-action.js'
import { withTestRunnerResources } from './support/test-resources.js'
import { MemoryFileSystem } from './support/memory-filesystem.js'
import { StatefulFakeWorktree } from './support/fake-worktree.js'

type RebaseTestResources = {
  fileSystem: RunnerFileSystem
  rebaseGitRunner?: RunnerGitRunner
  rebaseExistsChecker?: (path: string) => boolean
  issueFieldCommandRunner?: (
    command: string,
    args: string[],
    cwd: string,
    signal: AbortSignal,
  ) => Promise<{ exitCode: number; stdout: string; stderr: string }>
}

const EXPECTED_BRANCH = 'mohist/run-wr-rebase-1'

function it(name: string, body: (resources: RebaseTestResources) => Promise<void> | void): void {
  vitestIt(name, async () => {
    const resources: RebaseTestResources = { fileSystem: new MemoryFileSystem() }
    await withTestRunnerResources(async () => await body(resources), resources)
  })
}

function useRebaseExistsChecker(resources: RebaseTestResources, checker: (path: string) => boolean): void {
  resources.rebaseExistsChecker = checker
}

function installRebaseGitRunner(resources: RebaseTestResources, runner: RunnerGitRunner): void {
  // The rebase completion invariant probes the shared workspace health
  // snapshot after a successful rebase and squash. These three probes are
  // part of that contract and are answered for every scenario here; the
  // scenario-specific runner keeps handling the rebase/fetch/commit flow.
  resources.rebaseGitRunner = async (workDir, args, signal, options) => {
    const command = args.join(' ')
    if (command === 'rev-parse --git-path MERGE_HEAD') return ok('/fake/worktree/.git/MERGE_HEAD\n')
    if (command === 'rev-parse --git-path CHERRY_PICK_HEAD') return ok('/fake/worktree/.git/CHERRY_PICK_HEAD\n')
    if (command === 'rev-parse --abbrev-ref HEAD') return ok(`${EXPECTED_BRANCH}\n`)
    return runner(workDir, args, signal, options)
  }
}

function installIssueFieldCommandRunner(
  resources: RebaseTestResources,
  runner: (
    command: string,
    args: string[],
    cwd: string,
    signal: AbortSignal,
  ) => Promise<{ exitCode: number; stdout: string; stderr: string }>,
): void {
  resources.issueFieldCommandRunner = runner
}

describe('mohist/rebase', () => {
  it('LocalBasePath_RebasesOntoLocalBaseBranch', async (resources) => {
    const calls: string[] = []
    useRebaseExistsChecker(resources, () => false)
    installRebaseGitRunner(resources, async (_workDir, args) => {
      calls.push(args.join(' '))
      switch (args.join(' ')) {
        case 'rev-parse --git-path rebase-merge':
          return ok('/fake/worktree/.git/rebase-merge\n')
        case 'rev-parse --git-path rebase-apply':
          return ok('/fake/worktree/.git/rebase-apply\n')
        case 'rev-parse master':
          return ok('baseSha\n')
        case 'status --porcelain':
          return ok('')
        case 'rev-parse HEAD':
          return ok(calls.filter((call) => call === 'rev-parse HEAD').length === 1 ? 'before\n' : 'after\n')
        case 'rebase master':
          return ok('Successfully rebased')
        default:
          return fail(`unexpected git call: ${args.join(' ')}`)
      }
    })

    const result = await callAction(rebaseAction, context())
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeUndefined()
    expect(calls).toEqual([
      'rev-parse --git-path rebase-merge',
      'rev-parse --git-path rebase-apply',
      'rev-parse master',
      'status --porcelain',
      'rev-parse HEAD',
      'rebase master',
      'rev-parse HEAD',
      // Completion invariant probes: residual, head, and worktree status.
      'rev-parse --git-path rebase-merge',
      'rev-parse --git-path rebase-apply',
      'rev-parse HEAD',
      'status --porcelain',
    ])
    expect(calls).not.toContain('fetch origin master')
    expect(calls).not.toContain('rebase origin/master')
    expect(calls).not.toContain('reset --soft')
    expect(calls).not.toContain('commit -m Complete issue #217')
    expect(output).toMatchObject({
      kind: 'rebase',
      baseBranch: 'master',
      remote: null,
      baseRef: 'master',
      rebasedOntoSha: 'baseSha',
      beforeHeadSha: 'before',
      afterHeadSha: 'after',
      squashed: false,
      squashedHeadSha: null,
      rebased: true,
      conflicts: [],
    })
  })

  it('RemoteOption_FetchesAndRebasesOntoRemoteBaseRef', async (resources) => {
    const calls: string[] = []
    useRebaseExistsChecker(resources, () => false)
    installRebaseGitRunner(resources, async (_workDir, args) => {
      calls.push(args.join(' '))
      switch (args.join(' ')) {
        case 'rev-parse --git-path rebase-merge':
          return ok('/fake/worktree/.git/rebase-merge\n')
        case 'rev-parse --git-path rebase-apply':
          return ok('/fake/worktree/.git/rebase-apply\n')
        case 'fetch origin master':
          return ok('From https://example.com/repo\n * branch            master     -> FETCH_HEAD')
        case 'rev-parse origin/master':
          return ok('baseShaRemote\n')
        case 'status --porcelain':
          return ok('')
        case 'rev-parse HEAD':
          return ok(calls.filter((call) => call === 'rev-parse HEAD').length === 1 ? 'before\n' : 'after\n')
        case 'rebase origin/master':
          return ok('Successfully rebased and updated refs/heads/mo/issue-217.')
        default:
          return fail(`unexpected git call: ${args.join(' ')}`)
      }
    })

    const result = await callAction(rebaseAction, context({ baseBranch: 'master', remote: 'origin' }))
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeUndefined()
    expect(calls).toEqual([
      'rev-parse --git-path rebase-merge',
      'rev-parse --git-path rebase-apply',
      'fetch origin master',
      'rev-parse origin/master',
      'status --porcelain',
      'rev-parse HEAD',
      'rebase origin/master',
      'rev-parse HEAD',
      // Completion invariant probes.
      'rev-parse --git-path rebase-merge',
      'rev-parse --git-path rebase-apply',
      'rev-parse HEAD',
      'status --porcelain',
    ])
    expect(calls).not.toContain('rebase master')
    expect(output).toMatchObject({
      kind: 'rebase',
      baseBranch: 'master',
      remote: 'origin',
      baseRef: 'origin/master',
      rebasedOntoSha: 'baseShaRemote',
      beforeHeadSha: 'before',
      afterHeadSha: 'after',
      squashed: false,
      squashedHeadSha: null,
      rebased: true,
    })
  })

  it('MessageFrom_IsIgnoredWhenSquashIsFalse', async (resources) => {
    const calls: string[] = []
    const moCalls: string[] = []
    useRebaseExistsChecker(resources, () => false)
    installIssueFieldCommandRunner(resources, async (cmd, args) => {
      moCalls.push([cmd, ...args].join(' '))
      return {
        exitCode: 1,
        stdout: '',
        stderr: 'should not run',
      }
    })
    installRebaseGitRunner(resources, async (_workDir, args) => {
      calls.push(args.join(' '))
      switch (args.join(' ')) {
        case 'rev-parse --git-path rebase-merge':
          return ok('/fake/worktree/.git/rebase-merge\n')
        case 'rev-parse --git-path rebase-apply':
          return ok('/fake/worktree/.git/rebase-apply\n')
        case 'fetch origin master':
          return ok('')
        case 'rev-parse origin/master':
          return ok('baseShaRemote\n')
        case 'status --porcelain':
          return ok('')
        case 'rev-parse HEAD':
          return ok(calls.filter((call) => call === 'rev-parse HEAD').length === 1 ? 'before\n' : 'after\n')
        case 'rebase origin/master':
          return ok('Successfully rebased')
        default:
          return fail(`unexpected git call: ${args.join(' ')}`)
      }
    })

    const result = await callAction(
      rebaseAction,
      context({
        baseBranch: 'master',
        remote: 'origin',
        squash: false,
        messageFrom: 'issue.title',
      }),
    )

    expect(result.error).toBeUndefined()
    expect(moCalls).toEqual([])
    expect(calls).not.toContain('commit -m')
  })

  it('SquashOption_FoldsMultipleCommitsIntoOneOnRunBranch', async (resources) => {
    const calls: string[] = []
    useRebaseExistsChecker(resources, () => false)
    installRebaseGitRunner(resources, async (_workDir, args) => {
      calls.push(args.join(' '))
      switch (args.join(' ')) {
        case 'rev-parse --git-path rebase-merge':
          return ok('/fake/worktree/.git/rebase-merge\n')
        case 'rev-parse --git-path rebase-apply':
          return ok('/fake/worktree/.git/rebase-apply\n')
        case 'fetch origin master':
          return ok('')
        case 'rev-parse origin/master':
          return ok('baseSha\n')
        case 'status --porcelain':
          return ok('')
        case 'rev-parse HEAD': {
          const index = calls.filter((call) => call === 'rev-parse HEAD').length
          if (index === 1) return ok('beforeRebase\n')
          if (index === 2) return ok('afterRebase\n')
          return ok('squashedHead\n')
        }
        case 'rebase origin/master':
          return ok('Successfully rebased and updated refs/heads/mo/issue-217.')
        case 'reset --soft baseSha':
          return ok('')
        case 'commit -m Complete issue #217':
          return ok('[mo/issue-217 1a2b3c4] Complete issue #217\n 3 files changed, 42 insertions(+), 7 deletions(-)')
        default:
          return fail(`unexpected git call: ${args.join(' ')}`)
      }
    })

    const result = await callAction(
      rebaseAction,
      context({
        baseBranch: 'master',
        remote: 'origin',
        squash: true,
        message: 'Complete issue #217',
      }),
    )
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeUndefined()
    expect(calls).toEqual([
      'rev-parse --git-path rebase-merge',
      'rev-parse --git-path rebase-apply',
      'fetch origin master',
      'rev-parse origin/master',
      'status --porcelain',
      'rev-parse HEAD',
      'rebase origin/master',
      'rev-parse HEAD',
      'reset --soft baseSha',
      'commit -m Complete issue #217',
      'rev-parse HEAD',
      // Completion invariant probes after the squash commit.
      'rev-parse --git-path rebase-merge',
      'rev-parse --git-path rebase-apply',
      'rev-parse HEAD',
      'status --porcelain',
    ])
    expect(calls).not.toContain('checkout master')
    expect(calls).not.toContain('checkout origin/master')
    expect(output).toMatchObject({
      kind: 'rebase',
      baseBranch: 'master',
      remote: 'origin',
      baseRef: 'origin/master',
      rebasedOntoSha: 'baseSha',
      beforeHeadSha: 'beforeRebase',
      afterHeadSha: 'afterRebase',
      squashed: true,
      squashedHeadSha: 'squashedHead',
      rebased: true,
    })
  })

  it('SquashOption_MessageFromIssueTitle_ResolvesTitleWithMoIssueShow', async (resources) => {
    const calls: string[] = []
    const moCalls: string[] = []
    useRebaseExistsChecker(resources, () => false)
    installIssueFieldCommandRunner(resources, async (cmd, args) => {
      moCalls.push([cmd, ...args].join(' '))
      return {
        exitCode: 0,
        stdout: JSON.stringify({ success: true, data: { title: 'Use issue title for squash', body: 'ignored' } }),
        stderr: '',
      }
    })
    installRebaseGitRunner(resources, async (_workDir, args) => {
      calls.push(args.join(' '))
      switch (args.join(' ')) {
        case 'rev-parse --git-path rebase-merge':
          return ok('/fake/worktree/.git/rebase-merge\n')
        case 'rev-parse --git-path rebase-apply':
          return ok('/fake/worktree/.git/rebase-apply\n')
        case 'fetch origin master':
          return ok('')
        case 'rev-parse origin/master':
          return ok('baseSha\n')
        case 'status --porcelain':
          return ok('')
        case 'rev-parse HEAD': {
          const index = calls.filter((call) => call === 'rev-parse HEAD').length
          if (index === 1) return ok('beforeRebase\n')
          if (index === 2) return ok('afterRebase\n')
          return ok('squashedHead\n')
        }
        case 'rebase origin/master':
          return ok('Successfully rebased and updated refs/heads/mo/issue-217.')
        case 'reset --soft baseSha':
          return ok('')
        case 'commit -m Use issue title for squash':
          return ok('[mo/issue-217 1a2b3c4] Use issue title for squash\n')
        default:
          return fail(`unexpected git call: ${args.join(' ')}`)
      }
    })

    const result = await callAction(
      rebaseAction,
      context({
        baseBranch: 'master',
        remote: 'origin',
        squash: true,
        messageFrom: 'issue.title',
      }),
    )

    expect(result.error).toBeUndefined()
    expect(moCalls).toEqual(['mo issue view 217 --project proj_1 --json title,body'])
    expect(calls).toContain('commit -m Use issue title for squash')
  })

  it('SquashOption_MessageFromIssueTitleFailure_ReturnsStructuredFailure', async (resources) => {
    const calls: string[] = []
    const moCalls: string[] = []
    useRebaseExistsChecker(resources, () => false)
    installIssueFieldCommandRunner(resources, async (cmd, args) => {
      moCalls.push([cmd, ...args].join(' '))
      return {
        exitCode: 1,
        stdout: '',
        stderr: 'issue not found',
      }
    })
    installRebaseGitRunner(resources, async (_workDir, args) => {
      calls.push(args.join(' '))
      return fail(`unexpected git call: ${args.join(' ')}`)
    })

    const result = await callAction(
      rebaseAction,
      context({
        baseBranch: 'master',
        remote: 'origin',
        squash: true,
        messageFrom: 'issue.title',
      }),
    )
    expect(result.error).toBeDefined()
    expect(result.error).toMatchObject({ code: 'invalid-input' })
    expect(result.error?.message).toContain('mo issue view 217 failed')
    expect(calls).toEqual(['rev-parse --git-path rebase-merge', 'rev-parse --git-path rebase-apply'])
    expect(calls).not.toContain('fetch origin master')
    expect(calls).not.toContain('rebase origin/master')
    expect(calls).not.toContain('commit -m Use issue title for squash')
    expect(moCalls).toEqual(['mo issue view 217 --project proj_1 --json title,body'])
  })

  it('SquashOption_UnsupportedMessageFrom_ReturnsStructuredFailure', async (resources) => {
    const calls: string[] = []
    const moCalls: string[] = []
    useRebaseExistsChecker(resources, () => false)
    installIssueFieldCommandRunner(resources, async (cmd, args) => {
      moCalls.push([cmd, ...args].join(' '))
      return {
        exitCode: 0,
        stdout: 'unexpected',
        stderr: '',
      }
    })
    installRebaseGitRunner(resources, async (_workDir, args) => {
      calls.push(args.join(' '))
      return fail(`unexpected git call: ${args.join(' ')}`)
    })

    const result = await callAction(
      rebaseAction,
      context({
        baseBranch: 'master',
        remote: 'origin',
        squash: true,
        messageFrom: 'issue.summary',
      }),
    )
    expect(result.error).toBeDefined()
    expect(result.error).toMatchObject({ code: 'invalid-input' })
    expect(result.error?.message).toContain("Unsupported messageFrom source 'issue.summary'")
    expect(calls).toEqual(['rev-parse --git-path rebase-merge', 'rev-parse --git-path rebase-apply'])
    expect(moCalls).toEqual([])
  })

  it('SquashOptionWithoutMessage_FailsBeforeSquash', async (resources) => {
    const calls: string[] = []
    useRebaseExistsChecker(resources, () => false)
    installRebaseGitRunner(resources, async (_workDir, args) => {
      calls.push(args.join(' '))
      switch (args.join(' ')) {
        case 'rev-parse --git-path rebase-merge':
          return ok('/fake/worktree/.git/rebase-merge\n')
        case 'rev-parse --git-path rebase-apply':
          return ok('/fake/worktree/.git/rebase-apply\n')
        case 'status --porcelain':
          return ok('')
        case 'rev-parse master':
          return ok('baseSha\n')
        case 'rev-parse HEAD':
          return ok(calls.filter((call) => call === 'rev-parse HEAD').length === 1 ? 'before\n' : 'after\n')
        case 'rebase master':
          return ok('Successfully rebased')
        default:
          return fail(`unexpected git call: ${args.join(' ')}`)
      }
    })

    const result = await callAction(rebaseAction, context({ baseBranch: 'master', squash: true }))
    expect(result.error).toBeDefined()
    expect(calls).not.toContain('reset --soft')
    expect(calls).not.toContain('commit -m')
    expect(result.error).toMatchObject({ code: 'invalid-input' })
    expect(result.error?.message).toContain("non-empty commit 'message'")
  })

  it('RemoteFetchFails_ReportsRetrySafe', async (resources) => {
    const calls: string[] = []
    useRebaseExistsChecker(resources, () => false)
    installRebaseGitRunner(resources, async (_workDir, args) => {
      calls.push(args.join(' '))
      switch (args.join(' ')) {
        case 'rev-parse --git-path rebase-merge':
          return ok('/fake/worktree/.git/rebase-merge\n')
        case 'rev-parse --git-path rebase-apply':
          return ok('/fake/worktree/.git/rebase-apply\n')
        case 'fetch origin master':
          return fail('fatal: could not resolve host')
        default:
          return fail(`unexpected git call: ${args.join(' ')}`)
      }
    })

    const result = await callAction(rebaseAction, context({ baseBranch: 'master', remote: 'origin' }))
    expect(result.error).toBeDefined()
    expect(calls).toEqual([
      'rev-parse --git-path rebase-merge',
      'rev-parse --git-path rebase-apply',
      'fetch origin master',
    ])
    expect(calls).not.toContain('rebase origin/master')
    expect(result.error).toMatchObject({ code: 'fetch-failed' })
    expect(result.error?.message).toContain('Rebase was not started')
  })

  it('BaseRefRevParseFails_ReportsRetrySafe', async (resources) => {
    const calls: string[] = []
    useRebaseExistsChecker(resources, () => false)
    installRebaseGitRunner(resources, async (_workDir, args) => {
      calls.push(args.join(' '))
      switch (args.join(' ')) {
        case 'rev-parse --git-path rebase-merge':
          return ok('/fake/worktree/.git/rebase-merge\n')
        case 'rev-parse --git-path rebase-apply':
          return ok('/fake/worktree/.git/rebase-apply\n')
        case 'status --porcelain':
          return ok('')
        case 'rev-parse origin/master':
          return fail("fatal: ambiguous argument 'origin/master'")
        default:
          return fail(`unexpected git call: ${args.join(' ')}`)
      }
    })

    const result = await callAction(rebaseAction, context({ baseBranch: 'master', remote: 'origin' }))
    expect(result.error).toBeDefined()
    expect(calls).not.toContain('rebase origin/master')
    expect(result.error).toMatchObject({ code: 'fetch-failed' })
  })

  it('DirtyWorktreeBeforeRebase_CommitsPendingChangesThenRebases', async (resources) => {
    const calls: string[] = []
    let pendingCommitted = false
    useRebaseExistsChecker(resources, () => false)
    installRebaseGitRunner(resources, async (_workDir, args) => {
      calls.push(args.join(' '))
      switch (args.join(' ')) {
        case 'rev-parse --git-path rebase-merge':
          return ok('/fake/worktree/.git/rebase-merge\n')
        case 'rev-parse --git-path rebase-apply':
          return ok('/fake/worktree/.git/rebase-apply\n')
        case 'status --porcelain':
          return ok(pendingCommitted ? '' : ' M packages/runner/src/actions/opencode.ts\n')
        case 'rev-parse master':
          return ok('baseSha\n')
        case 'add .':
          return ok('')
        case 'commit -m Prepare rebase onto master':
          pendingCommitted = true
          return ok('[issue abc123] Prepare rebase onto master')
        case 'rev-parse HEAD':
          return ok(calls.filter((call) => call === 'rev-parse HEAD').length === 1 ? 'before\n' : 'after\n')
        case 'rebase master':
          return ok('Successfully rebased')
        default:
          return fail(`unexpected git call: ${args.join(' ')}`)
      }
    })

    const result = await callAction(rebaseAction, context())
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeUndefined()
    expect(calls).toEqual([
      'rev-parse --git-path rebase-merge',
      'rev-parse --git-path rebase-apply',
      'rev-parse master',
      'status --porcelain',
      'add .',
      'commit -m Prepare rebase onto master',
      'rev-parse HEAD',
      'rebase master',
      'rev-parse HEAD',
      // Completion invariant probes confirm the committed worktree is clean.
      'rev-parse --git-path rebase-merge',
      'rev-parse --git-path rebase-apply',
      'rev-parse HEAD',
      'status --porcelain',
    ])
    expect(output).toMatchObject({
      beforeHeadSha: 'before',
      afterHeadSha: 'after',
      rebased: true,
    })
  })

  it('StaleRebaseStateBeforeRebase_AbortsBeforeStartingFreshRebase', async (resources) => {
    const calls: string[] = []
    let rebaseStatePresent = true
    useRebaseExistsChecker(resources, (path) => path === '/fake/worktree/.git/rebase-merge' && rebaseStatePresent)
    installRebaseGitRunner(resources, async (_workDir, args) => {
      calls.push(args.join(' '))
      switch (args.join(' ')) {
        case 'rev-parse --git-path rebase-merge':
          return ok('/fake/worktree/.git/rebase-merge\n')
        case 'rev-parse --git-path rebase-apply':
          return ok('/fake/worktree/.git/rebase-apply\n')
        case 'rebase --abort':
          rebaseStatePresent = false
          return ok('aborted')
        case 'rev-parse master':
          return ok('baseSha\n')
        case 'status --porcelain':
          return ok('')
        case 'rev-parse HEAD':
          return ok(calls.filter((call) => call === 'rev-parse HEAD').length === 1 ? 'before\n' : 'after\n')
        case 'rebase master':
          return ok('Successfully rebased')
        default:
          return fail(`unexpected git call: ${args.join(' ')}`)
      }
    })

    const result = await callAction(rebaseAction, context())

    expect(result.error).toBeUndefined()
    expect(calls).toContain('rebase --abort')
    expect(calls.indexOf('rebase --abort')).toBeLessThan(calls.indexOf('rebase master'))
  })

  it('Conflict_NoRecovery_AbortsAndReportsConflict', async (resources) => {
    const calls: string[] = []
    useRebaseExistsChecker(resources, () => false)
    installRebaseGitRunner(resources, async (_workDir, args) => {
      calls.push(args.join(' '))
      switch (args.join(' ')) {
        case 'rev-parse --git-path rebase-merge':
          return ok('/fake/worktree/.git/rebase-merge\n')
        case 'rev-parse --git-path rebase-apply':
          return ok('/fake/worktree/.git/rebase-apply\n')
        case 'rev-parse master':
          return ok('baseSha\n')
        case 'status --porcelain':
          return ok('')
        case 'rev-parse HEAD':
          return ok('before\n')
        case 'rebase master':
          return fail('CONFLICT (content): Merge conflict in packages/runner/src/actions/rebase.ts')
        case 'diff --name-only --diff-filter=U':
          return ok('packages/runner/src/actions/rebase.ts\n')
        case 'rebase --abort':
          return ok('aborted')
        default:
          return fail(`unexpected git call: ${args.join(' ')}`)
      }
    })

    const result = await callAction(rebaseAction, context())
    expect(result.error).toBeDefined()
    expect(result.error).toMatchObject({ code: 'conflict' })
    expect(calls).not.toContain('rebase --abort')
    expect(result.error?.message).toContain('unresolved conflicts')
  })

  it('Conflict_WithRecovery_LeavesRebaseInProgressAndReturnsConflict', async (resources) => {
    const calls: string[] = []
    useRebaseExistsChecker(resources, () => false)
    installRebaseGitRunner(resources, async (_workDir, args) => {
      calls.push(args.join(' '))
      switch (args.join(' ')) {
        case 'rev-parse --git-path rebase-merge':
          return ok('/fake/worktree/.git/rebase-merge\n')
        case 'rev-parse --git-path rebase-apply':
          return ok('/fake/worktree/.git/rebase-apply\n')
        case 'rev-parse master':
          return ok('baseSha\n')
        case 'status --porcelain':
          return ok('')
        case 'rev-parse HEAD':
          return ok('before\n')
        case 'rebase master':
          return fail('CONFLICT (content): Merge conflict in packages/runner/src/actions/rebase.ts')
        case 'diff --name-only --diff-filter=U':
          return ok('packages/runner/src/actions/rebase.ts\npackages/runner/src/actions/git.ts\n')
        default:
          return fail(`unexpected git call: ${args.join(' ')}`)
      }
    })

    const result = await callAction(rebaseAction, context({}, {}, { budget: 1, handlers: [] }))
    expect(result.error).toBeDefined()
    expect(result.error).toMatchObject({ code: 'conflict' })
    expect(calls).not.toContain('rebase --abort')
  })

  it('Conflict_WithRecoveryOnlyInWith_AbortsBecauseRecoveryIsDispatchMetadata', async (resources) => {
    const calls: string[] = []
    useRebaseExistsChecker(resources, () => false)
    installRebaseGitRunner(resources, async (_workDir, args) => {
      calls.push(args.join(' '))
      switch (args.join(' ')) {
        case 'rev-parse --git-path rebase-merge':
          return ok('/fake/worktree/.git/rebase-merge\n')
        case 'rev-parse --git-path rebase-apply':
          return ok('/fake/worktree/.git/rebase-apply\n')
        case 'rev-parse master':
          return ok('baseSha\n')
        case 'status --porcelain':
          return ok('')
        case 'rev-parse HEAD':
          return ok('before\n')
        case 'rebase master':
          return fail('CONFLICT (content): Merge conflict in packages/runner/src/actions/rebase.ts')
        case 'diff --name-only --diff-filter=U':
          return ok('packages/runner/src/actions/rebase.ts\n')
        case 'rebase --abort':
          return ok('aborted')
        default:
          return fail(`unexpected git call: ${args.join(' ')}`)
      }
    })

    const result = await callAction(
      rebaseAction,
      context({
        recovery: { budget: 1, handlers: [] },
      }),
    )
    expect(result.error).toBeDefined()
    expect(result.error).toMatchObject({ code: 'conflict' })
    expect(calls).not.toContain('rebase --abort')
  })

  it('Conflict_WithRecovery_RerunAfterAbandonedInProgress_AbortsPriorRebaseThenStartsFresh', async (resources) => {
    const calls: string[] = []
    let rebaseStatePresent = true
    useRebaseExistsChecker(resources, (path) => path === '/fake/worktree/.git/rebase-merge' && rebaseStatePresent)
    installRebaseGitRunner(resources, async (_workDir, args) => {
      calls.push(args.join(' '))
      switch (args.join(' ')) {
        case 'rev-parse --git-path rebase-merge':
          return ok('/fake/worktree/.git/rebase-merge\n')
        case 'rev-parse --git-path rebase-apply':
          return ok('/fake/worktree/.git/rebase-apply\n')
        case 'rebase --abort':
          rebaseStatePresent = false
          return ok('aborted')
        case 'rev-parse master':
          return ok('baseSha\n')
        case 'status --porcelain':
          return ok('')
        case 'rev-parse HEAD':
          return ok('before\n')
        case 'rebase master':
          return fail('CONFLICT (content): Merge conflict in packages/runner/src/actions/rebase.ts')
        case 'diff --name-only --diff-filter=U':
          return ok('packages/runner/src/actions/rebase.ts\n')
        default:
          return fail(`unexpected git call: ${args.join(' ')}`)
      }
    })

    const result = await callAction(rebaseAction, context({}, {}, { budget: 1, handlers: [] }))
    expect(result.error).toBeDefined()
    expect(calls).toContain('rebase --abort')
    expect(result.error).toMatchObject({ code: 'conflict' })
  })

  it('Conflict_WithRecovery_SuccessfulRebase_ReportsNormal', async (resources) => {
    const calls: string[] = []
    useRebaseExistsChecker(resources, () => false)
    installRebaseGitRunner(resources, async (_workDir, args) => {
      calls.push(args.join(' '))
      switch (args.join(' ')) {
        case 'rev-parse --git-path rebase-merge':
          return ok('/fake/worktree/.git/rebase-merge\n')
        case 'rev-parse --git-path rebase-apply':
          return ok('/fake/worktree/.git/rebase-apply\n')
        case 'rev-parse master':
          return ok('baseSha\n')
        case 'status --porcelain':
          return ok('')
        case 'rev-parse HEAD':
          return ok(calls.filter((call) => call === 'rev-parse HEAD').length === 1 ? 'before\n' : 'after\n')
        case 'rebase master':
          return ok('Successfully rebased')
        default:
          return fail(`unexpected git call: ${args.join(' ')}`)
      }
    })

    const result = await callAction(rebaseAction, context({}, {}, { budget: 1, handlers: [] }))
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeUndefined()
    expect(output).toMatchObject({
      rebased: true,
      rebaseLeftInProgress: false,
    })
  })

  it('NetworkFetch_ReceivesTimeoutMsAndLocalProbesDoNot', async (resources) => {
    type GitCall = { command: string; timeoutMs: number | undefined }
    const calls: GitCall[] = []
    useRebaseExistsChecker(resources, () => false)
    installRebaseGitRunner(resources, async (_workDir, args, _signal, options) => {
      const command = args.join(' ')
      calls.push({ command, timeoutMs: options?.timeoutMs })
      switch (command) {
        case 'rev-parse --git-path rebase-merge':
          return ok('/fake/worktree/.git/rebase-merge\n')
        case 'rev-parse --git-path rebase-apply':
          return ok('/fake/worktree/.git/rebase-apply\n')
        case 'fetch origin master':
          return ok('')
        case 'rev-parse origin/master':
          return ok('baseSha\n')
        case 'status --porcelain':
          return ok('')
        case 'rev-parse HEAD':
          return ok('before\n')
        case 'rebase origin/master':
          return ok('Successfully rebased')
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })

    await callAction(rebaseAction, context({ baseBranch: 'master', remote: 'origin' }))

    const fetchCall = calls.find((c) => c.command === 'fetch origin master')
    const revParseBase = calls.find((c) => c.command === 'rev-parse origin/master')
    const statusCall = calls.find((c) => c.command === 'status --porcelain')
    const revParseHead = calls.find((c) => c.command === 'rev-parse HEAD')
    const rebaseCall = calls.find((c) => c.command === 'rebase origin/master')
    expect(fetchCall?.timeoutMs).toBe(NETWORK_COMMAND_TIMEOUT_MS)
    expect(revParseBase?.timeoutMs).toBeUndefined()
    expect(statusCall?.timeoutMs).toBeUndefined()
    expect(revParseHead?.timeoutMs).toBeUndefined()
    expect(rebaseCall?.timeoutMs).toBeUndefined()
  })

  it('FetchTimeout_ClassifiesAsRetrySafeAndSurfacesDuration', async (resources) => {
    useRebaseExistsChecker(resources, () => false)
    installRebaseGitRunner(resources, async (_workDir, args) => {
      const command = args.join(' ')
      switch (command) {
        case 'rev-parse --git-path rebase-merge':
          return ok('/fake/worktree/.git/rebase-merge\n')
        case 'rev-parse --git-path rebase-apply':
          return ok('/fake/worktree/.git/rebase-apply\n')
        case 'fetch origin master':
          return {
            success: false,
            stdout: '',
            stderr: `Command timed out after ${NETWORK_COMMAND_TIMEOUT_MS / 1000}s\n`,
            exitCode: 124,
            combinedOutput: `Command timed out after ${NETWORK_COMMAND_TIMEOUT_MS / 1000}s`,
            status: 'timeout' as const,
            timeoutMs: NETWORK_COMMAND_TIMEOUT_MS,
          }
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })

    const result = await callAction(rebaseAction, context({ baseBranch: 'master', remote: 'origin' }))
    const output = result.output as Record<string, unknown>

    expect(result.error).toMatchObject({ code: 'timeout' })
    expect(result.error?.message).toContain('Rebase operation timed out')
  })

  it('LocalBasePath_RebaseDoesNotCarryTimeoutMs', async (resources) => {
    type GitCall = { command: string; timeoutMs: number | undefined }
    const calls: GitCall[] = []
    useRebaseExistsChecker(resources, () => false)
    installRebaseGitRunner(resources, async (_workDir, args, _signal, options) => {
      const command = args.join(' ')
      calls.push({ command, timeoutMs: options?.timeoutMs })
      switch (command) {
        case 'rev-parse --git-path rebase-merge':
          return ok('/fake/worktree/.git/rebase-merge\n')
        case 'rev-parse --git-path rebase-apply':
          return ok('/fake/worktree/.git/rebase-apply\n')
        case 'rev-parse master':
          return ok('baseSha\n')
        case 'status --porcelain':
          return ok('')
        case 'rev-parse HEAD':
          return ok(calls.filter((c) => c.command === 'rev-parse HEAD').length === 1 ? 'before\n' : 'after\n')
        case 'rebase master':
          return ok('Successfully rebased')
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })

    await callAction(rebaseAction, context())

    for (const call of calls) {
      expect(call.timeoutMs).toBeUndefined()
    }
    expect(calls.some((c) => c.command.startsWith('fetch'))).toBe(false)
  })
})

describe('mohist/rebase stateful fake worktree', () => {
  function installFake(resources: RebaseTestResources, fake: StatefulFakeWorktree): void {
    resources.rebaseGitRunner = fake.gitRunner
    resources.rebaseExistsChecker = fake.existsChecker
  }

  it('StatefulExpectedBranchSuccess_LocalRebaseReportsCompletedAfterVerify', async (resources) => {
    const fake = new StatefulFakeWorktree()
    fake.configure('/fake/worktree', {
      branch: EXPECTED_BRANCH,
      branches: [EXPECTED_BRANCH],
      revs: { master: 'baseSha' },
    })
    installFake(resources, fake)

    const result = await callAction(rebaseAction, context())
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeUndefined()
    expect(output).toMatchObject({
      kind: 'rebase',
      status: 'completed',
      baseRef: 'master',
      rebased: true,
      conflicts: [],
    })
    expect(fake.hasCommand('rebase master')).toBe(true)
    // The completion invariant was probed and the workspace stayed attached.
    expect(fake.state('/fake/worktree')?.branch).toBe(EXPECTED_BRANCH)
    expect(fake.state('/fake/worktree')?.porcelain).toBe('')
    expect(fake.state('/fake/worktree')?.residual.rebaseMerge).toBe(false)
  })

  it('StatefulExpectedBranchSuccess_RemoteRebaseReportsCompletedAfterVerify', async (resources) => {
    const fake = new StatefulFakeWorktree()
    fake.configure('/fake/worktree', {
      branch: EXPECTED_BRANCH,
      branches: [EXPECTED_BRANCH],
      revs: { 'origin/master': 'baseShaRemote' },
    })
    installFake(resources, fake)

    const result = await callAction(rebaseAction, context({ baseBranch: 'master', remote: 'origin' }))
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeUndefined()
    expect(output).toMatchObject({ baseRef: 'origin/master', rebased: true })
    expect(fake.hasCommand('fetch origin master')).toBe(true)
    expect(fake.hasCommand('rebase origin/master')).toBe(true)
    expect(fake.state('/fake/worktree')?.branch).toBe(EXPECTED_BRANCH)
  })

  it('StatefulExpectedBranchSuccess_SquashReportsCompletedOnlyAfterVerify', async (resources) => {
    const fake = new StatefulFakeWorktree()
    fake.configure('/fake/worktree', {
      branch: EXPECTED_BRANCH,
      branches: [EXPECTED_BRANCH],
      revs: { master: 'baseSha' },
    })
    installFake(resources, fake)

    const result = await callAction(rebaseAction, context({ squash: true, message: 'Squash it' }))
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeUndefined()
    expect(output).toMatchObject({ squashed: true, rebased: true })
    expect(fake.hasCommand('reset --soft baseSha')).toBe(true)
    expect(fake.hasCommand('commit -m Squash it')).toBe(true)
    // Success is reported only after the completion invariant probes pass.
    expect(fake.state('/fake/worktree')?.branch).toBe(EXPECTED_BRANCH)
    expect(fake.state('/fake/worktree')?.porcelain).toBe('')
  })

  it('StatefulDetachedCompletion_ReportsBranchIntegrityFailureWithoutSuccessOutput', async (resources) => {
    const fake = new StatefulFakeWorktree()
    fake.configure('/fake/worktree', {
      branch: EXPECTED_BRANCH,
      branches: [EXPECTED_BRANCH],
      revs: { master: 'baseSha' },
    })
    // The rebase command itself succeeds but leaves HEAD detached.
    fake.rebaseSimulation = { successBranch: null }
    installFake(resources, fake)

    const result = await callAction(rebaseAction, context())

    expect(result.error).toBeDefined()
    expect(result.error).toMatchObject({ code: 'branch-invariant-violation' })
    expect(result.error?.message).toContain(`expectedBranch=${EXPECTED_BRANCH}`)
    expect(result.error?.message).toContain('observedBranch=(detached)')
    expect(result.error?.message).toContain('observedRef=detached-after-rebase')
    // Successful rebase output is never exposed.
    expect(result.output).toBeUndefined()
  })

  it('StatefulDetachedCompletionAfterSquash_ReportsBranchIntegrityFailure', async (resources) => {
    const fake = new StatefulFakeWorktree()
    fake.configure('/fake/worktree', {
      branch: EXPECTED_BRANCH,
      branches: [EXPECTED_BRANCH],
      revs: { master: 'baseSha' },
    })
    fake.rebaseSimulation = { successBranch: null }
    installFake(resources, fake)

    const result = await callAction(rebaseAction, context({ squash: true, message: 'Squash it' }))

    expect(result.error).toBeDefined()
    expect(result.error).toMatchObject({ code: 'branch-invariant-violation' })
    expect(result.error?.message).toContain('observedBranch=(detached)')
    expect(result.output).toBeUndefined()
  })

  it('StatefulWrongBranchCompletion_ReportsBranchIntegrityFailure', async (resources) => {
    const fake = new StatefulFakeWorktree()
    fake.configure('/fake/worktree', {
      branch: EXPECTED_BRANCH,
      branches: [EXPECTED_BRANCH, 'feature/other'],
      revs: { master: 'baseSha' },
    })
    fake.rebaseSimulation = { successBranch: 'feature/other' }
    installFake(resources, fake)

    const result = await callAction(rebaseAction, context())

    expect(result.error).toBeDefined()
    expect(result.error).toMatchObject({ code: 'branch-invariant-violation' })
    expect(result.error?.message).toContain(`expectedBranch=${EXPECTED_BRANCH}`)
    expect(result.error?.message).toContain('observedBranch=feature/other')
    expect(result.error?.message).toContain('observedRef=feature/other')
    expect(result.output).toBeUndefined()
  })

  it('StatefulConflict_ReturnsConflictFailureAndLeavesResidualForResolver', async (resources) => {
    const fake = new StatefulFakeWorktree()
    fake.configure('/fake/worktree', {
      branch: EXPECTED_BRANCH,
      branches: [EXPECTED_BRANCH],
      revs: { master: 'baseSha' },
    })
    fake.rebaseSimulation = { conflictFiles: ['packages/runner/src/actions/rebase.ts'] }
    installFake(resources, fake)

    const result = await callAction(rebaseAction, context())

    expect(result.error).toBeDefined()
    expect(result.error).toMatchObject({ code: 'conflict' })
    expect(result.error?.message).toContain('unresolved conflicts')
    expect(result.error?.message).toContain('packages/runner/src/actions/rebase.ts')
    // The conflict state is preserved for a resolver — never cleaned and
    // never represented as successful recovery.
    expect(result.output).toBeUndefined()
    expect(fake.state('/fake/worktree')?.residual.rebaseMerge).toBe(true)
    expect(fake.hasCommand('rebase --abort')).toBe(false)
  })

  it('StatefulFinalProbeFailure_ReportsBranchIntegrityFailureWithoutSuccessOutput', async (resources) => {
    const fake = new StatefulFakeWorktree()
    fake.configure('/fake/worktree', {
      branch: EXPECTED_BRANCH,
      branches: [EXPECTED_BRANCH],
      revs: { master: 'baseSha' },
    })
    // The branch probe runs only as part of the completion snapshot, so
    // failing it exercises the final-probe failure path after a successful
    // rebase.
    fake.fail((args) => args.join(' ') === 'rev-parse --abbrev-ref HEAD', 'fatal: not a git repository')
    installFake(resources, fake)

    const result = await callAction(rebaseAction, context())

    expect(result.error).toBeDefined()
    expect(result.error).toMatchObject({ code: 'branch-invariant-violation' })
    expect(result.error?.message).toContain('git rev-parse --abbrev-ref HEAD failed')
    expect(result.error?.message).toContain('operation=head-ref')
    expect(result.output).toBeUndefined()
  })
})

function context(
  withOverrides: JsonObject = {},
  variables: JsonObject = {},
  recovery: JsonObject | null = null,
): ActionContext {
  return {
    workflowRunId: 'workflow-1',
    workId: 'rebase.1',
    workType: 'task',
    stage: 'check',
    title: 'Rebase onto master',
    uses: 'mohist/rebase',
    with: { baseBranch: 'master', expectedBranch: EXPECTED_BRANCH, ...withOverrides },
    variables: {
      project: { id: 'proj_1' },
      issue: { number: 217 },
      workspace: { path: '/fake/worktree', branch: EXPECTED_BRANCH, changeDir: null },
      ...variables,
    },
    workDir: '/fake/worktree',
    recovery,
    projectId: 'proj_1',
    issueNumber: 217,
    signal: new AbortController().signal,
    writeVars: async () => {},
  }
}

function ok(stdout: string) {
  return { success: true, stdout, stderr: '', exitCode: 0, combinedOutput: stdout.trim() }
}

function fail(stderr: string) {
  return { success: false, stdout: '', stderr, exitCode: 1, combinedOutput: stderr }
}
