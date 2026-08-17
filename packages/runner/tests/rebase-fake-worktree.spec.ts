import { describe, expect, it as vitestIt } from 'vitest'
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

function fail(stderr: string) {
  return { success: false, stdout: '', stderr, exitCode: 1, combinedOutput: stderr }
}
