import { AsyncLocalStorage } from 'node:async_hooks'
import { basename, join } from 'node:path'
import { describe, expect, it as vitestIt } from 'vitest'
import { issueWorkspacePath, withManagedWorkspacePath } from '../src/runtime/workspace.js'
import { WorkspaceRegistry } from '../src/runtime/workspace-registry.js'
import type { WorkspaceRegistryEntry } from '../src/runtime/workspace-registry.js'
import type { CommandLineOptions, CommandResult } from '../src/system/process.js'
import { currentRunnerFileSystem } from '../src/system/filesystem.js'
import { createTestTempDir } from './support/temp-dir.js'
import { MemoryFileSystem } from './support/memory-filesystem.js'
import { withTestRunnerResources } from './support/test-resources.js'

export interface CommandCall {
  command: string
  args: string[]
  cwd: string
  timeoutMs?: number
}

export class FakeGitRunner {
  readonly calls: CommandCall[] = []
  readonly remoteBranches = new Set([
    'master',
    'issue-symlink',
    'issue-parent-swap',
    'issue-mismatch',
    'issue-recover-registry',
  ])
  remoteUrl = 'https://example.test/mohist.git'
  remoteOriginResult: CommandResult | null = null
  cloneResult: CommandResult | null = null
  lsRemoteResult: CommandResult | null = null
  branchCheckoutResult: CommandResult | null = null
  failedCheckouts = 0
  beforeClone: (() => Promise<void>) | null = null
  beforeRemote: (() => Promise<void>) | null = null
  private readonly branches = new Map<string, Set<string>>()
  private readonly failures: Array<{ match: (args: string[]) => boolean; message: string; remaining: number }> = []
  /** Current branch per workspace; null means detached. */
  private readonly currentBranch = new Map<string, string | null>()
  /** Commit sha used for `rev-parse HEAD` (especially when detached). */
  private readonly detachedRef = new Map<string, string>()
  /** Porcelain output per workspace; '' (absent) means clean. */
  private readonly dirty = new Map<string, string>()
  /** Residual marker filenames per workspace, mirrored as `.git` files. */
  private readonly residualFiles = new Map<string, Set<string>>()
  /** Markers an abort must not clear (to simulate residual re-probe failures). */
  private readonly stubbornMarkers = new Map<string, Set<string>>()

  async setBranch(path: string, branch: string | null): Promise<void> {
    this.ensureGitDir(path)
    const preparing = this.preparingSibling(path)
    if (branch === null) {
      this.currentBranch.delete(path)
      this.currentBranch.delete(preparing)
    } else {
      this.currentBranch.set(path, branch)
      this.currentBranch.set(preparing, branch)
    }
  }

  async setDetached(path: string, ref: string): Promise<void> {
    this.ensureGitDir(path)
    const preparing = this.preparingSibling(path)
    this.currentBranch.delete(path)
    this.currentBranch.delete(preparing)
    this.detachedRef.set(path, ref)
    this.detachedRef.set(preparing, ref)
  }

  async setDirty(path: string, porcelain: string): Promise<void> {
    const preparing = this.preparingSibling(path)
    if (porcelain.trim() === '') {
      this.dirty.delete(path)
      this.dirty.delete(preparing)
    } else {
      this.dirty.set(path, porcelain)
      this.dirty.set(preparing, porcelain)
    }
  }

  async ensureBranch(path: string, branch: string): Promise<void> {
    this.ensureGitDir(path)
    let set = this.branches.get(path)
    if (!set) {
      set = new Set<string>()
      this.branches.set(path, set)
    }
    set.add(branch)
  }

  async setResidual(path: string, markers: string[]): Promise<void> {
    this.ensureGitDir(path)
    const preparing = this.preparingSibling(path)
    let set = this.residualFiles.get(path)
    let prepSet = this.residualFiles.get(preparing)
    if (!set) {
      set = new Set<string>()
      this.residualFiles.set(path, set)
    }
    if (!prepSet) {
      prepSet = new Set<string>()
      this.residualFiles.set(preparing, prepSet)
    }
    for (const marker of markers) {
      set.add(marker)
      prepSet.add(marker)
      await currentRunnerFileSystem().writeText(join(path, '.git', marker), 'ref\n')
    }
  }

  /** Markers that an abort command will not clear, simulating a re-probe failure. */
  async makeStubborn(path: string, markers: string[]): Promise<void> {
    const preparing = this.preparingSibling(path)
    let set = this.stubbornMarkers.get(path)
    let prepSet = this.stubbornMarkers.get(preparing)
    if (!set) {
      set = new Set<string>()
      this.stubbornMarkers.set(path, set)
    }
    if (!prepSet) {
      prepSet = new Set<string>()
      this.stubbornMarkers.set(preparing, prepSet)
    }
    for (const marker of markers) {
      set.add(marker)
      prepSet.add(marker)
      await currentRunnerFileSystem().writeText(join(path, '.git', marker), 'ref\n')
    }
  }

  residualMarkers(path: string): string[] {
    return [...(this.residualFiles.get(path) ?? [])]
  }

  /** Inject a transient command failure for `times` matching calls. */
  failCommand(match: (args: string[]) => boolean, message: string, times = 1): void {
    this.failures.push({ match, message, remaining: times })
  }

  private ensureGitDir(path: string): void {
    if (!currentRunnerFileSystem().exists(join(path, '.git'))) {
      currentRunnerFileSystem().ensureDir(join(path, '.git', 'info'))
    }
  }

  private preparingSibling(path: string): string {
    return `${path}.preparing`
  }

  private async clearResidual(path: string, markers: string[]): Promise<void> {
    const set = this.residualFiles.get(path)
    const stubborn = this.stubbornMarkers.get(path)
    if (!set) return
    for (const marker of markers) {
      if (stubborn?.has(marker)) continue
      set.delete(marker)
      try {
        await currentRunnerFileSystem().deleteFile(join(path, '.git', marker))
      } catch {
        // marker file already absent
      }
    }
  }

  async run(
    command: string,
    args: string[],
    cwd: string,
    _signal: AbortSignal,
    _env?: NodeJS.ProcessEnv,
    _options?: CommandLineOptions,
  ): Promise<CommandResult> {
    this.calls.push({ command, args: [...args], cwd, timeoutMs: _options?.timeoutMs })
    if (command !== 'git') throw new Error(`Unexpected command: ${command}`)

    const transient = this.failures.find((failure) => failure.match(args))
    if (transient) {
      transient.remaining -= 1
      if (transient.remaining <= 0) this.failures.splice(this.failures.indexOf(transient), 1)
      return commandResult(1, '', transient.message)
    }

    if (args[0] === 'ls-remote') {
      if (this.lsRemoteResult) return this.lsRemoteResult
      const branch = args.at(-1)
      return this.remoteBranches.has(branch ?? '')
        ? commandResult(0, `fake-sha\trefs/heads/${branch}\n`)
        : commandResult(0)
    }

    if (args[0] === 'clone') {
      if (this.beforeClone) {
        const beforeClone = this.beforeClone
        this.beforeClone = null
        await beforeClone()
      }
      const workspacePath = args.at(-1)
      if (!workspacePath) throw new Error('git clone needs a destination')
      if (this.cloneResult) {
        await currentRunnerFileSystem().ensureDir(workspacePath)
        return this.cloneResult
      }
      await currentRunnerFileSystem().ensureDir(join(workspacePath, '.git', 'info'))
      await currentRunnerFileSystem().writeText(join(workspacePath, 'README.md'), 'base\n')
      this.branches.set(workspacePath, new Set())
      this.currentBranch.set(workspacePath, null)
      this.detachedRef.set(workspacePath, 'fake-base-sha')
      this.dirty.delete(workspacePath)
      this.residualFiles.delete(workspacePath)
      return commandResult(0)
    }

    if (args[0] !== '-C' || !args[1]) throw new Error(`Unexpected git arguments: ${args.join(' ')}`)
    const workspacePath = args[1]
    const gitArgs = args.slice(2)
    let branches = this.branches.get(workspacePath)
    if (!branches) {
      // A prior clone + atomic rename (prepare) leaves all branch/head state
      // under the `.preparing` sibling; adopt it the first time the final
      // stable path is addressed so `hasRunBranch` and the health probes see
      // the same worktree the clone produced. Adoption never overwrites state
      // a test explicitly set on the final path.
      const preparing = `${workspacePath}.preparing`
      if (this.branches.has(preparing)) {
        const preparedBranches = this.branches.get(preparing)!
        for (const branch of preparedBranches) {
          if (!branches) {
            branches = new Set<string>()
            this.branches.set(workspacePath, branches)
          }
          branches.add(branch)
        }
        this.branches.delete(preparing)
        if (!this.currentBranch.has(workspacePath) && this.currentBranch.has(preparing)) {
          this.currentBranch.set(workspacePath, this.currentBranch.get(preparing)!)
        }
        this.currentBranch.delete(preparing)
        if (!this.detachedRef.has(workspacePath) && this.detachedRef.has(preparing)) {
          this.detachedRef.set(workspacePath, this.detachedRef.get(preparing)!)
        }
        this.detachedRef.delete(preparing)
        if (!this.dirty.has(workspacePath) && this.dirty.has(preparing)) {
          this.dirty.set(workspacePath, this.dirty.get(preparing)!)
        }
        this.dirty.delete(preparing)
        if (!this.residualFiles.has(workspacePath) && this.residualFiles.has(preparing)) {
          this.residualFiles.set(workspacePath, this.residualFiles.get(preparing)!)
        }
        this.residualFiles.delete(preparing)
        if (!this.stubbornMarkers.has(workspacePath) && this.stubbornMarkers.has(preparing)) {
          this.stubbornMarkers.set(workspacePath, this.stubbornMarkers.get(preparing)!)
        }
        this.stubbornMarkers.delete(preparing)
      } else if (!currentRunnerFileSystem().exists(join(workspacePath, '.git'))) {
        throw new Error(`Unknown fake workspace: ${workspacePath}`)
      }
      branches = this.branches.get(workspacePath) ?? new Set<string>()
      this.branches.set(workspacePath, branches)
    }

    if (gitArgs[0] === 'remote' && gitArgs[1] === 'get-url' && gitArgs[2] === 'origin') {
      if (this.beforeRemote) {
        const beforeRemote = this.beforeRemote
        this.beforeRemote = null
        await beforeRemote()
      }
      if (this.remoteOriginResult) {
        return {
          ...this.remoteOriginResult,
          stdout: this.remoteOriginResult.stdout.replaceAll('<workspace>', workspacePath),
          stderr: this.remoteOriginResult.stderr.replaceAll('<workspace>', workspacePath),
        }
      }
      return commandResult(0, `${this.remoteUrl}\n`)
    }

    if (gitArgs[0] === 'rev-parse' && gitArgs[1] === '--verify') {
      const branch = gitArgs[2]?.replace('refs/heads/', '')
      return branches.has(branch ?? '')
        ? commandResult(0, 'fake-sha\n')
        : commandResult(1, '', `fatal: Needed a single revision`)
    }

    if (gitArgs[0] === 'rev-parse' && gitArgs[1] === '--abbrev-ref') {
      const branch = this.currentBranch.get(workspacePath) ?? null
      return commandResult(0, branch === null ? 'HEAD\n' : `${branch}\n`)
    }

    if (gitArgs[0] === 'rev-parse' && gitArgs[1] === 'HEAD') {
      const ref = this.detachedRef.get(workspacePath) ?? this.currentBranch.get(workspacePath) ?? 'fake-head-sha'
      return commandResult(0, `${ref}\n`)
    }

    if (gitArgs[0] === 'status' && gitArgs[1] === '--porcelain') {
      return commandResult(0, this.dirty.get(workspacePath) ?? '')
    }

    if (gitArgs[0] === 'show-ref' && gitArgs[1] === '--verify' && gitArgs[2] === '--quiet') {
      const remoteBranch = gitArgs[3]?.replace('refs/remotes/origin/', '')
      return this.remoteBranches.has(remoteBranch ?? '') ? commandResult(0) : commandResult(1)
    }

    if (gitArgs[0] === 'checkout' && (gitArgs[1] === '-b' || gitArgs[1] === '-B')) {
      if (this.branchCheckoutResult) return this.branchCheckoutResult
      const branch = gitArgs[2]
      if (!branch) throw new Error('git checkout -b needs a branch')
      branches.add(branch)
      this.currentBranch.set(workspacePath, branch)
      this.detachedRef.delete(workspacePath)
      return commandResult(0)
    }

    if (gitArgs[0] === 'checkout') {
      if (this.failedCheckouts > 0) {
        this.failedCheckouts -= 1
        return commandResult(1, '', 'checkout blocked by unfinished rebase')
      }
      const branch = gitArgs[1]
      if (branch && !branches.has(branch)) {
        return commandResult(1, '', `error: pathspec '${branch}' did not match any file(s) known to git`)
      }
      if (branch) {
        this.currentBranch.set(workspacePath, branch)
        this.detachedRef.delete(workspacePath)
      }
      return commandResult(0)
    }

    if (gitArgs[0] === 'rebase' && gitArgs[1] === '--abort') {
      await this.clearResidual(workspacePath, ['rebase-merge', 'rebase-apply'])
      return commandResult(0)
    }

    if (gitArgs[0] === 'merge' && gitArgs[1] === '--abort') {
      await this.clearResidual(workspacePath, ['MERGE_HEAD'])
      return commandResult(0)
    }

    if (gitArgs[0] === 'cherry-pick' && gitArgs[1] === '--abort') {
      await this.clearResidual(workspacePath, ['CHERRY_PICK_HEAD'])
      return commandResult(0)
    }

    if (gitArgs[0] === 'reset' && gitArgs[1] === '--hard') {
      this.dirty.delete(workspacePath)
      return commandResult(0)
    }

    if (gitArgs[0] === 'clean' && gitArgs[1] === '-fd') {
      this.dirty.delete(workspacePath)
      return commandResult(0)
    }

    if (gitArgs[0] === 'rebase' || gitArgs[0] === 'merge' || gitArgs[0] === 'cherry-pick') {
      return commandResult(0)
    }

    throw new Error(`Unexpected git arguments: ${args.join(' ')}`)
  }

  commandArgs() {
    return this.calls.map((call) => call.args)
  }
}

interface WorkspaceTestContext {
  fileSystem: MemoryFileSystem
  gitRunner: FakeGitRunner
}

const workspaceTestContext = new AsyncLocalStorage<WorkspaceTestContext>()

export function workspaceContext(): WorkspaceTestContext {
  const context = workspaceTestContext.getStore()
  if (!context) throw new Error('workspace test resource context is not active')
  return context
}

export const fileSystem = new Proxy({} as MemoryFileSystem, {
  get(_target, property) {
    const value = Reflect.get(workspaceContext().fileSystem, property, workspaceContext().fileSystem)
    return typeof value === 'function' ? value.bind(workspaceContext().fileSystem) : value
  },
})

export const gitRunner = new Proxy({} as FakeGitRunner, {
  get(_target, property) {
    const value = Reflect.get(workspaceContext().gitRunner, property, workspaceContext().gitRunner)
    return typeof value === 'function' ? value.bind(workspaceContext().gitRunner) : value
  },
  set(_target, property, value) {
    const runner = workspaceContext().gitRunner
    Reflect.set(runner, property, value, runner)
    return true
  },
})

async function withWorkspaceTestResources(body: () => unknown): Promise<void> {
  const context: WorkspaceTestContext = {
    fileSystem: new MemoryFileSystem(),
    gitRunner: new FakeGitRunner(),
  }
  const commandRunner = {
    run: (
      command: string,
      args: string[],
      cwd: string,
      signal: AbortSignal,
      env?: NodeJS.ProcessEnv,
      options?: unknown,
    ) => context.gitRunner.run(command, args, cwd, signal, env, options as CommandLineOptions | undefined),
  }
  await withTestRunnerResources(
    async () => {
      await workspaceTestContext.run(context, async () => await body())
    },
    { commandRunner, fileSystem: context.fileSystem },
  )
}

export async function withWorkspaceFileSystem<T>(fileSystem: MemoryFileSystem, body: () => Promise<T>): Promise<T> {
  const current = workspaceContext()
  const commandRunner = {
    run: (
      command: string,
      args: string[],
      cwd: string,
      signal: AbortSignal,
      env?: NodeJS.ProcessEnv,
      options?: unknown,
    ) => current.gitRunner.run(command, args, cwd, signal, env, options as CommandLineOptions | undefined),
  }
  return await withTestRunnerResources(
    async () => await workspaceTestContext.run({ fileSystem, gitRunner: current.gitRunner }, body),
    { commandRunner, fileSystem },
  )
}

export const it = Object.assign(
  (name: string, body: () => unknown) => vitestIt(name, () => withWorkspaceTestResources(body)),
  {
    each: vitestIt.each.bind(vitestIt),
    runIf: (condition: boolean) => (name: string, body: () => unknown) =>
      vitestIt.runIf(condition)(name, () => withWorkspaceTestResources(body)),
  },
) as typeof vitestIt

export function commandResult(exitCode = 0, stdout = '', stderr = ''): CommandResult {
  return { exitCode, stdout, stderr }
}

export function work(workflowRunId: string, baseBranch = 'master') {
  return {
    workflowRunId,
    workId: 'proposal.1',
    workType: 'task',
    uses: 'mohist/opencode',
    variables: {
      issue: { number: 9 },
      repository: { name: 'master', gitUrl: 'https://example.test/mohist.git', baseBranch },
    },
  }
}

export function managedPath(path: string) {
  return path
}

export function managedPathPattern(path: string) {
  return path.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
}

export function cleanupEntry(workspacePath: string, workflowRunId: string): WorkspaceRegistryEntry {
  return {
    issueNumber: 9,
    workflowRunId,
    workspacePath,
    runBranch: `mohist/run-${workflowRunId}`,
    phase: 'eligible',
    materializedAt: '2026-01-01T00:00:00.000Z',
    terminalAt: '2026-01-02T00:00:00.000Z',
  }
}
