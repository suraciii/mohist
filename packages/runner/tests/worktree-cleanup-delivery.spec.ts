import { describe, expect, it as vitestIt } from 'vitest'
import { WorkExecutor } from '../src/runtime/executor.js'
import { rebaseAction } from '../src/actions/rebase.js'
import { pushAction } from '../src/actions/push.js'
import { verifyOnlyWorkspaceManager } from './support/workspace-mock.js'
import type { ActionResult, JsonObject, DispatchWorkItem } from '../src/core/types.js'
import type { ActionTestContext as ActionContext } from './support/action-test-context.js'
import type { ActionHost } from '../src/actions/host.js'
import type { ServerConnection } from '../src/server/connection.js'
import type { CleanupAgentAction } from '../src/runtime/worktree-enforcement.js'
import type { RunnerFileSystem, RunnerGitRunner } from '../src/system/filesystem.js'
import { defineTestActions, type ActionRegistry, type TestActionDefinition } from './support/action-registry-test.js'
import { callAction } from './support/call-action.js'
import { MemoryFileSystem } from './support/memory-filesystem.js'
import { withTestRunnerResources } from './support/test-resources.js'

interface FakeWorktree {
  workDir: string
  branch: string
  staged: string[]
  unstaged: string[]
  untracked: string[]
  cleanupCommits: { files: string[]; sha: string }[]
}

type WorktreeTestResources = {
  fileSystem: RunnerFileSystem
  gitRunner?: RunnerGitRunner
  rebaseGitRunner?: RunnerGitRunner
  rebaseExistsChecker?: (path: string) => boolean
  pushGitRunner?: RunnerGitRunner
  cleanupAgentAction?: CleanupAgentAction
}

function it(name: string, body: (resources: WorktreeTestResources) => Promise<void> | void): void {
  vitestIt(name, async () => {
    const resources: WorktreeTestResources = { fileSystem: new MemoryFileSystem() }
    await withTestRunnerResources(async () => await body(resources), resources)
  })
}

function createFakeWorktree(): FakeWorktree {
  return {
    workDir: '/virtual/worktree-cleanup',
    branch: 'mo/worktree-cleanup',
    staged: [],
    unstaged: [],
    untracked: [],
    cleanupCommits: [],
  }
}

function installExecutorGit(resources: WorktreeTestResources, state: FakeWorktree) {
  resources.gitRunner = async (workDir, args) => {
    expect(workDir).toBe(state.workDir)
    switch (args.join(' ')) {
      case 'rev-parse --git-path rebase-merge':
      case 'rev-parse --git-path rebase-apply':
      case 'rev-parse --git-path MERGE_HEAD':
      case 'rev-parse --git-path CHERRY_PICK_HEAD':
        return gitOk(`${workDir}/.git/${args[2] ?? ''}\n`)
      case 'rev-parse HEAD':
        return gitOk('cleanup-head-sha\n')
      case 'rev-parse --abbrev-ref HEAD':
        return gitOk(`${state.branch}\n`)
      case 'status --porcelain':
        return gitOk('')
      case 'rev-parse --is-inside-work-tree':
        return gitOk('true\n')
      case 'diff --cached --name-only':
        return gitOk(fileList(state.staged))
      case 'diff --name-only':
        return gitOk(fileList(state.unstaged))
      case 'ls-files --others --exclude-standard':
        return gitOk(fileList(state.untracked))
      case 'rev-parse --git-path index.lock':
        return gitOk('/fake/worktree/.git/index.lock\n')
      default:
        throw new Error(`unexpected executor git call: ${args.join(' ')}`)
    }
  }
}

function fileList(files: string[]) {
  return files.length === 0 ? '' : `${files.join('\n')}\n`
}

function commitCleanup(state: FakeWorktree, files: string[], sha: string) {
  expect(state.untracked).toEqual(files)
  state.staged = []
  state.unstaged = []
  state.untracked = []
  state.cleanupCommits.push({ files, sha })
}

function buildRegistry(
  handlers: Record<string, TestActionDefinition | ((inputs: JsonObject, host: ActionHost) => Promise<ActionResult>)>,
): ActionRegistry {
  return defineTestActions(handlers)
}

function buildExecutor(
  registry: ActionRegistry,
  worktree: FakeWorktree,
  connection: Pick<ServerConnection, 'uploadArtifact' | 'report'>,
): WorkExecutor {
  return new WorkExecutor(
    registry,
    verifyOnlyWorkspaceManager({ path: worktree.workDir, branch: worktree.branch }),
    connection as never,
    worktree.workDir,
  )
}

function buildWork(worktree: FakeWorktree, overrides: Partial<DispatchWorkItem> = {}): DispatchWorkItem {
  return {
    workflowRunId: 'wf-worktree-cleanup',
    workId: 'build:agent.1',
    workType: 'task',
    title: 'Agent-backed task',
    uses: 'mohist/opencode',
    with: { prompt: 'do the work' },
    variables: {
      workspace: { path: worktree.workDir, branch: worktree.branch, changeDir: null },
      project: { path: worktree.workDir },
      issue: { title: 'Worktree cleanup delivery', number: 42 },
    },
    ...overrides,
  }
}

function rebaseContext(worktree: FakeWorktree, overrides: JsonObject = {}, variables: JsonObject = {}): ActionContext {
  return {
    workflowRunId: 'wf-worktree-cleanup',
    workId: 'integrate:rebase.1',
    workType: 'task',
    stage: 'integrate',
    title: 'Rebase and squash branch',
    uses: 'mohist/rebase',
    with: {
      baseBranch: 'master',
      remote: 'origin',
      squash: true,
      message: 'Complete worktree cleanup',
      expectedBranch: worktree.branch,
      ...overrides,
    },
    variables: {
      project: { path: worktree.workDir },
      workspace: { path: worktree.workDir, branch: worktree.branch, changeDir: null },
      issue: { title: 'Worktree cleanup delivery', number: 42 },
      ...variables,
    },
    workDir: worktree.workDir,
    signal: new AbortController().signal,
    writeVars: async () => {},
  }
}

function pushContext(worktree: FakeWorktree, overrides: JsonObject = {}, variables: JsonObject = {}): ActionContext {
  return {
    workflowRunId: 'wf-worktree-cleanup',
    workId: 'integrate:push.1',
    workType: 'task',
    stage: 'integrate',
    title: 'Push changes',
    uses: 'mohist/push',
    with: { source: worktree.branch, target: 'master', remote: 'origin', ...overrides },
    variables: {
      project: { path: '/not/the/workspace' },
      repository: { baseBranch: 'master' },
      workspace: { path: worktree.workDir, branch: worktree.branch, changeDir: null },
      issue: { title: 'Worktree cleanup delivery', number: 42 },
      ...variables,
    },
    workDir: worktree.workDir,
    signal: new AbortController().signal,
    writeVars: async () => {},
  }
}

function gitOk(stdout: string) {
  return { success: true, stdout, stderr: '', exitCode: 0, combinedOutput: stdout.trim() }
}

function gitFail(stderr: string, exitCode = 1) {
  return { success: false, stdout: '', stderr, exitCode, combinedOutput: stderr }
}

function installRebaseMockGit(resources: WorktreeTestResources, calls: string[]) {
  resources.rebaseGitRunner = async (_dir, args) => {
    const cmd = args.join(' ')
    calls.push(cmd)
    switch (cmd) {
      case 'rev-parse --git-path rebase-merge':
        return gitOk('/fake/worktree/.git/rebase-merge\n')
      case 'rev-parse --git-path rebase-apply':
        return gitOk('/fake/worktree/.git/rebase-apply\n')
      case 'rev-parse --git-path MERGE_HEAD':
        return gitOk('/fake/worktree/.git/MERGE_HEAD\n')
      case 'rev-parse --git-path CHERRY_PICK_HEAD':
        return gitOk('/fake/worktree/.git/CHERRY_PICK_HEAD\n')
      case 'rev-parse --abbrev-ref HEAD':
        return gitOk('mo/worktree-cleanup\n')
      case 'fetch origin master':
        return gitOk('From origin\n * branch            master     -> FETCH_HEAD')
      case 'rev-parse origin/master':
        return gitOk('base-sha\n')
      case 'status --porcelain':
        return gitOk('')
      case 'rev-parse HEAD': {
        const count = calls.filter((call) => call === 'rev-parse HEAD').length
        if (count === 1) return gitOk('before-sha\n')
        if (count === 2) return gitOk('rebased-sha\n')
        return gitOk('squashed-sha\n')
      }
      case 'rebase origin/master':
        return gitOk('Successfully rebased and updated refs/heads/mo/worktree-cleanup.')
      case 'reset --soft base-sha':
        return gitOk('')
      case 'commit -m Complete worktree cleanup':
        return gitOk('[mo/worktree-cleanup squashed-sha] Complete worktree cleanup')
      default:
        return gitFail(`unexpected git call: ${cmd}`, 1)
    }
  }
  resources.rebaseExistsChecker = () => false
}

describe('worktree cleanup before delivery', () => {
  it('commits agent leftovers before rebase and push', async (resources) => {
    const worktree = createFakeWorktree()
    installExecutorGit(resources, worktree)
    const connection = {
      async report() {
        return {}
      },
      async uploadArtifact() {
        throw new Error('uploadArtifact should not be called in cleanup delivery tests')
      },
    } as unknown as Pick<ServerConnection, 'uploadArtifact' | 'report'>
    const cleanupPrompts: string[] = []
    resources.cleanupAgentAction = async (_host, inputs) => {
      const prompt = String(inputs.prompt ?? '')
      cleanupPrompts.push(prompt)
      expect(prompt).toMatch(/do NOT start any new task work/i)
      expect(prompt).toMatch(/do NOT push to any remote/i)
      expect(prompt).toContain('src/agent-output.ts')
      commitCleanup(worktree, ['src/agent-output.ts'], 'cleanup-sha')
      return { output: { commitSha: 'cleanup-sha' } }
    }

    const registry = buildRegistry({
      'mohist/opencode': {
        run: async () => {
          worktree.untracked = ['src/agent-output.ts']
          return { output: null }
        },
        inputs: { prompt: { types: ['string', 'object'] } },
      },
      'mohist/rebase': rebaseAction,
      'mohist/push': pushAction,
    })
    const executor = buildExecutor(registry, worktree, connection)

    const agentResult = await executor.execute(buildWork(worktree), new AbortController().signal)
    expect(agentResult.status).toBe('completed')
    expect(agentResult.cleanupAttempts).toBe(1)
    expect(cleanupPrompts).toHaveLength(1)
    expect(worktree.cleanupCommits).toEqual([{ files: ['src/agent-output.ts'], sha: 'cleanup-sha' }])
    expect(worktree.untracked).toEqual([])

    const rebaseCalls: string[] = []
    installRebaseMockGit(resources, rebaseCalls)
    const rebaseResult = await callAction(rebaseAction, rebaseContext(worktree))
    const rebaseOutput = rebaseResult.output as Record<string, unknown>

    expect(rebaseResult.error).toBeUndefined()
    expect(rebaseCalls).toEqual([
      'rev-parse --git-path rebase-merge',
      'rev-parse --git-path rebase-apply',
      'fetch origin master',
      'rev-parse origin/master',
      'status --porcelain',
      'rev-parse HEAD',
      'rebase origin/master',
      'rev-parse HEAD',
      'reset --soft base-sha',
      'commit -m Complete worktree cleanup',
      'rev-parse HEAD',
      // Completion invariant probes after the squash commit.
      'rev-parse --git-path rebase-merge',
      'rev-parse --git-path rebase-apply',
      'rev-parse --git-path MERGE_HEAD',
      'rev-parse --git-path CHERRY_PICK_HEAD',
      'rev-parse HEAD',
      'rev-parse --abbrev-ref HEAD',
      'status --porcelain',
    ])
    expect(rebaseOutput).toMatchObject({
      kind: 'rebase',
      status: 'completed',
      baseBranch: 'master',
      remote: 'origin',
      squashed: true,
      squashedHeadSha: 'squashed-sha',
    })

    const pushCalls: { workDir: string; command: string }[] = []
    resources.pushGitRunner = async (workDir, args) => {
      const command = args.join(' ')
      pushCalls.push({ workDir, command })
      switch (command) {
        case 'rev-parse mo/worktree-cleanup':
          return gitOk('squashed-sha\n')
        case 'push origin mo/worktree-cleanup:master':
          return gitOk('To origin\n   base-sha..squashed-sha  mo/worktree-cleanup -> master')
        default:
          return gitFail(`unexpected git call: ${command}`, 1)
      }
    }

    const pushResult = await callAction(pushAction, pushContext(worktree))
    const pushOutput = pushResult.output as Record<string, unknown>
    expect(pushResult.error).toBeUndefined()
    expect(pushOutput).toMatchObject({
      kind: 'push',
      status: 'completed',
      source: 'mo/worktree-cleanup',
      target: 'master',
      landedCommit: 'squashed-sha',
      pushed: true,
      workDir: worktree.workDir,
    })
    expect(pushCalls).toEqual([
      { workDir: worktree.workDir, command: 'rev-parse mo/worktree-cleanup' },
      { workDir: worktree.workDir, command: 'push origin mo/worktree-cleanup:master' },
    ])
  })

  it('fails delivery after cleanup attempts leave the workspace dirty', async (resources) => {
    const worktree = createFakeWorktree()
    installExecutorGit(resources, worktree)
    const connection = {
      async report() {
        return {}
      },
      async uploadArtifact() {
        throw new Error('uploadArtifact should not be called in cleanup delivery tests')
      },
    } as unknown as Pick<ServerConnection, 'uploadArtifact' | 'report'>
    let attempt = 0
    resources.cleanupAgentAction = async (_host, inputs) => {
      attempt += 1
      const prompt = String(inputs.prompt ?? '')
      expect(prompt).toContain(`attempt ${attempt}`)
      return { output: null }
    }

    const registry = buildRegistry({
      'mohist/opencode': {
        run: async () => {
          worktree.untracked = ['src/never-clean.ts']
          return { output: null }
        },
        inputs: { prompt: { types: ['string', 'object'] } },
      },
      'mohist/rebase': rebaseAction,
      'mohist/push': pushAction,
    })
    const executor = buildExecutor(registry, worktree, connection)

    const agentResult = await executor.execute(
      buildWork(worktree, {
        variables: {
          workspace: { path: worktree.workDir, branch: worktree.branch, changeDir: null },
          project: { path: worktree.workDir },
          issue: { title: 'Worktree cleanup delivery', number: 42 },
          runner: { cleanup: { maxAttempts: 3 } },
        },
      }),
      new AbortController().signal,
    )

    expect(agentResult.status).toBe('failed')
    expect(agentResult.cleanupAttempts).toBe(3)
    expect(attempt).toBe(3)
    expect(agentResult.message).toMatch(/worktree dirty after 3 cleanup attempt/i)
    expect(agentResult.message).toMatch(/untracked=\[src\/never-clean\.ts\]/)
  })

  it('treats Pi-backed tasks as agent-backed for cleanup', async (resources) => {
    const worktree = createFakeWorktree()
    installExecutorGit(resources, worktree)
    const connection = {
      async report() {
        return {}
      },
      async uploadArtifact() {
        throw new Error('uploadArtifact should not be called in cleanup delivery tests')
      },
    } as unknown as Pick<ServerConnection, 'uploadArtifact' | 'report'>
    const cleanupPrompts: string[] = []
    resources.cleanupAgentAction = async (_host, inputs) => {
      cleanupPrompts.push(String(inputs.prompt ?? ''))
      commitCleanup(worktree, ['openspec/changes/issue-596/proposal.md'], 'pi-cleanup-sha')
      return { output: { commitSha: 'pi-cleanup-sha' } }
    }

    const registry = buildRegistry({
      'mohist/pi': {
        run: async () => {
          worktree.untracked = ['openspec/changes/issue-596/proposal.md']
          return { output: null }
        },
        inputs: { prompt: { types: ['string', 'object'] } },
      },
    })
    const executor = buildExecutor(registry, worktree, connection)

    const agentResult = await executor.execute(buildWork(worktree, { uses: 'mohist/pi' }), new AbortController().signal)

    expect(agentResult.status).toBe('completed')
    expect(agentResult.cleanupAttempts).toBe(1)
    expect(cleanupPrompts).toHaveLength(1)
    expect(worktree.cleanupCommits).toEqual([
      { files: ['openspec/changes/issue-596/proposal.md'], sha: 'pi-cleanup-sha' },
    ])
    expect(worktree.untracked).toEqual([])
  })
})
