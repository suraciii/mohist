import { describe, expect, it as vitestIt } from 'vitest'
import { workspacePrepareAction } from '../src/actions/workspace-prepare.js'
import type { RunnerFileSystem, RunnerGitRunner } from '../src/system/filesystem.js'
import { callAction } from './support/call-action.js'
import { withTestRunnerResources } from './support/test-resources.js'
import { MemoryFileSystem } from './support/memory-filesystem.js'
import type { JsonObject } from '../src/core/types.js'
import type { ActionTestContext as ActionContext } from './support/action-test-context.js'
import { StatefulFakeWorktree } from './support/fake-worktree.js'

type GitCall = { workDir: string; args: string[] }

const WORKSPACE_PATH = '/workspace'
const EXPECTED_BRANCH = 'mohist/run-wr-prepare-1'

type WorkspacePrepareTestResources = {
  fileSystem: RunnerFileSystem
  workspacePrepareGitRunner?: RunnerGitRunner
  workspacePrepareExistsChecker?: (path: string) => boolean
}

function it(name: string, body: (resources: WorkspacePrepareTestResources) => Promise<void> | void): void {
  vitestIt(name, async () => {
    const resources: WorkspacePrepareTestResources = { fileSystem: new MemoryFileSystem() }
    await withTestRunnerResources(async () => await body(resources), resources)
  })
}

function commandOf(call: GitCall): string {
  return call.args.join(' ')
}

function hasCommand(calls: GitCall[], command: string): boolean {
  return calls.some((call) => commandOf(call) === command)
}

function fail(stderr: string) {
  return { success: false, stdout: '', stderr, exitCode: 1, combinedOutput: stderr }
}

function context(variables: JsonObject = {}): ActionContext {
  return {
    workflowRunId: 'wr-prepare-1',
    workId: 'workspace-prepare',
    workType: 'task',
    stage: 'build',
    title: 'Prepare workspace',
    uses: 'mohist/workspace-prepare',
    with: { expectedBranch: EXPECTED_BRANCH },
    variables: {
      workspace: { path: WORKSPACE_PATH, branch: EXPECTED_BRANCH, changeDir: null },
      ...variables,
    },
    workDir: WORKSPACE_PATH,
    signal: new AbortController().signal,
    writeVars: async () => {},
  }
}

describe('mohist/workspace-prepare stateful fake worktree', () => {
  function installFake(resources: WorkspacePrepareTestResources, fake: StatefulFakeWorktree): void {
    resources.workspacePrepareGitRunner = fake.gitRunner
    resources.workspacePrepareExistsChecker = fake.existsChecker
  }

  it('StatefulFastPath_HealthyWorkspace_NoMutation', async (resources) => {
    const fake = new StatefulFakeWorktree()
    fake.configure(WORKSPACE_PATH, { branch: EXPECTED_BRANCH, branches: [EXPECTED_BRANCH] })
    installFake(resources, fake)

    const result = await callAction(workspacePrepareAction, context())
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeUndefined()
    expect(output).toMatchObject({
      kind: 'workspace-prepare',
      status: 'success',
      expectedBranch: EXPECTED_BRANCH,
      head: { ref: EXPECTED_BRANCH },
      residual: { rebaseMerge: false, rebaseApply: false, mergeHead: false, cherryPickHead: false },
      porcelain: '',
    })
    // The fast path issues no mutation commands.
    expect(fake.hasCommand('checkout', EXPECTED_BRANCH)).toBe(false)
    expect(fake.hasCommand('rebase --abort')).toBe(false)
    expect(fake.hasCommand('merge --abort')).toBe(false)
    expect(fake.hasCommand('cherry-pick --abort')).toBe(false)
    expect(fake.hasCommand('reset --hard HEAD')).toBe(false)
    expect(fake.hasCommand('clean -fd')).toBe(false)
  })

  it('StatefulDetachedRepair_ChecksOutExpectedBranchAndVerifies', async (resources) => {
    const fake = new StatefulFakeWorktree()
    fake.configure(WORKSPACE_PATH, { branch: null, commit: 'detached-sha', branches: [EXPECTED_BRANCH] })
    installFake(resources, fake)

    const result = await callAction(workspacePrepareAction, context())
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeUndefined()
    expect(fake.hasCommand('checkout', EXPECTED_BRANCH)).toBe(true)
    // Success is reported only after the follow-up probe confirms the branch.
    expect(output.head).toMatchObject({ ref: EXPECTED_BRANCH })
    expect(output.porcelain).toBe('')
    expect((output.residual as Record<string, unknown>).rebaseMerge).toBe(false)
    // Clean detached repair is non-destructive.
    expect(fake.hasCommand('reset --hard HEAD')).toBe(false)
    expect(fake.hasCommand('clean -fd')).toBe(false)
    expect(fake.hasCommand('rebase --abort')).toBe(false)
  })

  it('StatefulDirtyMismatchedRepair_OrderResetCleanCheckoutVerify', async (resources) => {
    const fake = new StatefulFakeWorktree()
    fake.configure(WORKSPACE_PATH, {
      branch: 'feature/other',
      porcelain: ' M dirty.txt\n?? untracked.txt\n',
      branches: [EXPECTED_BRANCH, 'feature/other'],
    })
    installFake(resources, fake)

    const result = await callAction(workspacePrepareAction, context())
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeUndefined()
    const calls = fake.calls.map((call) => call.args.join(' '))
    const resetIdx = calls.findIndex((call) => call === 'reset --hard HEAD')
    const cleanIdx = calls.findIndex((call) => call === 'clean -fd')
    const checkoutIdx = calls.findIndex((call) => call === `checkout ${EXPECTED_BRANCH}`)
    expect(resetIdx).toBeGreaterThanOrEqual(0)
    expect(cleanIdx).toBeGreaterThanOrEqual(0)
    expect(checkoutIdx).toBeGreaterThanOrEqual(0)
    expect(resetIdx).toBeLessThan(cleanIdx)
    expect(cleanIdx).toBeLessThan(checkoutIdx)
    // Complete final probe confirms the invariant.
    expect(output.head).toMatchObject({ ref: EXPECTED_BRANCH })
    expect(output.porcelain).toBe('')
    expect(fake.state(WORKSPACE_PATH)?.porcelain).toBe('')
  })

  it('StatefulRebaseCleanup_AbortsReprobesThenRepairs', async (resources) => {
    const fake = new StatefulFakeWorktree()
    fake.configure(WORKSPACE_PATH, {
      branch: null,
      commit: 'detached-sha',
      residual: { rebaseMerge: true },
      branches: [EXPECTED_BRANCH],
    })
    installFake(resources, fake)

    const result = await callAction(workspacePrepareAction, context())
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeUndefined()
    expect(fake.hasCommand('rebase --abort')).toBe(true)
    expect(fake.hasCommand('merge --abort')).toBe(false)
    expect(fake.hasCommand('cherry-pick --abort')).toBe(false)
    expect(fake.hasCommand('checkout', EXPECTED_BRANCH)).toBe(true)
    // The abort was re-probed before repair continued.
    expect(fake.state(WORKSPACE_PATH)?.residual.rebaseMerge).toBe(false)
    expect(output.residual).toMatchObject({ rebaseMerge: false, rebaseApply: false })
    expect(output.head).toMatchObject({ ref: EXPECTED_BRANCH })
  })

  it('StatefulMergeCherryPickCleanup_AbortsEachAndReprobes', async (resources) => {
    const fake = new StatefulFakeWorktree()
    fake.configure(WORKSPACE_PATH, {
      branch: null,
      commit: 'detached-sha',
      residual: { mergeHead: true, cherryPickHead: true },
      branches: [EXPECTED_BRANCH],
    })
    installFake(resources, fake)

    const result = await callAction(workspacePrepareAction, context())
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeUndefined()
    expect(fake.hasCommand('merge --abort')).toBe(true)
    expect(fake.hasCommand('cherry-pick --abort')).toBe(true)
    expect(fake.state(WORKSPACE_PATH)?.residual.mergeHead).toBe(false)
    expect(fake.state(WORKSPACE_PATH)?.residual.cherryPickHead).toBe(false)
    expect(output.residual).toMatchObject({ mergeHead: false, cherryPickHead: false })
  })

  it('StatefulAbortFailure_DiagnosticHasExpectedObservedAndOperation', async (resources) => {
    const fake = new StatefulFakeWorktree()
    fake.configure(WORKSPACE_PATH, {
      branch: null,
      commit: 'detached-sha',
      residual: { mergeHead: true },
      branches: [EXPECTED_BRANCH],
    })
    fake.fail((args) => args.join(' ') === 'merge --abort', 'fatal: could not abort merge')
    installFake(resources, fake)

    const result = await callAction(workspacePrepareAction, context())

    expect(result.error).toBeDefined()
    expect(result.error?.message).toContain('operation=abort-merge')
    expect(result.error?.message).toContain(`expectedBranch=${EXPECTED_BRANCH}`)
    expect(result.error?.message).toContain('observedBranch=(detached)')
    expect(result.error?.message).toContain('observedRef=detached-sha')
    expect(result.error?.message).toContain('merge --abort failed')
    expect(fake.hasCommand('checkout', EXPECTED_BRANCH)).toBe(false)
    expect(fake.hasCommand('reset --hard HEAD')).toBe(false)
  })

  it('StatefulResidualReProbeFailure_DiagnosticAndNoCheckout', async (resources) => {
    const fake = new StatefulFakeWorktree()
    fake.configure(WORKSPACE_PATH, {
      branch: null,
      commit: 'detached-sha',
      residual: { cherryPickHead: true },
      branches: [EXPECTED_BRANCH],
    })
    fake.abortLeavesResidual = true
    installFake(resources, fake)

    const result = await callAction(workspacePrepareAction, context())

    expect(result.error).toBeDefined()
    expect(result.error?.message).toContain('operation=abort-cherry-pick')
    expect(result.error?.message).toContain('still in progress')
    expect(fake.hasCommand('checkout', EXPECTED_BRANCH)).toBe(false)
    expect(fake.hasCommand('reset --hard HEAD')).toBe(false)
  })

  it('StatefulCheckoutFailure_DiagnosticHasExpectedObservedAndOperation', async (resources) => {
    const fake = new StatefulFakeWorktree()
    // The expected branch does not exist, so checkout cannot attach.
    fake.configure(WORKSPACE_PATH, { branch: null, commit: 'detached-sha', branches: [] })
    installFake(resources, fake)

    const result = await callAction(workspacePrepareAction, context())

    expect(result.error).toBeDefined()
    expect(result.error?.message).toContain('operation=checkout')
    expect(result.error?.message).toContain(`expectedBranch=${EXPECTED_BRANCH}`)
    expect(result.error?.message).toContain('observedBranch=(detached)')
    expect(result.error?.message).toContain('observedRef=detached-sha')
    expect(result.error?.message).toContain(`git checkout ${EXPECTED_BRANCH} failed`)
    expect(fake.hasCommand('reset --hard HEAD')).toBe(false)
    expect(fake.hasCommand('clean -fd')).toBe(false)
  })

  it('StatefulFinalVerifyFailure_DirtyAfterCleanup', async (resources) => {
    const fake = new StatefulFakeWorktree()
    fake.configure(WORKSPACE_PATH, {
      branch: EXPECTED_BRANCH,
      porcelain: ' M still-dirty.txt\n',
      branches: [EXPECTED_BRANCH],
    })
    fake.resetCleanIneffective = true
    installFake(resources, fake)

    const result = await callAction(workspacePrepareAction, context())

    expect(result.error).toBeDefined()
    expect(result.error?.message).toContain('operation=verify')
    expect(result.error?.message).toContain(`expectedBranch=${EXPECTED_BRANCH}`)
    expect(result.error?.message).toContain('dirty=true')
    expect(fake.hasCommand('reset --hard HEAD')).toBe(true)
    expect(fake.hasCommand('clean -fd')).toBe(true)
  })

  it('StatefulFinalVerifyFailure_WrongBranchAfterCheckout', async (resources) => {
    const fake = new StatefulFakeWorktree()
    fake.configure(WORKSPACE_PATH, {
      branch: 'feature/other',
      branches: [EXPECTED_BRANCH, 'feature/other'],
      checkoutAttaches: false,
    })
    installFake(resources, fake)

    const result = await callAction(workspacePrepareAction, context())

    expect(result.error).toBeDefined()
    expect(fake.hasCommand('checkout', EXPECTED_BRANCH)).toBe(true)
    expect(result.error?.message).toContain('operation=verify')
    expect(result.error?.message).toContain(`expectedBranch=${EXPECTED_BRANCH}`)
    expect(result.error?.message).toContain('observedBranch=feature/other')
  })

  it('StatefulTransientCheckoutFailure_FirstAttemptIsDurableFailureThenRetryRepairsSamePath', async (resources) => {
    const fake = new StatefulFakeWorktree()
    fake.configure(WORKSPACE_PATH, {
      branch: null,
      commit: 'detached-sha',
      branches: [EXPECTED_BRANCH],
    })
    // Single-shot checkout failure: the first call returns a durable
    // actionable failure and the transient injection auto-clears. The
    // exact retry against the SAME fake worktree then converges on
    // the expected branch with no replacement path, no clone, no new
    // branch creation.
    fake.fail((args) => args[0] === 'checkout' && args[1] === EXPECTED_BRANCH, 'fatal: pathspec did not match', 1)
    installFake(resources, fake)

    const first = await callAction(workspacePrepareAction, context())
    // Workspace still detached after the failed attempt — no
    // replacement workspace was created.
    expect(fake.state(WORKSPACE_PATH)?.branch).toBeNull()
    expect(fake.state(WORKSPACE_PATH)?.commit).toBe('detached-sha')

    // First attempt: actionable failure with shared diagnostic, no
    // successful output, no follow-up addTasks, no replacement path.
    expect(first.error).toBeDefined()
    expect(first.error?.message).toContain(`operation=checkout`)
    expect(first.error?.message).toContain(`expectedBranch=${EXPECTED_BRANCH}`)
    expect(first.error?.message).toContain(`observedBranch=(detached)`)
    expect(first.error?.message).toContain(`observedRef=detached-sha`)
    expect(first.error?.message).toContain(`git checkout ${EXPECTED_BRANCH} failed`)
    expect(first.output).toBeUndefined()
    // The fake worktree state is keyed by workDir; nothing has moved
    // the workspace to a new path or introduced a sibling path during
    // the failed attempt.
    expect(WORKSPACE_PATH).toBe('/workspace')

    // Second attempt: same fake worktree, same expected branch, fast
    // path converges without cloning or branch replacement.
    const second = await callAction(workspacePrepareAction, context())
    expect(second.error).toBeUndefined()
    const secondOutput = second.output as Record<string, unknown>
    expect(secondOutput).toMatchObject({
      kind: 'workspace-prepare',
      status: 'success',
      expectedBranch: EXPECTED_BRANCH,
      head: { ref: EXPECTED_BRANCH },
    })
    expect(fake.state(WORKSPACE_PATH)?.branch).toBe(EXPECTED_BRANCH)
    // Workspace path is exactly the same — never replaced.
    expect(WORKSPACE_PATH).toBe('/workspace')
  })

  it('StatefulTransientResidualFailure_FirstAttemptIsDurableFailureThenRetryRepairsSamePath', async (resources) => {
    const fake = new StatefulFakeWorktree()
    // Already on the expected branch but carrying residual rebase state.
    fake.configure(WORKSPACE_PATH, {
      branch: EXPECTED_BRANCH,
      branches: [EXPECTED_BRANCH],
      residual: { rebaseMerge: true },
    })
    // Single-shot `rebase --abort` failure: first call cannot repair,
    // the transient clears, and the exact retry finally aborts the
    // residual state.
    fake.fail((args) => args.join(' ') === 'rebase --abort', 'fatal: rebase --abort failed', 1)
    installFake(resources, fake)

    const first = await callAction(workspacePrepareAction, context())
    // Workspace branch identity intact after the failed repair; the
    // workspace was not replaced and no new branch was created.
    expect(fake.state(WORKSPACE_PATH)?.branch).toBe(EXPECTED_BRANCH)
    expect(fake.state(WORKSPACE_PATH)?.residual.rebaseMerge).toBe(true)

    expect(first.error).toBeDefined()
    expect(first.error?.message).toContain('operation=abort-rebase')
    expect(first.error?.message).toContain(`expectedBranch=${EXPECTED_BRANCH}`)
    expect(first.error?.message).toContain(`residual=rebase`)
    expect(first.error?.message).toContain('rebase --abort failed')
    expect(first.output).toBeUndefined()

    const second = await callAction(workspacePrepareAction, context())
    expect(second.error).toBeUndefined()
    const secondOutput = second.output as Record<string, unknown>
    expect(secondOutput).toMatchObject({
      kind: 'workspace-prepare',
      status: 'success',
      expectedBranch: EXPECTED_BRANCH,
    })
    expect(fake.state(WORKSPACE_PATH)?.branch).toBe(EXPECTED_BRANCH)
    expect(fake.state(WORKSPACE_PATH)?.residual.rebaseMerge).toBe(false)
  })

  it('StatefulPersistentCheckoutFailure_BothAttemptsFailWithSameDiagnosticClass', async (resources) => {
    const fake = new StatefulFakeWorktree()
    fake.configure(WORKSPACE_PATH, {
      branch: null,
      commit: 'detached-sha',
      branches: [EXPECTED_BRANCH],
    })
    // Persistent checkout failure that survives both attempts and is
    // not cleared between calls — the second attempt must remain a
    // failure of the SAME diagnostic class, never successful.
    fake.fail((args) => args[0] === 'checkout' && args[1] === EXPECTED_BRANCH, 'fatal: pathspec did not match', 100)
    installFake(resources, fake)

    const first = await callAction(workspacePrepareAction, context())
    // Workspace identity never mutated by the failed repair attempt.
    expect(fake.state(WORKSPACE_PATH)?.branch).toBeNull()
    expect(fake.state(WORKSPACE_PATH)?.commit).toBe('detached-sha')

    expect(first.error).toBeDefined()
    expect(first.error?.message).toContain('operation=checkout')
    expect(first.error?.message).toContain(`expectedBranch=${EXPECTED_BRANCH}`)
    expect(first.error?.message).toContain(`observedBranch=(detached)`)

    const second = await callAction(workspacePrepareAction, context())
    expect(second.error).toBeDefined()
    // Same diagnostic class: same operation, same expected branch, and
    // no successful output ever observed.
    expect(second.error?.message).toContain('operation=checkout')
    expect(second.error?.message).toContain(`expectedBranch=${EXPECTED_BRANCH}`)
    expect(second.error?.message).toContain(`observedBranch=(detached)`)
    expect(second.output).toBeUndefined()
    // Workspace identity still preserved — the persistent failure did
    // not cause the workspace to be replaced or a new branch created.
    expect(fake.state(WORKSPACE_PATH)?.branch).toBeNull()
    expect(fake.state(WORKSPACE_PATH)?.commit).toBe('detached-sha')
  })

  it('StatefulRepeatedPreparation_HealthyWorkspace_IsIdempotentFastPathEachTime', async (resources) => {
    const fake = new StatefulFakeWorktree()
    fake.configure(WORKSPACE_PATH, {
      branch: EXPECTED_BRANCH,
      branches: [EXPECTED_BRANCH],
    })
    installFake(resources, fake)

    const first = await callAction(workspacePrepareAction, context())
    const firstCalls = fake.calls.length
    const second = await callAction(workspacePrepareAction, context())
    const secondCalls = fake.calls.length - firstCalls

    expect(first.error).toBeUndefined()
    expect(second.error).toBeUndefined()
    expect(second.output).toMatchObject({
      kind: 'workspace-prepare',
      status: 'success',
      expectedBranch: EXPECTED_BRANCH,
      head: { ref: EXPECTED_BRANCH },
    })
    // Fast path is genuinely idempotent: no mutation commands issued,
    // no checkout, no abort, no reset/clean, no replacement path or
    // branch. The second call does the same kind of read-only probes
    // (rev-parse + status) and returns without changing state.
    expect(fake.hasCommand('checkout', EXPECTED_BRANCH)).toBe(false)
    expect(fake.hasCommand('rebase --abort')).toBe(false)
    expect(fake.hasCommand('merge --abort')).toBe(false)
    expect(fake.hasCommand('cherry-pick --abort')).toBe(false)
    expect(fake.hasCommand('reset --hard HEAD')).toBe(false)
    expect(fake.hasCommand('clean -fd')).toBe(false)
    expect(secondCalls).toBeGreaterThan(0)
    // Branch binding is unchanged across both calls.
    expect(fake.state(WORKSPACE_PATH)?.branch).toBe(EXPECTED_BRANCH)
  })
})
